using CoreMotion;
using Foundation;
using HealthKit;
using LibationMobile.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.iOS;

/// <summary>
/// Core Motion's history: activity (stationary, walking, running, cycling, automotive) and steps over a stretch of time,
/// read after it, from what the motion coprocessor recorded anyway. Asks for Motion and Fitness permission once. Also
/// head movement from headphones that report it, while asleep is being watched for, and sleep from Apple Health.
/// </summary>
public sealed class AppleListeningContext : IListeningContext
{
	private readonly CMMotionActivityManager activity = new();
	private readonly CMPedometer pedometer = new();
	private CMHeadphoneMotionManager? head;
	private CMAttitude? headRest;
	// UTC ticks, 0 for never: written on the motion queue, read on the UI thread.
	private long headLastMoved;
	private long headGestured;
	/// <summary>
	/// Head readings arrive about 25 times a second for as long as a book plays; handled off the main thread, they
	/// do not wake the app's UI each time.
	/// </summary>
	private readonly NSOperationQueue headQueue = new() { MaxConcurrentOperationCount = 1, Name = "Head movement" };

	private static DateTimeOffset? FromTicks(ref long ticks) => Volatile.Read(ref ticks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero).ToLocalTime() : null;
	private readonly HKHealthStore? health = HKHealthStore.IsHealthDataAvailable ? new HKHealthStore() : null;
	private static readonly HKCategoryType? SleepType = HKCategoryType.Create(HKCategoryTypeIdentifier.SleepAnalysis);

	/// <summary>A turn of the head larger than this, in radians (about 8 degrees), is movement.</summary>
	private const double HEAD_MOVED = 0.14;
	/// <summary>
	/// Turning faster than this, in radians a second, is a nod or a shake: deliberate, where a head settling into sleep
	/// drifts slowly. About 70 degrees a second; a nod runs at two or three times that.
	/// </summary>
	private const double HEAD_GESTURE = 1.2;

	/// <summary>Whether head movement may be used at all: the listener's setting.</summary>
	public bool UseHead { get; set; } = true;

	public DateTimeOffset? HeadLastMoved => head is { DeviceMotionActive: true } ? FromTicks(ref headLastMoved) : null;

	public DateTimeOffset? HeadGestured => head is { DeviceMotionActive: true } ? FromTicks(ref headGestured) : null;

	// Battery monitoring is turned on at launch (AppDelegate); without it, iOS reports -1.
	public (int Percent, bool Charging)? Battery => UIKit.UIDevice.CurrentDevice is { BatteryLevel: >= 0 } device
		? ((int)Math.Round(device.BatteryLevel * 100), device.BatteryState is UIKit.UIDeviceBatteryState.Charging or UIKit.UIDeviceBatteryState.Full)
		: null;

