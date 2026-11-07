using AudioPlayer;
using AudioToolbox;
using AVFoundation;
using Foundation;
using System;
using System.Buffers;

namespace LibationMobile.iOS;

/// <summary>
/// Plays through AVAudioEngine. A source node pulls interleaved samples from the render callback and hands them
/// to the engine one channel at a time, which is the layout Apple's standard format uses.
/// </summary>
public sealed class AppleAudioOutput : IAudioOutput
{
	private readonly AVAudioEngine engine = new();
	private readonly AVAudioSourceNode sourceNode;
	/// <summary>Decoding and speed change run ahead on their own thread; the real-time callback only copies.</summary>
	private readonly BufferedRender buffer;
	/// <summary>Refilled when half is left: 200 ms in hand at worst, as much as the device takes in two of its longest calls.</summary>
	private static readonly TimeSpan BufferAhead = TimeSpan.FromMilliseconds(400);
	private readonly int channels;
	private readonly AVAudioFormat format;
	private readonly NSObject configurationObserver;
	private readonly NSObject routeObserver;
	/// <summary>Whether playback should be running, so the engine can be restarted when iOS stops it.</summary>
	private volatile bool wantRunning;
	/// <summary>The output last started: the one a call or another app interrupts.</summary>
	private static AppleAudioOutput? current;

	/// <summary>
	/// Whether the book is meant to be playing. Unlike the engine, which iOS stops itself as a call comes in, often
	/// before the app hears of the call, this still says the book was playing.
	/// </summary>
	public static bool IsMeantToPlay => current?.wantRunning == true;

	/// <summary>
	/// A call or another app has taken the audio: stop meaning to play, so a route change during the call (to the
	/// earpiece, or AirPods switching to the call) does not try to start the book again over it.
	/// </summary>
	public static void Interrupted()
	{
		if (current is not { } output)
			return;
		output.wantRunning = false;
		output.engine.Pause();
	}

	public bool IsRunning => engine.Running;
	public int BufferedFrames => buffer.BufferedFrames;
	public void Discard() => buffer.Discard();

	public AppleAudioOutput(int sampleRate, int channels, AudioRenderCallback render)
	{
		this.channels = channels;
		buffer = new BufferedRender(render, sampleRate, channels, BufferAhead);

		UsePlaybackSession();

		format = new AVAudioFormat(sampleRate, (uint)channels);
		sourceNode = new AVAudioSourceNode(format, Render);
		engine.AttachNode(sourceNode);
		engine.Connect(sourceNode, engine.MainMixerNode, format);
		engine.Prepare();

		// iOS stops the engine when the audio route changes: headphones, Bluetooth or a car connecting or going
		// away. Without restarting it, the lock screen's play button does nothing until the app is opened.
		configurationObserver = NSNotificationCenter.DefaultCenter.AddObserver(AVAudioEngine.ConfigurationChangeNotification, _ => OnConfigurationChanged(), engine);

		// AirPods or headphones going away: stop here and now, so the restart above cannot play the book out of
		// the speaker before the player hears about it and pauses.
		routeObserver = AVAudioSession.Notifications.ObserveRouteChange((_, e) =>
		{
			if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
			{
				wantRunning = false;
				engine.Pause();
			}
		});
	}

	/// <summary>
	/// Playback category: keeps playing with the screen locked and ignores the silent switch, like any audiobook app.
	/// Long-form audio is what audiobook and podcast apps declare: pressing play then takes AirPods over from a Mac or
	/// iPad they are connected to, and routes to the speaker the listener last chose for long listening. Also set back
	/// after a voice prompt, which needs the microphone.
	/// </summary>
	public static void UsePlaybackSession()
	{
		var session = AVAudioSession.SharedInstance();
		if (!session.SetCategory(AVAudioSessionCategory.Playback.GetConstant()!.ToString(), AVAudioSessionMode.SpokenAudio.GetConstant()!.ToString(),
			AVAudioSessionRouteSharingPolicy.LongFormAudio, 0, out var error))
		{
			Console.WriteLine($"Long-form audio session refused ({error?.LocalizedDescription}), using plain playback");
			session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionCategoryOptions.AllowAirPlay | AVAudioSessionCategoryOptions.AllowBluetoothA2DP);
			session.SetMode(AVAudioSessionMode.SpokenAudio, out _);
		}
	}

	private void OnConfigurationChanged()
	{
		engine.Connect(sourceNode, engine.MainMixerNode, format);
		engine.Prepare();
		if (!wantRunning)
			return;
		try
		{
			StartEngine();
		}
		catch (InvalidOperationException ex)
		{
			// Left stopped; the player sees it is not running and shows play.
			wantRunning = false;
			Console.WriteLine($"Audio could not restart after a route change: {ex.Message}");
		}
	}

	private unsafe int Render(ref bool isSilence, ref AudioTimeStamp timestamp, uint frameCount, AudioBuffers outputData)
	{
		var count = (int)frameCount * channels;
		var interleaved = ArrayPool<float>.Shared.Rent(count);
		try
		{
			var span = interleaved.AsSpan(0, count);
			buffer.Read(span);

			for (var c = 0; c < channels && c < outputData.Count; c++)
			{
				var channel = (float*)outputData[c].Data;
				for (var f = 0; f < frameCount; f++)
					channel[f] = span[f * channels + c];
			}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(interleaved);
		}
		return 0;
	}

	public void Start()
	{
		current = this;
		wantRunning = true;
		try
		{
			StartEngine();
		}
		catch
		{
			wantRunning = false;
			throw;
		}
	}

	private void StartEngine()
	{
		// Taken back each time: a call, Siri or another app may have had the audio since.
		if (!AVAudioSession.SharedInstance().SetActive(true, out var sessionError))
			throw new InvalidOperationException($"Audio could not start: {sessionError?.LocalizedDescription ?? "another app has the audio"}");
		if (engine.Running)
			return;
		engine.StartAndReturnError(out var error);
		if (error is not null)
			throw new InvalidOperationException($"Audio could not start: {error.LocalizedDescription}");
	}

	public void Stop()
	{
		wantRunning = false;
		engine.Pause();
	}

	public void Dispose()
	{
		wantRunning = false;
		if (current == this)
			current = null;
		NSNotificationCenter.DefaultCenter.RemoveObserver(configurationObserver);
		routeObserver.Dispose();
		engine.Stop();
		engine.DetachNode(sourceNode);
		sourceNode.Dispose();
		engine.Dispose();
		buffer.Dispose();
	}
}
