using BackgroundTasks;
using LibationMobile.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UIKit;

namespace LibationMobile.iOS;

/// <summary>
/// Speech to text through Apple's SpeechAnalyzer (iOS 26), on the device, through the Swift bridge built with the
/// widget (Widget/Bridge/Transcriber.swift). Unavailable on older iOS, and in builds without the bridge.
/// </summary>
public sealed class AppleTranscriber : ITranscriber
{
	private const string BRIDGE = "@rpath/LibationWidgetBridge.framework/LibationWidgetBridge";

	[DllImport(BRIDGE)] private static extern int libation_transcriber_available();
	[DllImport(BRIDGE)] private static extern unsafe int libation_transcriber_installed(byte* locale);
	[DllImport(BRIDGE)] private static extern unsafe void libation_transcriber_install(byte* locale, IntPtr context,
		delegate* unmanaged<IntPtr, double, void> progress, delegate* unmanaged<IntPtr, int, byte*, void> done);
	[DllImport(BRIDGE)] private static extern unsafe IntPtr libation_transcriber_start(byte* path, double from, byte* locale, IntPtr context,
		delegate* unmanaged<IntPtr, double, double, byte*, void> line, delegate* unmanaged<IntPtr, double, void> progress,
		delegate* unmanaged<IntPtr, int, byte*, void> done);
	[DllImport(BRIDGE)] private static extern void libation_transcriber_cancel(IntPtr handle);
	[DllImport(BRIDGE)] private static extern void libation_transcriber_release(IntPtr handle);

	private bool? available;

	public bool IsAvailable => available ??= Probe();

	private static bool Probe()
	{
		if (!OperatingSystem.IsIOSVersionAtLeast(26))
			return false;
		try
		{
			return libation_transcriber_available() == 1;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			// Built without the bridge (Widget/build-widget.sh not run).
			return false;
		}
	}

	private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

	/// <summary>A call into Swift, which answers on its own threads through the callbacks below.</summary>
	private sealed class Call
	{
		public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Action<TranscriptLine>? Line;
		public Action<double>? Progress;
	}

	private static Call Of(IntPtr context) => (Call)GCHandle.FromIntPtr(context).Target!;

	[UnmanagedCallersOnly]
	private static unsafe void OnLine(IntPtr context, double start, double end, byte* text)
	{
		try
		{
			Of(context).Line?.Invoke(new TranscriptLine(start, end, Marshal.PtrToStringUTF8((IntPtr)text) ?? ""));
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Transcript line not kept: {ex.Message}");
		}
	}

	[UnmanagedCallersOnly]
	private static void OnProgress(IntPtr context, double value)
	{
		try
		{
			Of(context).Progress?.Invoke(value);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Transcript progress not shown: {ex.Message}");
		}
	}

	[UnmanagedCallersOnly]
	private static unsafe void OnDone(IntPtr context, int ok, byte* message)
	{
		var call = Of(context);
		if (ok == 1)
			call.Done.TrySetResult();
		else
			call.Done.TrySetException(new IOException(Marshal.PtrToStringUTF8((IntPtr)message) ?? "Transcribing failed."));
	}

	private static unsafe int Installed(string language)
	{
		fixed (byte* l = Utf8(language))
			return libation_transcriber_installed(l);
	}

	private static unsafe void Install(string language, IntPtr context)
	{
		fixed (byte* l = Utf8(language))
			libation_transcriber_install(l, context, &OnProgress, &OnDone);
	}

	private static unsafe IntPtr Start(string path, double from, string language, IntPtr context)
	{
		fixed (byte* p = Utf8(path))
		fixed (byte* l = Utf8(language))
			return libation_transcriber_start(p, from, l, context, &OnLine, &OnProgress, &OnDone);
	}

	public async Task EnsureModelAsync(string language, Action<double> progress, CancellationToken token)
	{
		var installed = await Task.Run(() => Installed(language), token);
		if (installed == 1)
			return;
		if (installed == -1)
			throw new NotSupportedException($"This phone cannot transcribe {language}.");
		var call = new Call { Progress = progress };
		var handle = GCHandle.Alloc(call);
		try
		{
			Install(language, GCHandle.ToIntPtr(handle));
			await call.Done.Task.WaitAsync(token);
		}
		finally
		{
			// Swift may still report after a cancel; the handle stays until it has said it is done.
			_ = call.Done.Task.ContinueWith(_ => handle.Free(), TaskScheduler.Default);
		}
	}

