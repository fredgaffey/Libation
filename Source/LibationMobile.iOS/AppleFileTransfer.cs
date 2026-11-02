using Foundation;
using LibationMobile.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UIKit;

namespace LibationMobile.iOS;

/// <summary>
/// Downloads through a background URL session: iOS fetches the file itself, carrying on while the app is in the
/// background or suspended, and relaunching it when the download ends if it was closed meanwhile.
/// </summary>
public sealed class AppleFileTransfer : NSUrlSessionDownloadDelegate, IFileTransfer
{
	public const string SESSION_ID = "io.github.freddygaffey.libation.downloads";

	/// <summary>A finished download whose file has been moved into place, waiting for the app to decrypt it.</summary>
	private static string CompleteMarker(string path) => path + ".complete";

	/// <summary>
	/// With the app on screen, how long a download may go without a byte before the system's downloader is given up on
	/// for the app's own. iOS's background downloader can sit at nothing, with no error, until the phone restarts.
	/// </summary>
	private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan StallCheck = TimeSpan.FromSeconds(5);

	private sealed class Transfer(string path, Action<double> progress)
	{
		public string Path { get; } = path;
		public Action<double> Progress { get; set; } = progress;
		public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	/// <summary>NSURLSessionDownloadTaskResumeData: where an interrupted download's state comes in an error.</summary>
	private static readonly NSString ResumeDataKey = new("NSURLSessionDownloadTaskResumeData");

	/// <summary>Reports of downloads stopped while the app was closed arrive just after the session reconnects.</summary>
	private static readonly TimeSpan ReconnectSettle = TimeSpan.FromSeconds(2);
	private readonly DateTime created = DateTime.UtcNow;

	private readonly ConcurrentDictionary<string, Transfer> transfers = new();
	private readonly NSUrlSession session;

	/// <summary>Given by iOS when it wakes the app for this session's events; called once they are handled.</summary>
	public static Action? BackgroundEventsHandled { get; set; }

	public AppleFileTransfer()
	{
		var configuration = NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration(SESSION_ID);
		configuration.SessionSendsLaunchEvents = true;
		configuration.Discretionary = false;
		session = NSUrlSession.FromConfiguration(configuration, (INSUrlSessionDelegate)this, null);
	}

	public async Task DownloadAsync(Uri url, string path, string userAgent, Action<double> progress, CancellationToken token)
	{
		// Finished while the app was closed: the file is already in place.
		if (File.Exists(CompleteMarker(path)) && File.Exists(path))
		{
			File.Delete(CompleteMarker(path));
			progress(1);
			return;
		}

		var transfer = transfers.AddOrUpdate(path, _ => new Transfer(path, progress), (_, existing) =>
		{
			existing.Progress = progress;
			return existing;
		});

		try
		{
			var task = await FindOrStartAsync(url, path, userAgent);
			if (task.BytesExpectedToReceive > 0)
				progress((double)task.BytesReceived / task.BytesExpectedToReceive);
			_ = WatchForStallAsync(task, transfer, token);

			// A cancel keeps nothing: the person asked for the download to stop.
			using var registration = token.Register(() =>
			{
				task.Cancel();
				File.Delete(ResumeDataPath(path));
				transfer.Completion.TrySetCanceled(token);
			});
			try
			{
				await transfer.Completion.Task;
			}
			catch (IOException ex) when (ex is not DownloadStalledException && task.TaskDescription == ResumedDescription(path) && !token.IsCancellationRequested)
			{
				// The saved state was refused, often because the link in it has expired: start again with the new link.
				Console.WriteLine($"Background download could not resume, starting again: {System.IO.Path.GetFileName(path)}");
				File.Delete(ResumeDataPath(path));
				var restarted = transfers.AddOrUpdate(path, _ => new Transfer(path, progress), (_, _) => new Transfer(path, progress));
				var fresh = Start(url, path, userAgent);
				_ = WatchForStallAsync(fresh, restarted, token);
				using var freshRegistration = token.Register(() =>
				{
					fresh.Cancel();
					restarted.Completion.TrySetCanceled(token);
				});
				await restarted.Completion.Task;
			}
		}
		finally
		{
			transfers.TryRemove(path, out _);
			File.Delete(CompleteMarker(path));
		}
	}

