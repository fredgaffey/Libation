using Avalonia;
using Avalonia.iOS;
using Foundation;
using System;
using System.Linq;
using LibationMobile.Services;

namespace LibationMobile.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
	{
		// iOS plays through Apple's own audio stack: it keeps playing in the background and on the lock screen,
		// and it works in the simulator, which SoundFlow's native libraries do not.
		AudioBackend.OpenSource = path => new AppleAudioFileSource(path);
		AudioBackend.CreateOutput = (sampleRate, channels, render) => new AppleAudioOutput(sampleRate, channels, render);
		var mediaSession = new AppleMediaSession();
		MediaSession.Platform = mediaSession;
		HomeWidget.Platform = new AppleHomeWidget(mediaSession);
		DocumentViewer.Platform = new AppleDocumentViewer();
		ListeningContext.Platform = new AppleListeningContext();
		VoicePrompt.Platform = new AppleVoicePrompt();
		// Slow voices speak ahead to disk while the phone charges: with the book open, and overnight.
		UIKit.UIDevice.CurrentDevice.BatteryMonitoringEnabled = true;
		Voicing.OvernightVoicing.Register();
		// The GPU only while on screen: iOS refuses it to apps in the background.
		Kokoro.KokoroGpu.WatchAppState();
		DocumentText.Pdf = new ApplePdfReader();
		BookVoice.Platform = new AppleBookVoice();
		Transcriber.Platform = new AppleTranscriber();
		Transcriber.Work = new AppleTranscriptionWork();
		AudioBackend.Route = AppleAudioRoute.Describe;
		// Created at launch, so a download that finished while the app was closed is delivered.
		FileTransfer.Platform = new AppleFileTransfer();
		FileTransfer.BackgroundWork = new AppleBackgroundWork();
#if DEBUG
		// LIBATION_OVERNIGHT_TEST=SECONDS: overnight voicing of the newest voiced book in Heart, for that long, now.
		if (Environment.GetEnvironmentVariable("LIBATION_OVERNIGHT_TEST") is { Length: > 0 } overnight)
			System.Threading.Tasks.Task.Run(() =>
			{
				var voiced = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voiced");
				var text = System.IO.Directory.EnumerateFiles(voiced, "text.txt", System.IO.SearchOption.AllDirectories).OrderByDescending(System.IO.File.GetLastWriteTimeUtc).First();
				Voicing.OvernightVoicing.Ask(new Voicing.OvernightVoicing.Job(text, "kokoro:af_heart", 0));
				var done = Voicing.OvernightVoicing.VoiceAhead(default, TimeSpan.FromSeconds(int.Parse(overnight)));
				Console.WriteLine($"LIBATION_TEST overnight: finished={done}");
			});
		// LIBATION_KOKORO_BENCH=SECONDS[:VOICE]: how fast Kokoro speaks on this device, writing nothing and playing nothing.
		if (Environment.GetEnvironmentVariable("LIBATION_KOKORO_BENCH") is { Length: > 0 } bench)
			System.Threading.Tasks.Task.Run(() => Kokoro.KokoroBench.Run(bench));
#endif
		return base.CustomizeAppBuilder(builder).WithInterFont();
	}

	/// <summary>iOS woke the app because the background download session has news.</summary>
	[Export("application:handleEventsForBackgroundURLSession:completionHandler:")]
	public void HandleEventsForBackgroundUrl(UIKit.UIApplication application, string sessionIdentifier, Action completionHandler)
	{
		if (sessionIdentifier == AppleFileTransfer.SESSION_ID)
			AppleFileTransfer.BackgroundEventsHandled = completionHandler;
		else
			completionHandler();
	}
}
