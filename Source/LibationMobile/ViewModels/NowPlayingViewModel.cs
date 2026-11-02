using AudioPlayer;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using Mpeg4Lib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

/// <summary>One line in the chapter list.</summary>
public partial class ChapterRowViewModel(int number, Chapter chapter) : ObservableObject
{
	public Chapter Chapter { get; } = chapter;
	public int Number { get; } = number;
	// Titles embedded in Audible files often carry stray spaces.
	public string Title { get; } = chapter.Title.Trim();
	public string LengthText { get; } = NowPlayingViewModel.FormatTime(chapter.Duration);

	/// <summary>The chapter playing now.</summary>
	[ObservableProperty]
	private bool isCurrent;

	/// <summary>Before the current chapter.</summary>
	[ObservableProperty]
	private bool isPlayed;
}

/// <summary>The one book currently loaded for playback. Use on the UI thread.</summary>
public partial class NowPlayingViewModel : ObservableObject, IDisposable
{
	public static IReadOnlyList<double> SpeedStops { get; } = [1, 1.5, 2, 3, 5, 10];
	public const double MIN_SPEED = AudioFilePlayer.MIN_SPEED;
	public const double MAX_SPEED = AudioFilePlayer.MAX_SPEED;
	private const double SPEED_STEP = 0.1;