	/// <summary>
	/// Give up on a download that has received nothing for <see cref="StallLimit"/> while the app is on screen, so the
	/// app downloads it itself. Time in the background does not count: iOS may hold downloads back then.
	/// </summary>
	private static async Task WatchForStallAsync(NSUrlSessionDownloadTask task, Transfer transfer, CancellationToken token)
	{
		var lastBytes = task.BytesReceived;
		var quietSince = DateTime.UtcNow;
		while (!transfer.Completion.Task.IsCompleted && !token.IsCancellationRequested)
		{
			await Task.WhenAny(transfer.Completion.Task, Task.Delay(StallCheck, CancellationToken.None));
			if (transfer.Completion.Task.IsCompleted || token.IsCancellationRequested)
				return;
			var bytes = task.BytesReceived;
			if (bytes != lastBytes || !await IsOnScreenAsync())
			{
				lastBytes = bytes;
				quietSince = DateTime.UtcNow;
				continue;
			}
			if (DateTime.UtcNow - quietSince < StallLimit)
				continue;
			Console.WriteLine($"Background download stalled at {bytes} bytes, downloading in the app: {System.IO.Path.GetFileName(transfer.Path)}");
			transfer.Completion.TrySetException(new DownloadStalledException());
			task.Cancel();
			return;
		}
	}

	private static Task<bool> IsOnScreenAsync()
	{
		var onScreen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		UIApplication.SharedApplication.BeginInvokeOnMainThread(
			() => onScreen.TrySetResult(UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active));
		return onScreen.Task;
	}

	/// <summary>
	/// The download for <paramref name="path"/> already held by the system, else one carried on from where an
	/// earlier one stopped, else a new one.
	/// </summary>
	private async Task<NSUrlSessionDownloadTask> FindOrStartAsync(Uri url, string path, string userAgent)
	{
		// Let a stopped download's saved state land first, rather than start it again from nothing.
		var settle = created + ReconnectSettle - DateTime.UtcNow;
		if (settle > TimeSpan.Zero)
			await Task.Delay(settle);
		var live = (await session.GetAllTasksAsync()).OfType<NSUrlSessionDownloadTask>()
			.Where(t => IsFor(t, path) && t.State is NSUrlSessionTaskState.Running or NSUrlSessionTaskState.Suspended)
			.ToList();
		if (live.Count > 0)
		{
			// Keep the one furthest along; any other would race it to the same file.
			var keep = live.MaxBy(t => t.BytesReceived)!;
			foreach (var extra in live.Where(t => t != keep))
				extra.Cancel();
			if (keep.State == NSUrlSessionTaskState.Suspended)
				keep.Resume();
			Console.WriteLine($"Background download reattached at {keep.BytesReceived} bytes: {System.IO.Path.GetFileName(path)}");
			return keep;
		}

		if (File.Exists(ResumeDataPath(path)))
		{
			try
			{
				var resumeData = NSData.FromFile(ResumeDataPath(path));
				var resumed = session.CreateDownloadTaskFromResumeData(resumeData, null);
				resumed.TaskDescription = ResumedDescription(path);
				resumed.Resume();
				Console.WriteLine($"Background download resumed: {System.IO.Path.GetFileName(path)}");
				return resumed;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Background download resume data unusable: {ex.Message}");
				File.Delete(ResumeDataPath(path));
			}
		}

		return Start(url, path, userAgent);
	}

	private NSUrlSessionDownloadTask Start(Uri url, string path, string userAgent)
	{
		// A background download cannot append to a part-file left by an earlier, in-app download.
		File.Delete(path);
		var request = new NSMutableUrlRequest(new NSUrl(url.AbsoluteUri));
		request["User-Agent"] = userAgent;
		var task = session.CreateDownloadTask(request);
		task.TaskDescription = path;
		task.Resume();
		Console.WriteLine($"Background download started: {System.IO.Path.GetFileName(path)}");
		return task;
	}

	/// <summary>Where the system's saved state for an interrupted download is kept, so it can carry on rather than restart.</summary>
	private static string ResumeDataPath(string path) => path + ".resume";

