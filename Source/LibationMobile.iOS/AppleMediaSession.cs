using AVFoundation;
using Foundation;
using LibationMobile.Services;
using MediaPlayer;
using System;
using System.Threading.Tasks;
using UIKit;

namespace LibationMobile.iOS;

/// <summary>
/// The lock screen and Control Centre player, and the play, pause and skip buttons on headphones and in cars.
/// Also pauses when a call or another app takes the audio, and plays again after; and pauses when headphones are unplugged.
/// </summary>
public sealed class AppleMediaSession : IMediaSession
{
	private const double SKIP_SECONDS = 30;

	public event Action? PlayRequested;
	public event Action? PauseRequested;
	public event Action? TogglePlayPauseRequested;
	public event Action? SkipForwardRequested;
	public event Action? SkipBackRequested;
	public event Action<TimeSpan>? SeekRequested;
	public event Action<double>? SpeedRequested;

	private MPNowPlayingInfo? nowPlaying;
	private bool isPlaying;
	private bool wasPlayingWhenInterrupted;
	private bool pendingPlay;

	public DateTime LastButtonPress { get; private set; }

	public AppleMediaSession()
	{
		var commands = MPRemoteCommandCenter.Shared;
		Handle(commands.PlayCommand, () => { LastButtonPress = DateTime.UtcNow; RequestPlay(); });
		Handle(commands.PauseCommand, () => { LastButtonPress = DateTime.UtcNow; RequestPause(); });
		Handle(commands.TogglePlayPauseCommand, () =>
		{
			LastButtonPress = DateTime.UtcNow;
			resumeGeneration++;
			// Before a book is loaded, toggling can only mean play.
			if (TogglePlayPauseRequested is null)
				RequestPlay();
			else
				TogglePlayPauseRequested.Invoke();
		});

		// Audiobook controls: 30-second skips rather than next and previous track.
		commands.SkipForwardCommand.PreferredIntervals = [SKIP_SECONDS];
		commands.SkipBackwardCommand.PreferredIntervals = [SKIP_SECONDS];
		Handle(commands.SkipForwardCommand, () => SkipForwardRequested?.Invoke());
		Handle(commands.SkipBackwardCommand, () => SkipBackRequested?.Invoke());
		commands.NextTrackCommand.Enabled = false;
		commands.PreviousTrackCommand.Enabled = false;

		commands.ChangePlaybackPositionCommand.Enabled = true;
		commands.ChangePlaybackPositionCommand.AddTarget(e =>
		{
			if (e is not MPChangePlaybackPositionCommandEvent change)
				return MPRemoteCommandHandlerStatus.CommandFailed;
			SeekRequested?.Invoke(TimeSpan.FromSeconds(change.PositionTime));
			return MPRemoteCommandHandlerStatus.Success;
		});

		// Speed from Siri ("Hey Siri, speed 2", "set playback speed to 1.5"): without this iOS says the app cannot change
		// speed. Siri picks from the rates listed, so list every tenth the player offers.
		var rates = new NSNumber[96];
		for (var i = 0; i < rates.Length; i++)
			rates[i] = NSNumber.FromDouble((i + 5) / 10.0);
		commands.ChangePlaybackRateCommand.SupportedPlaybackRates = rates;
		commands.ChangePlaybackRateCommand.Enabled = true;
		commands.ChangePlaybackRateCommand.AddTarget(e =>
		{
			if (e is not MPChangePlaybackRateCommandEvent change || change.PlaybackRate <= 0)
				return MPRemoteCommandHandlerStatus.CommandFailed;
			SpeedRequested?.Invoke(change.PlaybackRate);
			return MPRemoteCommandHandlerStatus.Success;
		});

		AVAudioSession.Notifications.ObserveInterruption((_, e) =>
		{
			if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
			{
				// iOS has usually stopped the engine already, and the player may have shown it as paused: what was
				// last shown is not enough to tell whether the book was playing when the call came.
				wasPlayingWhenInterrupted = isPlaying || AppleAudioOutput.IsMeantToPlay;
				Console.WriteLine($"Audio interrupted (playing: {wasPlayingWhenInterrupted})");
				resumeGeneration++;
				AppleAudioOutput.Interrupted();
				PauseRequested?.Invoke();
			}
			else
			{
				var resume = e.Option.HasFlag(AVAudioSessionInterruptionOptions.ShouldResume);
				Console.WriteLine($"Audio interruption ended (was playing: {wasPlayingWhenInterrupted}, should resume: {resume})");
				// After a call or Siri: carry on if it was playing and iOS says resuming is expected.
				if (wasPlayingWhenInterrupted && resume)
					_ = ResumeAfterInterruptionAsync();
				wasPlayingWhenInterrupted = false;
			}
		});
		AVAudioSession.Notifications.ObserveRouteChange((_, e) =>
		{
			// Headphones unplugged or a speaker disconnected: do not carry on out loud.
			if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
				PauseRequested?.Invoke();
		});
	}