	public void WatchHead(bool on)
	{
		on &= UseHead;
		try
		{
			if (on && head is null)
			{
				head = new CMHeadphoneMotionManager();
				if (!head.DeviceMotionAvailable)
				{
					head = null;
					return;
				}
				headRest = null;
				Volatile.Write(ref headLastMoved, DateTime.UtcNow.Ticks);
				head.StartDeviceMotionUpdates(headQueue, (motion, error) =>
				{
					if (motion?.Attitude is not { } attitude)
						return;
					var rate = motion.RotationRate;
					if (Math.Sqrt(rate.x * rate.x + rate.y * rate.y + rate.z * rate.z) > HEAD_GESTURE)
						Volatile.Write(ref headGestured, DateTime.UtcNow.Ticks);
					if (headRest is null)
					{
						headRest = attitude;
						return;
					}
					// How far the head has turned since it last settled.
					var turned = attitude.Copy() as CMAttitude;
					if (turned is null)
						return;
					turned.MultiplyByInverseOfAttitude(headRest);
					if (Math.Abs(turned.Roll) > HEAD_MOVED || Math.Abs(turned.Pitch) > HEAD_MOVED || Math.Abs(turned.Yaw) > HEAD_MOVED)
					{
						Volatile.Write(ref headLastMoved, DateTime.UtcNow.Ticks);
						headRest = attitude;
					}
				});
			}
			else if (!on && head is not null)
			{
				head.StopDeviceMotionUpdates();
				head = null;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Head movement not followed: {ex.Message}");
			head = null;
		}
	}

	public async Task<double?> StillShareAsync(DateTimeOffset from, DateTimeOffset to)
	{
		if (!CMMotionActivityManager.IsActivityAvailable || to <= from)
			return null;
		try
		{
			var records = await activity.QueryActivityAsync((NSDate)from.UtcDateTime, (NSDate)to.UtcDateTime, NSOperationQueue.MainQueue);
			// No new record means the last one still holds: ask from a little earlier to have it.
			if (records is null || records.Length == 0)
				records = await activity.QueryActivityAsync((NSDate)from.AddHours(-6).UtcDateTime, (NSDate)to.UtcDateTime, NSOperationQueue.MainQueue);
			var shares = Shares(records, from, to);
			return shares.Count == 0 ? null : shares.GetValueOrDefault("still");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Stillness not read: {ex.Message}");
			return null;
		}
	}

	public string MotionPermission => !CMMotionActivityManager.IsActivityAvailable ? "unavailable"
		: CMMotionActivityManager.AuthorizationStatus switch
		{
			CMAuthorizationStatus.Authorized => "allowed",
			CMAuthorizationStatus.NotDetermined => "not asked",
			_ => "denied",
		};

	public void OpenAppSettings()
	{
		if (NSUrl.FromString(UIKit.UIApplication.OpenSettingsUrlString) is { } url)
			UIKit.UIApplication.SharedApplication.OpenUrl(url, new UIKit.UIApplicationOpenUrlOptions(), null);
	}

	public async Task<bool> RequestMotionAsync()
	{
		if (!CMMotionActivityManager.IsActivityAvailable)
			return false;
		if (CMMotionActivityManager.AuthorizationStatus == CMAuthorizationStatus.Authorized)
			return true;
		try
		{
			// A query is what asks; Motion & Fitness then covers the headphones' movement too.
			await activity.QueryActivityAsync((NSDate)DateTime.UtcNow.AddMinutes(-1), (NSDate)DateTime.UtcNow, NSOperationQueue.MainQueue);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Motion not allowed: {ex.Message}");
		}
		return CMMotionActivityManager.AuthorizationStatus == CMAuthorizationStatus.Authorized;
	}

	public async Task<bool> ConnectHealthAsync()
	{
		if (health is null || SleepType is null)
			return false;
		try
		{
			var (ok, error) = await health.RequestAuthorizationToShareAsync(new NSSet(), new NSSet(SleepType));
			if (error is not null)
				Console.WriteLine($"Apple Health: {error.LocalizedDescription}");
			// iOS does not say whether reading was allowed, only that the question was answered.
			return ok;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Apple Health not connected: {ex.Message}");
			return false;
		}
	}

	public Task<IReadOnlyList<SleepSpan>> SleepAsync(DateTimeOffset from, DateTimeOffset to)
	{
		var done = new TaskCompletionSource<IReadOnlyList<SleepSpan>>();
		if (health is null || SleepType is null)
		{
			done.SetResult([]);
			return done.Task;
		}
		try
		{
			var predicate = HKQuery.GetPredicateForSamples((NSDate)from.UtcDateTime, (NSDate)to.UtcDateTime, HKQueryOptions.None);
			var byStart = new NSSortDescriptor(HKSample.SortIdentifierStartDate, ascending: true);
			var query = new HKSampleQuery(SleepType, predicate, 0, [byStart], (_, results, error) =>
			{
				var spans = new List<SleepSpan>();
				foreach (var sample in (results ?? []).OfType<HKCategorySample>())
				{
					// Asleep in any stage; not "in bed" or "awake".
					if ((HKCategoryValueSleepAnalysis)(int)sample.Value is HKCategoryValueSleepAnalysis.InBed or HKCategoryValueSleepAnalysis.Awake)
						continue;
					spans.Add(new SleepSpan((DateTime)sample.StartDate, (DateTime)sample.EndDate, sample.SourceRevision?.Source?.Name));
				}
				done.TrySetResult(Merge(spans));
			});
			health.ExecuteQuery(query);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Sleep not read: {ex.Message}");
			done.TrySetResult([]);
		}
		return done.Task;
	}

	/// <summary>Stages of one sleep (core, deep, REM) as one stretch: gaps under 20 minutes are joined.</summary>
	private static IReadOnlyList<SleepSpan> Merge(List<SleepSpan> spans)
	{
		var merged = new List<SleepSpan>();
		foreach (var span in spans.OrderBy(s => s.From))
		{
			if (merged.Count > 0 && span.From - merged[^1].To < TimeSpan.FromMinutes(20))
				merged[^1] = merged[^1] with { To = Max(merged[^1].To, span.To) };
			else
				merged.Add(span);
		}
		return merged;
	}

	public async Task<ActivityContext?> ActivityAsync(DateTimeOffset from, DateTimeOffset to)
	{
		if (!CMMotionActivityManager.IsActivityAvailable || to <= from)
			return null;
		try
		{
			var start = (NSDate)from.UtcDateTime;
			var end = (NSDate)to.UtcDateTime;
			var records = await activity.QueryActivityAsync(start, end, NSOperationQueue.MainQueue);
			var shares = Shares(records, from, to);
			int? steps = null;
			if (CMPedometer.IsStepCountingAvailable)
			{
				var data = await pedometer.QueryPedometerDataAsync(start, end);
				steps = data?.NumberOfSteps?.Int32Value;
			}
			var summary = string.Join(" · ", shares.Where(s => s.Value >= 0.05).OrderByDescending(s => s.Value).Select(s => $"{s.Key} {s.Value:P0}"));
			return new ActivityContext(summary.Length > 0 ? summary : "unknown", steps);
		}
		catch (Exception ex)
		{
			// Permission refused, or no history: the session is logged without it.
			Console.WriteLine($"Activity not read: {ex.Message}");
			return null;
		}
	}

	/// <summary>How much of the stretch each activity covered. Each record lasts until the next one starts.</summary>
	private static Dictionary<string, double> Shares(CMMotionActivity[]? records, DateTimeOffset from, DateTimeOffset to)
	{
		var shares = new Dictionary<string, double>();
		if (records is null || records.Length == 0)
			return shares;
		var total = (to - from).TotalSeconds;
		for (var i = 0; i < records.Length; i++)
		{
			var begins = Max((DateTimeOffset)(DateTime)records[i].StartDate, from);
			var ends = i + 1 < records.Length ? Min((DateTimeOffset)(DateTime)records[i + 1].StartDate, to) : to;
			if (ends <= begins)
				continue;
			var name = Name(records[i]);
			shares[name] = shares.GetValueOrDefault(name) + (ends - begins).TotalSeconds / total;
		}
		return shares;
	}

	private static string Name(CMMotionActivity a)
		=> a.Automotive ? "driving" : a.Cycling ? "cycling" : a.Running ? "running" : a.Walking ? "walking" : a.Stationary ? "still" : "unknown";

	private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
	private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