	/// <summary>Marks a task carried on from saved state, so a refusal of that state can fall back to a fresh start.</summary>
	private static string ResumedDescription(string path) => path + "|resumed";

	private static string? PathOf(NSUrlSessionTask task)
		=> task.TaskDescription is string description ? description.Split('|')[0] : null;

	private static bool IsFor(NSUrlSessionTask task, string path) => PathOf(task) == path;

	public override void DidWriteData(NSUrlSession session, NSUrlSessionDownloadTask downloadTask, long bytesWritten, long totalBytesWritten, long totalBytesExpectedToWrite)
	{
		if (totalBytesExpectedToWrite > 0 && PathOf(downloadTask) is string path && transfers.TryGetValue(path, out var transfer))
			transfer.Progress((double)totalBytesWritten / totalBytesExpectedToWrite);
	}

	public override void DidResume(NSUrlSession session, NSUrlSessionDownloadTask downloadTask, long resumeFileOffset, long expectedTotalBytes)
	{
		if (expectedTotalBytes > 0 && PathOf(downloadTask) is string path && transfers.TryGetValue(path, out var transfer))
			transfer.Progress((double)resumeFileOffset / expectedTotalBytes);
	}

	public override void DidFinishDownloading(NSUrlSession session, NSUrlSessionDownloadTask downloadTask, NSUrl location)
	{
		// The system deletes the file when this returns, so it has to be moved now.
		if (PathOf(downloadTask) is not string path || location.Path is not string temporary)
			return;
		File.Delete(ResumeDataPath(path));
		if (downloadTask.Response is NSHttpUrlResponse { StatusCode: < 200 or >= 300 } response)
		{
			Fail(path, new IOException($"The download failed with status {response.StatusCode}."));
			return;
		}
		try
		{
			File.Move(temporary, path, overwrite: true);
			// For a download the app is not waiting on, because it was closed: picked up when it next runs.
			File.WriteAllText(CompleteMarker(path), "");
			if (transfers.TryGetValue(path, out var transfer))
				transfer.Completion.TrySetResult();
		}
		catch (IOException ex)
		{
			Fail(path, ex);
		}
	}

	public override void DidCompleteWithError(NSUrlSession session, NSUrlSessionTask task, NSError? error)
	{
		if (error is null || PathOf(task) is not string path)
			return;
		// Stopped by the system, such as on a restart or the app being swiped away: keep what it has so far, to carry on later.
		if (error.UserInfo?[ResumeDataKey] is NSData resumeData)
		{
			resumeData.Save(ResumeDataPath(path), atomically: true);
			Console.WriteLine($"Background download stopped at {task.BytesReceived} bytes, kept to resume: {System.IO.Path.GetFileName(path)}");
		}
		Fail(path, new IOException($"{error.LocalizedDescription} ({error.Domain} {error.Code})"));
	}

	public override void DidFinishEventsForBackgroundSession(NSUrlSession session)
	{
		var handled = BackgroundEventsHandled;
		BackgroundEventsHandled = null;
		if (handled is not null)
			UIApplication.SharedApplication.BeginInvokeOnMainThread(handled);
	}

	private void Fail(string path, Exception error)
	{
		Console.WriteLine($"Background download failed: {System.IO.Path.GetFileName(path)}: {error.Message}");
		if (transfers.TryGetValue(path, out var transfer))
			transfer.Completion.TrySetException(error);
	}
}

/// <summary>Asks iOS for time to finish, such as decrypting a book, after the app leaves the screen.</summary>
public sealed class AppleBackgroundWork : IBackgroundWork
{
	public IDisposable Begin(string name)
	{
		var id = UIApplication.BackgroundTaskInvalid;
		id = UIApplication.SharedApplication.BeginBackgroundTask(name, () => End(ref id));
		return new Ending(() => End(ref id));
	}

	private static void End(ref nint id)
	{
		if (id == UIApplication.BackgroundTaskInvalid)
			return;
		UIApplication.SharedApplication.EndBackgroundTask(id);
		id = UIApplication.BackgroundTaskInvalid;
	}

	private sealed class Ending(Action end) : IDisposable
	{
		private int done;
		public void Dispose()
		{
			if (Interlocked.Exchange(ref done, 1) == 0)
				end();
		}
	}
}
