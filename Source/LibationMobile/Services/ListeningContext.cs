using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>What the listener was doing over a stretch of time: shares of each activity, and steps.</summary>
/// <param name="Summary">Such as "walking 62% · stationary 38%".</param>
public record ActivityContext(string Summary, int? Steps);

/// <summary>
/// The phone's own record of what the listener was doing, asked for after the fact, so no sensor runs to collect it.
/// On iOS, the motion coprocessor classifies activity and counts steps all the time and keeps days of history.
/// </summary>
public interface IListeningContext
{
	Task<ActivityContext?> ActivityAsync(DateTimeOffset from, DateTimeOffset to);

	/// <summary>How much of a stretch the phone lay still, 0 to 1, from the motion record; null if unknown.</summary>
	Task<double?> StillShareAsync(DateTimeOffset from, DateTimeOffset to);

	/// <summary>
	/// Follow head movement through headphones that report it (AirPods Pro, Max, 3rd generation and later), while asleep
	/// is being watched for. Off again when not needed, as the headphones then send nothing.
	/// </summary>
	void WatchHead(bool on);

	/// <summary>Whether head movement may be used at all, from the listener's settings. Off stops it at once.</summary>
	bool UseHead { get; set; }

	/// <summary>When the head last moved, while headphones that report it are worn and watched; null otherwise.</summary>
	DateTimeOffset? HeadLastMoved { get; }

	/// <summary>When the head last nodded or shook (turned quickly), while watched; null otherwise.</summary>
	DateTimeOffset? HeadGestured { get; }

	/// <summary>The phone's battery, 0 to 100, and whether it is charging; null where it cannot be read.</summary>
	(int Percent, bool Charging)? Battery { get; }

	/// <summary>
	/// Ask for Motion and Fitness, which covers the phone's activity and the headphones' head movement, as the listener
	/// turns on something that needs it, rather than later, perhaps while asleep. True if allowed.
	/// </summary>
	Task<bool> RequestMotionAsync();

	/// <summary>Motion &amp; Fitness: "allowed", "denied", "not asked" or "unavailable".</summary>
	string MotionPermission { get; }

	/// <summary>Open this app's page in the iPhone's Settings, where its access can be changed.</summary>
	void OpenAppSettings();

	/// <summary>Ask once to read sleep from Apple Health, where a watch's app (Garmin Connect, Apple Watch) writes it.</summary>
	Task<bool> ConnectHealthAsync();

	/// <summary>Stretches recorded as asleep in Apple Health that overlap this one, oldest first. Empty without permission.</summary>
	Task<IReadOnlyList<SleepSpan>> SleepAsync(DateTimeOffset from, DateTimeOffset to);
}

/// <summary>A stretch of sleep, and what recorded it, such as "Connect" (Garmin) or "Apple Watch".</summary>
public record SleepSpan(DateTimeOffset From, DateTimeOffset To, string? Source);

public static class ListeningContext
{
	/// <summary>Set by the platform head. Null where there is none.</summary>
	public static IListeningContext? Platform { get; set; }
}