	/// <summary>Counts interruptions and presses, so a resume still trying after a call gives way to anything newer.</summary>
	private int resumeGeneration;

	/// <summary>
	/// Play again after a call. The phone app can hold the audio for a moment after the call ends, and taking it back
	/// then fails, which left the book paused; so try for a few seconds before giving up.
	/// </summary>
	private async Task ResumeAfterInterruptionAsync()
	{
		var generation = ++resumeGeneration;
		for (var attempt = 1; attempt <= 10; attempt++)
		{
			if (generation != resumeGeneration)
				return;
			if (AVAudioSession.SharedInstance().SetActive(true, out var error))
			{
				PlayRequested?.Invoke();
				return;
			}
			Console.WriteLine($"Audio not yet back after the interruption (try {attempt}): {error?.LocalizedDescription}");
			await Task.Delay(TimeSpan.FromMilliseconds(500));
		}
	}

	/// <summary>
	/// Play, or remember to once a book is loaded: when iOS has closed the app, pressing play on the lock screen
	/// relaunches it, and the press arrives before the last book has been opened.
	/// </summary>
	public void RequestPlay()
	{
		resumeGeneration++;
		if (PlayRequested is null)
			pendingPlay = true;
		else
			PlayRequested.Invoke();
	}

	/// <summary>Pause, if anything is playing. Also from the home-screen widget.</summary>
	public void RequestPause()
	{
		resumeGeneration++;
		pendingPlay = false;
		PauseRequested?.Invoke();
	}

	/// <summary>Skip, if a book is loaded. From the home-screen widget.</summary>
	public void RequestSkip(bool forward)
	{
		if (forward)
			SkipForwardRequested?.Invoke();
		else
			SkipBackRequested?.Invoke();
	}

	public bool TakePendingPlay()
	{
		var pending = pendingPlay;
		pendingPlay = false;
		return pending;
	}

	private static void Handle(MPRemoteCommand command, Action action)
	{
		command.Enabled = true;
		command.AddTarget(_ =>
		{
			action();
			return MPRemoteCommandHandlerStatus.Success;
		});
	}

	public void Show(MediaInfo info)
	{
		nowPlaying = new MPNowPlayingInfo
		{
			Title = info.Title,
			Artist = info.Subtitle,
			AlbumTitle = info.Album ?? info.Title,
			PlaybackDuration = info.Duration.TotalSeconds,
			MediaType = MPNowPlayingInfoMediaType.Audio
		};

		nowPlaying.Artwork = Artwork(info.Cover);

		MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = nowPlaying;
	}

	// The lock screen is shown again at every chapter; decoding a full-size cover each time is wasted work.
	private byte[]? artworkCover;
	private MPMediaItemArtwork? artwork;

	private MPMediaItemArtwork? Artwork(byte[]? cover)
	{
		if (cover != artworkCover)
		{
			artworkCover = cover;
			artwork = cover is not null && UIImage.LoadFromData(NSData.FromArray(cover)) is UIImage image
				? new MPMediaItemArtwork(image.Size, _ => image)
				: null;
		}
		return artwork;
	}

	public void Update(TimeSpan position, double speed, bool isPlaying)
	{
		this.isPlaying = isPlaying;
		if (nowPlaying is null)
			return;

		// The system advances the shown time by itself at PlaybackRate, so it only needs telling when something changes.
		nowPlaying.ElapsedPlaybackTime = position.TotalSeconds;
		nowPlaying.PlaybackRate = isPlaying ? speed : 0;
		nowPlaying.DefaultPlaybackRate = speed;
		MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = nowPlaying;
	}

	public void Clear()
	{
		nowPlaying = null;
		MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
	}
}