	public async Task TranscribeAsync(string path, double from, string language, Action<TranscriptLine> line, Action<double> progress, CancellationToken token)
	{
		var call = new Call { Line = line, Progress = progress };
		var handle = GCHandle.Alloc(call);
		var job = Start(path, from, language, GCHandle.ToIntPtr(handle));
		if (job == IntPtr.Zero)
		{
			await call.Done.Task;
			handle.Free();
			return;
		}
		await using var stop = token.Register(() => libation_transcriber_cancel(job));
		try
		{
			await call.Done.Task;
		}
		catch when (token.IsCancellationRequested)
		{
			throw new OperationCanceledException(token);
		}
		finally
		{
			libation_transcriber_release(job);
			handle.Free();
		}
	}
}

/// <summary>
/// Keeps a transcript going after the app leaves the screen: iOS 26's continued processing, which shows its progress
/// on the lock screen and lets it be stopped there. Earlier iOS gives the half a minute any app gets.
/// </summary>
public sealed class AppleTranscriptionWork : ITranscriptionWork
{
	public const string TASK_ID = "io.github.freddygaffey.libation.transcribe";

	private static bool registered;
	private static Handle? current;

	public ITranscriptionWorkHandle Begin(string title, Action cancel)
	{
		var handle = new Handle(title, cancel);
		current = handle;
		if (OperatingSystem.IsIOSVersionAtLeast(26))
		{
			if (!registered)
				registered = BGTaskScheduler.Shared.Register(TASK_ID, null, task => current?.Attach((BGContinuedProcessingTask)task));
			var request = new BGContinuedProcessingTaskRequest(TASK_ID, title, "Starting");
			// The form iOS 27 replaces; its successor is not on iOS 26.
#pragma warning disable CA1422
			if (!BGTaskScheduler.Shared.Submit(request, out var error))
#pragma warning restore CA1422
				Console.WriteLine($"Transcript will stop when the app leaves the screen: {error?.LocalizedDescription}");
		}
		return handle;
	}

	private sealed class Handle : ITranscriptionWorkHandle
	{
		private const long UNITS = 1000;
		private readonly string title;
		private readonly Action cancel;
		private readonly Lock locker = new();
		private BGContinuedProcessingTask? task;
		private bool finished;
		private nint backgroundTask;

		public Handle(string title, Action cancel)
		{
			this.title = title;
			this.cancel = cancel;
			backgroundTask = UIApplication.SharedApplication.BeginBackgroundTask("Transcript", EndBackgroundTask);
		}

		public void Attach(BGContinuedProcessingTask given)
		{
			lock (locker)
			{
				if (finished)
				{
					given.SetTaskCompleted(true);
					return;
				}
				task = given;
				if (OperatingSystem.IsIOSVersionAtLeast(26))
					given.Progress.TotalUnitCount = UNITS;
				// Stopped from the lock screen, or by iOS.
				given.ExpirationHandler = cancel;
			}
		}

		public void Report(double fraction, string subtitle)
		{
			lock (locker)
			{
				if (task is null || !OperatingSystem.IsIOSVersionAtLeast(26))
					return;
				task.Progress.CompletedUnitCount = (long)(Math.Clamp(fraction, 0, 1) * UNITS);
				task.UpdateTitle(title, subtitle);
			}
		}

		public void Finish(bool success)
		{
			lock (locker)
			{
				if (finished)
					return;
				finished = true;
				task?.SetTaskCompleted(success);
				task = null;
			}
			EndBackgroundTask();
		}

		public void Dispose() => Finish(false);

		private void EndBackgroundTask()
		{
			var id = Interlocked.Exchange(ref backgroundTask, UIApplication.BackgroundTaskInvalid);
			if (id != UIApplication.BackgroundTaskInvalid)
				UIApplication.SharedApplication.EndBackgroundTask(id);
		}
	}
}