	private TimeSpan SkipInterval => TimeSpan.FromSeconds(settings.SkipSeconds);
	/// <summary>The number shown inside the skip buttons.</summary>
	public string SkipText => settings.SkipSeconds.ToString();
	private static readonly TimeSpan RestartChapterThreshold = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan FinishedThreshold = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);

	public LocalBook Book { get; }

	/// <summary>Played from Audible over the network rather than from a download.</summary>
	public bool IsStreamed => Book.IsStreamed;

	/// <summary>For a streamed book: that it is, and whether the sound is still on its way.</summary>
	[ObservableProperty]
	private string? streamText;
	public string Title => Book.Title;
	public string? Author => Book.Author;
	/// <summary>Long titles step down a size so they fit in two lines rather than crowding the controls.</summary>
	public double TitleFontSize => Title.Length switch { <= 24 => 22, <= 40 => 19, _ => 17 };
	public Bitmap? Cover { get; }
	public IReadOnlyList<Chapter> Chapters { get; }
	public bool HasChapters => Chapters.Count > 1;
	public TimeSpan Duration => player.Duration;
	public double DurationSeconds => Duration.TotalSeconds;

	public IReadOnlyList<ChapterRowViewModel> ChapterRows { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PositionSeconds), nameof(Progress), nameof(ElapsedText), nameof(RemainingText), nameof(ChapterRemainingText), nameof(ScrubberSeconds), nameof(ScrubberElapsedText), nameof(ScrubberRemainingText), nameof(ScrubberDetailText))]
	private TimeSpan position;

	/// <summary>The chapter list, shown over Now Playing.</summary>
	[ObservableProperty]
	private bool isChapterListOpen;

	/// <summary>The chapter playing now, or null for a book without chapters.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ChapterText), nameof(ScrubberMaximum), nameof(ScrubberSeconds))]
	private ChapterRowViewModel? currentChapterRow;

	[ObservableProperty]
	private bool isPlaying;

	/// <summary>Playback position in seconds, for a slider. Setting it seeks.</summary>
	public double PositionSeconds
	{
		get => Position.TotalSeconds;
		set
		{
			if (Math.Abs(value - Position.TotalSeconds) >= 1)
				Seek(TimeSpan.FromSeconds(value));
		}
	}

	#region Scrubber

	/// <summary>Whether the scrubber covers the chapter playing now, as the Audible app's does, or the whole book.</summary>
	private bool ScrubsChapter => settings.ScrubByChapter && HasChapters && CurrentChapter is not null;
	private TimeSpan ScrubberStart => ScrubsChapter ? CurrentChapter!.StartOffset : TimeSpan.Zero;
	private TimeSpan ScrubberLength => ScrubsChapter ? CurrentChapter!.Duration : Duration;

	public double ScrubberMaximum => Math.Max(1, ScrubberLength.TotalSeconds);

	/// <summary>
	/// A finger is on a seeking slider. Only then does setting its value seek: a slider also sets its value itself when
	/// its range changes, as the scrubber's does at each chapter, and taking that as a seek jumped ahead mid-book.
	/// </summary>
	public bool IsScrubbing { get; set; }

	/// <summary>Position along the scrubber, in seconds. Setting it while scrubbing seeks.</summary>
	public double ScrubberSeconds
	{
		get => Math.Clamp((Position - ScrubberStart).TotalSeconds, 0, ScrubberMaximum);
		set
		{
			if (IsScrubbing && Math.Abs(value - (Position - ScrubberStart).TotalSeconds) >= 1)
				// Stop just short of the end, so dragging to it does not tip into the next chapter.
				Seek(ScrubberStart + TimeSpan.FromSeconds(Math.Min(value, ScrubberMaximum - 0.5)));
		}
	}

	public string ScrubberElapsedText => FormatTime(Position - ScrubberStart);
	public string ScrubberRemainingText => ScrubsChapter ? ChapterRemainingText : RemainingText;
	/// <summary>Under the chapter name: whichever of the chapter and the book the scrubber is not showing.</summary>
	public string ScrubberDetailText => ScrubsChapter ? $"Book: {RemainingText}" : ChapterRemainingText;

	/// <summary>Pick up a change to the scrubber setting.</summary>
	private void RefreshScrubber()
	{
		OnPropertyChanged(nameof(ScrubberMaximum));
		OnPropertyChanged(nameof(ScrubberSeconds));
		OnPropertyChanged(nameof(ScrubberElapsedText));
		OnPropertyChanged(nameof(ScrubberRemainingText));
		OnPropertyChanged(nameof(ScrubberDetailText));
	}

	#endregion

	public double Progress => Duration > TimeSpan.Zero ? Position / Duration : 0;
	public string ElapsedText => FormatTime(Position);
	public string RemainingText => IsBlindMode
		? $"{Progress:P0} through"
		: $"{FormatTime((Duration - Position) / Speed)} left at {SpeedText}";

	/// <summary>Time to the end of this chapter at the current speed: how long until the next natural stopping point.</summary>
	public string ChapterRemainingText
		=> IsBlindMode ? ""
		: CurrentChapterRow?.Chapter is Chapter chapter
			? $"{FormatTime((chapter.EndOffset - Position) / Speed)} left in chapter"
			: "";

	/// <summary>The speed, as the listener sets it. During a training warm-up it moves the climb, which carries on from there.</summary>
	public double Speed
	{
		get => player.Speed;
		set
		{
			if (IsTraining)
				AdjustTraining(value);
			else
			{
				// Set by hand: the listener has taken over from any warm-up.
				EndWarmUp();
				ApplySpeed(value, save: true);
				RetargetRate();
			}
		}
	}

	/// <summary>Change speed. Unsaved, it lasts until the book is closed: training mode climbs without moving the book's own speed.</summary>
	private void ApplySpeed(double value, bool save)
	{
		{
			var speed = Math.Round(Math.Clamp(value, MIN_SPEED, MAX_SPEED) / SPEED_STEP) * SPEED_STEP;
			if (Math.Abs(speed - player.Speed) < 0.001)
				return;
			player.Speed = (float)speed;
			if (save)
				settings.SetBookSpeed(Book.Id, (float)speed);
			LogSpeedChange();
			OnPropertyChanged(nameof(Speed));
			OnPropertyChanged(nameof(SpeedText));
			OnPropertyChanged(nameof(SpeedChipText));
			OnPropertyChanged(nameof(RemainingText));
			OnPropertyChanged(nameof(ChapterRemainingText));
			RefreshScrubber();
			UpdateMediaSession();
		}
	}

	public string SpeedText => $"{Speed:0.0}×";

	/// <summary>
	/// Syllables a second as heard: the book's own rate, measured from the audio, times the speed. The usual measure
	/// in speech research. For scale, untrained listeners top out around 8; blind screen-reader experts reach 17 to 22.
	/// </summary>
	[ObservableProperty]
	private string syllableRateText = "";

	private void UpdateSyllableRate()
	{
		UpdateSpeedChip();
		SyllableRateText = !settings.ShowSyllableRate || IsBlindMode ? ""
			: player.SourceSyllablesPerSecond is double rate && rate > 0 ? $"≈ {rate * Speed:0} syllables a second"
			: "Measuring syllables a second…";
	}

	/// <summary>Output gain: 0 is silent, 1 is unchanged.</summary>
	public double Volume
	{
		get => player.Volume;
		set
		{
			player.Volume = (float)value;
			OnPropertyChanged();
		}
	}

	public string ChapterText
		=> HasChapters && CurrentChapterRow is { } row ? $"{row.Title}, {row.Number} of {Chapters.Count}" : "";

	private Chapter? CurrentChapter => CurrentChapterRow?.Chapter ?? Chapters.FirstOrDefault();

	private readonly AudioFilePlayer player;
	private readonly MobileSettings settings;
	private readonly DispatcherTimer timer;
	private DateTime lastSaved = DateTime.MinValue;
	private bool disposed;

	private NowPlayingViewModel(LocalBook book, AudioFilePlayer player, IReadOnlyList<Chapter> chapters, MobileSettings settings, AudibleAnnotations? annotations, LocalAnnotations? localAnnotations)
	{
		Book = book;
		this.player = player;
		this.settings = settings;
		Chapters = chapters;
		ChapterRows = chapters.Select((c, i) => new ChapterRowViewModel(i + 1, c)).ToList();
		Cover = LoadCover(book.Cover, 900);

		// Each book keeps its own speed; one not played before starts at the speed last used.
		player.Speed = settings.GetBookSpeed(book.Id) ?? settings.Speed;
		player.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(OnPlaybackEnded);
		if (settings.GetPosition(book.Id) is TimeSpan saved && saved < Duration - FinishedThreshold)
			player.Seek(saved);

		timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Update());
		timer.Start();
		Update();
		StartAnnotations(annotations, localAnnotations);
		StartMediaSession();
		UpdateRampText();
	}

	/// <param name="annotations">Position sync with Audible. Null plays without syncing.</param>
	/// <param name="localAnnotations">Bookmarks and clips saved on the device. Null plays without them.</param>
	public static async Task<NowPlayingViewModel> OpenAsync(LocalBook book, MobileSettings settings, AudibleAnnotations? annotations = null, LocalAnnotations? localAnnotations = null, ListeningLog? log = null)
	{
		var chapters = book.Chapters ?? await AudioFileChapters.ReadAsync(book.Path);
		AudioPlayer.IPcmSource? source = null;
		var player = await Task.Run(() => new AudioFilePlayer(source = book.OpenSource?.Invoke() ?? AudioBackend.OpenSource(book.Path),
			AudioBackend.CreateOutput, AudioBackend.CreateStretcher));
		settings.LastBookId = book.Id;
		return new NowPlayingViewModel(book, player, chapters, settings, annotations, localAnnotations) { log = log, voicedSource = source as IVoicedSource };
	}

	[RelayCommand]
	public void PlayPause()
	{
		if (player.IsPlaying)
		{
			player.Pause();
			SavePosition();
			PushPosition();
		}
		else
		{
			// A question being asked aloud stops when play is pressed.
			voiceCancel?.Cancel();
			// Ask Audible first (it notes what this device knew before playing), then start without waiting.
			_ = SyncPositionAsync(whilePlaying: true);
			try
			{
				player.Play();
			}
			catch (InvalidOperationException ex)
			{
				// Another app or a call has the audio. Say so rather than crash; pressing play again retries.
				ShowStatus(ex.Message);
			}
		}
		Update();
	}

	[RelayCommand]
	private void SkipBack()
	{
		seekSource = "skip";
		Seek(Position - SkipInterval);
	}

	[RelayCommand]
	private void SkipForward()
	{
		seekSource = "skip";
		Seek(Position + SkipInterval);
	}

	[RelayCommand]
	private void PreviousChapter()
	{
		seekSource = "chapter";
		if (CurrentChapter is not Chapter current)
			return;
		var previous = Chapters.LastOrDefault(c => c.StartOffset < current.StartOffset);
		Seek(Position - current.StartOffset > RestartChapterThreshold || previous is null ? current.StartOffset : previous.StartOffset);
	}

	[RelayCommand]
	private void NextChapter()
	{
		seekSource = "chapter";
		if (Chapters.FirstOrDefault(c => c.StartOffset > Position) is Chapter next)
			Seek(next.StartOffset);
	}

	[RelayCommand]
	private void ShowChapters()
	{
		if (HasChapters)
			IsChapterListOpen = true;
	}

	[RelayCommand]
	private void HideChapters() => IsChapterListOpen = false;

	[RelayCommand]
	private void JumpToChapter(ChapterRowViewModel row)
	{
		seekSource = "chapter";
		Seek(row.Chapter.StartOffset);
		IsChapterListOpen = false;
	}

	[RelayCommand]
	private void SlowerStep() => Speed -= SPEED_STEP;

	[RelayCommand]
	private void FasterStep() => Speed += SPEED_STEP;

	public void Seek(TimeSpan target)
	{
		target = target < TimeSpan.Zero ? TimeSpan.Zero : target > Duration ? Duration : target;
		LogSeek(Position, target);
		clipEnd = null;
		lastLocalActivity = DateTimeOffset.UtcNow;
		player.Seek(target);
		Update();
		UpdateMediaSession();
	}

	private void Update()
	{
		if (disposed)
			return;
		var wasPlaying = IsPlaying;
		IsPlaying = player.IsPlaying;
		LogPlayback(wasPlaying);
		Position = player.Position;
		if (IsPlaying != wasPlaying)
			UpdateMediaSession();
		UpdateCurrentChapter();
		UpdateAnnotations();
		UpdateSyllableRate();
		TargetRateTick();
		CheckNearingEnd();
		CountListening();
		SleepTick();
		SleepTimerTick();
		StatusTick();
		if (Book.IsStreamed)
			StreamText = IsPlaying && player.IsWaiting ? "Streaming · loading…" : "Streaming from Audible";
		if (IsPlaying && DateTime.UtcNow - lastSaved > SaveInterval)
			SavePosition();
	}

	private void UpdateCurrentChapter()
	{
		// Ticks four times a second, so only touch the rows when the chapter actually changes.
		var index = -1;
		for (var i = 0; i < ChapterRows.Count && ChapterRows[i].Chapter.StartOffset <= Position; i++)
			index = i;
		var current = index >= 0 ? ChapterRows[index] : ChapterRows.FirstOrDefault();
		if (current == CurrentChapterRow)
			return;

		CurrentChapterRow = current;
		chapterStartedAt = DateTime.UtcNow;
		LogEvent("chapter", detail: current?.Title);
		for (var i = 0; i < ChapterRows.Count; i++)
		{
			ChapterRows[i].IsCurrent = ChapterRows[i] == current;
			ChapterRows[i].IsPlayed = i < index;
		}
		UpdateMediaSession();
	}

	private DateTime? lastListeningTick;
	private TimeSpan uncountedTimeSpent;
	private TimeSpan uncountedBookTime;
	/// <summary>Longer gaps between ticks than this are not counted, since the app may have been suspended.</summary>
	private static readonly TimeSpan LongestCountedTick = TimeSpan.FromSeconds(30);

	/// <summary>Add up listening for the time-saved figures: time spent, and how much of the book that covered.</summary>
	private void CountListening()
	{
		var now = DateTime.UtcNow;
		// Previewing a clip is not listening to the book.
		var listening = IsPlaying && ClipEditor is null;
		if (listening && lastListeningTick is DateTime last && now - last < LongestCountedTick)
		{
			TrainingTick(now - last);
			TrialTick(now - last);
			RampTick(now - last);
			uncountedTimeSpent += now - last;
			uncountedBookTime += (now - last) * Speed;
			sessionSpent += now - last;
			sessionBookTime += (now - last) * Speed;
			// Syllables heard: the book's own rate, measured from the audio, over the book time just played.
			if (player.SourceSyllablesPerSecond is double rate)
				sessionSyllables += rate * ((now - last) * Speed).TotalSeconds;
		}
		lastListeningTick = listening ? now : null;

		if (listening && sessionStarted is null && pausedSession is { } paused && DateTimeOffset.Now - paused.Ended < SessionResumeGap)
		{
			// Back from a short pause: the same session carries on, rather than the log filling with two-minute pieces.
			sessionStarted = paused.Started;
			sessionFrom = paused.From;
			sessionSpent = paused.Spent;
			sessionBookTime = paused.BookTime;
			sessionSyllables = paused.Syllables;
			pausedSession = null;
		}
		else if (listening && sessionStarted is null)
		{
			sessionStarted = DateTimeOffset.Now;
			sessionFrom = Position;
			sessionSpent = sessionBookTime = TimeSpan.Zero;
			sessionSyllables = 0;
			pausedSession = null;
			StartMarks();
			StartTraining();
			StartCheckIns();
			StartWarmUp();
		}
		else if (!listening && sessionStarted is not null)
			EndSession();
		else
			MarkIfDue();
	}

	#region Listening log

	private ListeningLog? log;
	private DateTimeOffset? sessionStarted;
	private TimeSpan sessionFrom;
	private TimeSpan sessionSpent;
	private TimeSpan sessionBookTime;
	private double sessionSyllables;
	/// <summary>Shorter stretches, such as checking where a chapter starts, are not worth a line in the log.</summary>
	private static readonly TimeSpan ShortestLoggedSession = TimeSpan.FromSeconds(30);
	/// <summary>Playing again within this long of a pause continues the session it paused.</summary>
	internal static readonly TimeSpan SessionResumeGap = TimeSpan.FromMinutes(5);
	/// <summary>The session last paused, kept for <see cref="SessionResumeGap"/> in case listening carries on.</summary>
	private (DateTimeOffset Started, DateTimeOffset Ended, TimeSpan From, TimeSpan Spent, TimeSpan BookTime, double Syllables)? pausedSession;

	/// <summary>Set while ending a session on purpose, so it is not continued.</summary>
	private bool endingForJump;

	private void EndSession()
	{
		OfferEndCheckIn(sessionSpent, sessionSyllables);
		if (sessionStarted is DateTimeOffset began && sessionSpent >= ShortestLoggedSession)
			LogActivity(began, DateTimeOffset.Now, "session");
		var asleep = TakeFellAsleep();
		if (sessionStarted is DateTimeOffset started && sessionSpent >= ShortestLoggedSession)
			log?.Add(new ListeningSession(Book.Id, Title, started, DateTimeOffset.Now, sessionFrom, Position,
				sessionBookTime.TotalSeconds, sessionSpent.TotalSeconds, (float)Speed, sessionMarks.Count > 0 ? sessionMarks.ToList() : null,
				sessionSyllables > 0 ? Math.Round(sessionSyllables) : null, asleep?.At, asleep?.Position, asleep is null ? null : "auto-pause"));
		// A pause for sleep, or one from jumping elsewhere in the book through its history, is a real end.
		pausedSession = sessionStarted is DateTimeOffset s && asleep is null && !endingForJump
			? (s, DateTimeOffset.Now, sessionFrom, sessionSpent, sessionBookTime, sessionSyllables) : null;
		sessionStarted = null;
	}

	#endregion

	private void SavePosition()
	{
		if (uncountedTimeSpent > TimeSpan.Zero)
		{
			settings.AddListening(uncountedBookTime, uncountedTimeSpent);
			uncountedBookTime = uncountedTimeSpent = TimeSpan.Zero;
		}
		lastSaved = DateTime.UtcNow;
		// Unmoved, it keeps the time it was saved: just opening or leaving the app must not make this device look like
		// the latest to be listened on, or the place the Audible app reached would never be picked up.
		if (settings.GetPosition(Book.Id) is TimeSpan saved && (saved - player.Position).Duration() < TimeSpan.FromSeconds(1))
			return;
		settings.SetPosition(Book.Id, player.Position);
	}

	private void OnPlaybackEnded()
	{
		if (disposed)
			return;
		player.Pause();
		settings.RemovePosition(Book.Id);
		Update();
		BookEnded?.Invoke();
	}

	internal static Bitmap? LoadCover(byte[]? bytes, int width)
	{
		if (bytes is null)
			return null;
		try
		{
			using var stream = new MemoryStream(bytes);
			return Bitmap.DecodeToWidth(stream, width);
		}
		catch
		{
			return null;
		}
	}

	internal static string FormatTime(TimeSpan time)
		=> time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time:mm\\:ss}" : time.ToString("m\\:ss");

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		timer.Stop();
		statusTimer?.Stop();
		StopMediaSession();
		EndSession();
		EndCheckIns();
		SavePosition();
		PushPosition();
		player.Dispose();
		Cover?.Dispose();
		GC.SuppressFinalize(this);
	}
}
