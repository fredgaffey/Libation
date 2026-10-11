using AudibleApi.Common;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

/// <summary>A bookmark or clip in the list: saved on this device, or found on Audible's annotation server.</summary>
public class AnnotationRowViewModel
{
	/// <summary>Set for one saved on this device.</summary>
	public LocalAnnotation? Local { get; }
	/// <summary>Set for one that exists only on Audible's server, e.g. made in another app.</summary>
	public IRecord? Server { get; }

	public TimeSpan Start { get; }
	public TimeSpan? End { get; }
	public bool IsClip => End is not null;
	public string Title { get; }
	/// <summary>Where it is, and for a clip how long.</summary>
	public string DetailText { get; }

	/// <summary>What the listener wrote about it, if anything.</summary>
	public string? Note { get; }
	public bool HasNote => !string.IsNullOrWhiteSpace(Note);

	public AnnotationRowViewModel(LocalAnnotation local, string? chapterTitle)
		: this(local.Start, local.End, local.Title, local.Note, chapterTitle) => Local = local;

	public AnnotationRowViewModel(IRecord server, string? chapterTitle)
		: this(server.Start, (server as Clip)?.End, (server as Clip)?.Title, (server as IRangeAnnotation)?.Text, chapterTitle) => Server = server;

	private AnnotationRowViewModel(TimeSpan start, TimeSpan? end, string? title, string? note, string? chapterTitle)
	{
		Note = note;
		Start = start;
		End = end;
		Title = !string.IsNullOrWhiteSpace(title) ? title : end is null ? "Bookmark" : "Clip";
		var where = NowPlayingViewModel.FormatTime(start) + (chapterTitle is null ? "" : $" in {chapterTitle}");
		DetailText = end is TimeSpan clipEnd ? $"{where}, {(clipEnd - start).TotalSeconds:0}s" : where;
	}
}

/// <summary>Choosing where a new clip starts and ends, and what to call it.</summary>
public partial class ClipEditorViewModel : ObservableObject
{
	public static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(1);
	/// <summary>Audible's limit, kept so clips stay compatible with it.</summary>
	public static readonly TimeSpan MaximumLength = TimeSpan.FromSeconds(45);

	private readonly TimeSpan bookDuration;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(RangeText), nameof(StartText), nameof(StartSeconds), nameof(FromToText))]
	private TimeSpan start;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(RangeText), nameof(EndText), nameof(EndSeconds), nameof(FromToText))]
	private TimeSpan end;

	/// <summary>The stretch of the book the sliders cover: mostly what was just heard, and a little of what follows.</summary>
	public double WindowStartSeconds { get; }
	public double WindowEndSeconds { get; }

	/// <summary>
	/// The start, for the waveform. Only kept within the book here: the waveform keeps the clip between its shortest and
	/// longest as it is dragged, and moving the whole clip sets one end a moment before the other.
	/// </summary>
	public double StartSeconds
	{
		get => Start.TotalSeconds;
		set => Start = Clamp(TimeSpan.FromSeconds(value), TimeSpan.Zero, bookDuration);
	}

	public double EndSeconds
	{
		get => End.TotalSeconds;
		set => End = Clamp(TimeSpan.FromSeconds(value), TimeSpan.Zero, bookDuration);
	}

	public double MinimumSeconds => MinimumLength.TotalSeconds;
	public double MaximumSeconds => MaximumLength.TotalSeconds;

	/// <summary>Loudness across the window, for the waveform. Null until read from the audio.</summary>
	[ObservableProperty]
	private IReadOnlyList<float>? peaks;

	/// <summary>The clip as saved: kept between the shortest and longest allowed.</summary>
	public (TimeSpan Start, TimeSpan End) Range
	{
		get
		{
			var end = Clamp(End, Start + MinimumLength, Min(bookDuration, Start + MaximumLength));
			return (Start, end);
		}
	}

	[ObservableProperty]
	private string? title;

	[ObservableProperty]
	private string? note;

	public string StartText => FormatTenths(Start);
	public string EndText => FormatTenths(End);
	public string RangeText => $"{(End - Start).TotalSeconds:0.0} s";
	public string FromToText => $"{FormatTenths(Start)} – {FormatTenths(End)}";

	/// <summary>"1:02:15.3": clips are set to a tenth of a second.</summary>
	private static string FormatTenths(TimeSpan time)
		=> NowPlayingViewModel.FormatTime(TimeSpan.FromSeconds(Math.Floor(time.TotalSeconds))) + $".{(int)(time.TotalSeconds * 10 % 10)}";

	/// <summary>The book and chapter, above the waveform.</summary>
	public string ContextText { get; init; } = "";

	/// <param name="heardUpTo">Where playback was: the clip starts out as the 30 seconds before it.</param>
	public ClipEditorViewModel(TimeSpan heardUpTo, TimeSpan bookDuration, TimeSpan initialLength)
	{
		this.bookDuration = bookDuration;
		end = heardUpTo;
		start = heardUpTo - initialLength < TimeSpan.Zero ? TimeSpan.Zero : heardUpTo - initialLength;
		if (end - start < MinimumLength)
			end = Min(start + initialLength, bookDuration);
		WindowStartSeconds = Max(TimeSpan.Zero, heardUpTo - TimeSpan.FromSeconds(90)).TotalSeconds;
		WindowEndSeconds = Min(bookDuration, heardUpTo + TimeSpan.FromSeconds(30)).TotalSeconds;
	}

	/// <summary>Move the start by a number of seconds (a tenth or more), keeping the clip between its shortest and longest.</summary>
	[RelayCommand]
	private void NudgeStart(string seconds)
		=> Start = Clamp(Start + TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), Max(TimeSpan.Zero, End - MaximumLength), End - MinimumLength);

	[RelayCommand]
	private void NudgeEnd(string seconds)
		=> End = Clamp(End + TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), Start + MinimumLength, Min(bookDuration, Start + MaximumLength));

	/// <summary>Set the length by moving the start; the end stays where the listener stopped.</summary>
	[RelayCommand]
	private void SetLength(string seconds)
		=> Start = Max(TimeSpan.Zero, End - TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));

	private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
	private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
	private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}

// Position sync with Audible, and bookmarks and clips. See AudibleAnnotations for why the two differ.
public partial class NowPlayingViewModel
{
	/// <summary>How often the position goes to Audible while playing. It is also sent on pause and on leaving the book.</summary>
	private static readonly TimeSpan RemoteSaveInterval = TimeSpan.FromMinutes(1);
	/// <summary>A position from another device only wins if it is newer than ours by more than clock jitter, and actually elsewhere.</summary>
	private static readonly TimeSpan SyncTimeMargin = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan SyncMinimumDistance = TimeSpan.FromSeconds(10);
	/// <summary>Two annotations this close together, of the same kind, are the same one seen in two places.</summary>
	private static readonly TimeSpan SameAnnotationTolerance = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(6);

	public ObservableCollection<AnnotationRowViewModel> Annotations { get; } = new();
	public bool HasAnnotations => Annotations.Count > 0;

	[ObservableProperty]
	private bool isAnnotationListOpen;

	/// <summary>A short note about something that just happened, shown for a few seconds.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStatus))]
	private string? statusMessage;

	public bool HasStatus => StatusMessage is not null;

	/// <summary>The note under the chapter can be undone, until it goes away: a jump to another device's place, a bookmark.</summary>
	[ObservableProperty]
	private bool canUndo;
	private Action? undo;

	private AudibleAnnotations? annotations;
	private LocalAnnotations? localAnnotations;
	private IReadOnlyList<IRecord> serverAnnotations = [];
	private DispatcherTimer? statusTimer;
	private DateTime lastRemoteSave = DateTime.UtcNow;
	private TimeSpan positionBeforeSync;
	private TimeSpan? clipEnd;
	/// <summary>How often to ask Audible for a newer position while paused.</summary>
	private static readonly TimeSpan RemoteCheckInterval = TimeSpan.FromSeconds(30);
	private DateTime lastRemoteCheck = DateTime.UtcNow;
	private bool syncInProgress;
	/// <summary>When the listener last played or moved in this book on this device, which saving alone does not capture.</summary>
	private DateTimeOffset? lastLocalActivity;

	private void StartAnnotations(AudibleAnnotations? service, LocalAnnotations? local)
	{
		annotations = service;
		localAnnotations = local;
		Annotations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAnnotations));
		ShowAnnotationRows();
		_ = SyncPositionAsync();
		_ = LoadServerAnnotationsAsync();
	}

	#region Position

	/// <summary>
	/// Jump to where another device left off, if that is more recent than anything done here.
	/// Checked when a book opens, on pressing play, on returning to the app, and now and then while paused.
	/// </summary>
	/// <param name="whilePlaying">Apply the result even if playback has started, for the check made on pressing play.</param>
	private async Task SyncPositionAsync(bool whilePlaying = false)
	{
		// Not while a clip is being made: playback there is a preview, and must not be moved.
		if (annotations is null || syncInProgress || !settings.SyncPosition || ClipEditor is not null)
			return;

		// What "here" knew before asking: playback during the request must not make this device look newer.
		var localTime = settings.GetPositionTime(Book.Id);
		var hasLocalPosition = settings.GetPosition(Book.Id) is TimeSpan local && local > TimeSpan.Zero;
		var activity = lastLocalActivity;
		lastRemoteCheck = DateTime.UtcNow;

		RemotePosition? remote;
		syncInProgress = true;
		try
		{
			remote = await annotations.GetPositionAsync(Book.Id);
		}
		catch (Exception)
		{
			// Offline, or Audible is unreachable: carry on from the position saved on this device.
			return;
		}
		finally
		{
			syncInProgress = false;
		}
		if (disposed || remote is null || (player.IsPlaying && !whilePlaying))
			return;

		// A position at the very start means "not started there", not "rewind to the beginning".
		if (remote.Position < SyncMinimumDistance || remote.Position >= Duration - FinishedThreshold)
			return;

		// A position saved here with no record of when (from before times were kept) must not lose to Audible's:
		// its age is unknown, and moving away from it loses the listener's place.
		if (hasLocalPosition && localTime is null && activity is null)
			return;

		var lastHere = new[] { localTime, activity }.Max() ?? DateTimeOffset.MinValue;
		var newer = remote.Updated > lastHere + SyncTimeMargin;
		var elsewhere = (remote.Position - Position).Duration() > SyncMinimumDistance;
		if (!newer || !elsewhere)
			return;

		positionBeforeSync = Position;
		seekSource = "sync";
		Seek(remote.Position);
		SavePosition();
		ShowStatus($"Moved to {FormatTime(remote.Position)}, where you left off on another device.", UndoSync);
	}

	/// <summary>
	/// The app came back to the front, or the listener asked for a refresh: another device may have moved on,
	/// or added bookmarks, since.
	/// </summary>
	/// <summary>Pick up a change made on the settings page.</summary>
	public void SettingsChanged()
	{
		OnPropertyChanged(nameof(SkipText));
		OnPropertyChanged(nameof(TrainingEnabled));
		RefreshMode();
		RefreshScrubber();
		UpdateMediaSession();
	}

	public void RefreshFromAudible()
	{
		if (!player.IsPlaying)
			_ = SyncPositionAsync();
		_ = LoadServerAnnotationsAsync();
	}

	/// <summary>The app is leaving the front: make sure Audible has the latest position.</summary>
	public void OnAppBackgrounded()
	{
		SavePosition();
		PushPosition();
	}

	private void UndoSync()
	{
		Seek(positionBeforeSync);
		SavePosition();
		ShowStatus($"Back at {FormatTime(positionBeforeSync)}.");
	}

	[RelayCommand]
	private void Undo()
	{
		var action = undo;
		undo = null;
		CanUndo = false;
		action?.Invoke();
	}

	/// <summary>Send the position to Audible. Failures are ignored: the next one replaces it.</summary>
	private void PushPosition()
	{
		// Only after listening or moving here: otherwise this device's old place would overwrite the Audible app's.
		if (annotations is not { } service || !settings.SyncPosition || lastLocalActivity is not { } activity || activity.UtcDateTime <= lastRemoteSave)
			return;
		lastRemoteSave = DateTime.UtcNow;
		var position = player.Position;
		_ = Task.Run(async () =>
		{
			try
			{
				await service.SetPositionAsync(Book.Id, position);
			}
			catch (Exception)
			{
				// Offline: the position is still saved on this device.
			}
		});
	}

	#endregion

	#region Bookmarks and clips

	[RelayCommand]
	private void AddBookmark()
	{
		var at = Position;
		var saved = SaveAnnotation(at, null);
		ShowStatus($"Bookmark added at {FormatTime(at)}.", () => RemoveBookmark(saved, at));
	}

	/// <summary>The clip being made, while its editor is open.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsClipEditorOpen))]
	private ClipEditorViewModel? clipEditor;

	public bool IsClipEditorOpen => ClipEditor is not null;

	private TimeSpan positionBeforeClip;
	private bool wasPlayingBeforeClip;

	/// <summary>Start a clip ending where the listener is now. Playback pauses while it is set up.</summary>
	[RelayCommand]
	private void AddClip()
	{
		if (Position < ClipEditorViewModel.MinimumLength)
		{
			ShowStatus("Play a little first, then clip what you just heard.");
			return;
		}
		wasPlayingBeforeClip = player.IsPlaying;
		if (wasPlayingBeforeClip)
			PlayPause();
		positionBeforeClip = Position;
		var editor = new ClipEditorViewModel(Position, Duration, TimeSpan.FromSeconds(settings.ClipSeconds))
		{
			ContextText = CurrentChapterRow is { } chapter ? $"{Title} · {chapter.Title}" : Title
		};
		ClipEditor = editor;
		_ = LoadPeaksAsync(editor);
	}

	/// <summary>Read the audio under the editor's window, off the UI thread, for its waveform.</summary>
	private async Task LoadPeaksAsync(ClipEditorViewModel editor)
	{
		const int BARS = 160;
		try
		{
			var peaks = await Task.Run(() =>
			{
				using var source = AudioBackend.OpenSource(Book.Path);
				source.Seek(TimeSpan.FromSeconds(editor.WindowStartSeconds));
				var frames = (long)((editor.WindowEndSeconds - editor.WindowStartSeconds) * source.SampleRate);
				var perBar = Math.Max(1, frames / BARS);
				var bars = new float[BARS];
				var buffer = new float[4096 * source.Channels];
				long frame = 0;
				int read;
				while (frame < frames && (read = source.Read(buffer)) > 0)
				{
					for (var i = 0; i + source.Channels <= read && frame < frames; i += source.Channels, frame++)
					{
						var bar = (int)Math.Min(BARS - 1, frame / perBar);
						var level = Math.Abs(buffer[i]);
						if (level > bars[bar])
							bars[bar] = level;
					}
				}
				// Square root, as loudness is heard: quiet speech still shows.
				var loudest = Math.Max(1e-4f, bars.Max());
				return bars.Select(b => (float)Math.Sqrt(b / loudest)).ToArray();
			});
			if (ClipEditor == editor)
				editor.Peaks = peaks;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Clip waveform not drawn: {ex.Message}");
		}
	}

	/// <summary>Where preview playback is, for the waveform's line: NaN when not previewing.</summary>
	public double ClipPreviewSeconds => ClipEditor is not null && IsPlaying ? Position.TotalSeconds : double.NaN;

	private static readonly TimeSpan ClipEndTolerance = TimeSpan.FromSeconds(0.3);

	/// <summary>Play or pause inside the clip being made. Playing from its end starts it again.</summary>
	[RelayCommand]
	private void PreviewClip()
	{
		if (ClipEditor is not { } editor)
			return;
		if (!player.IsPlaying && (Position < editor.Start || Position >= editor.End - ClipEndTolerance))
			Seek(editor.Start);
		PlayPause();
	}

	/// <summary>Where playback is inside the clip being made, for its slider. Setting it seeks.</summary>
	public double ClipPlayheadSeconds
	{
		get => ClipEditor is { } editor ? Math.Clamp(Position.TotalSeconds, editor.StartSeconds, editor.EndSeconds) : 0;
		set
		{
			if (IsScrubbing && ClipEditor is not null && Math.Abs(value - Position.TotalSeconds) >= 0.5)
				Seek(TimeSpan.FromSeconds(value));
		}
	}

	public string ClipPlayheadText => ClipEditor is { } editor
		? $"{Math.Max(0, ClipPlayheadSeconds - editor.StartSeconds):0} of {(editor.End - editor.Start).TotalSeconds:0} s"
		: string.Empty;

	[RelayCommand]
	private void SaveClip()
	{
		if (ClipEditor is not { } editor)
			return;
		var (start, end) = editor.Range;
		SaveAnnotation(start, end, Clean(editor.Title), Clean(editor.Note));
		CloseClipEditor();
		ShowStatus($"Clip saved, {(end - start).TotalSeconds:0.0} seconds.");
	}

	[RelayCommand]
	private void CancelClip() => CloseClipEditor();

	/// <summary>Back to where the listener was, playing again if they were.</summary>
	private void CloseClipEditor()
	{
		if (ClipEditor is null)
			return;
		ClipEditor = null;
		if (player.IsPlaying)
			PlayPause();
		Seek(positionBeforeClip);
		if (wasPlayingBeforeClip)
			PlayPause();
	}

	private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

	private LocalAnnotation? SaveAnnotation(TimeSpan start, TimeSpan? end, string? title = null, string? note = null)
	{
		var saved = localAnnotations?.Add(Book.Id, start, end, title, note);
		ShowAnnotationRows();

		// Also offer it to Audible's annotation server, which keeps it only for some books.
		if (annotations is { } service)
			_ = Task.Run(async () =>
			{
				try
				{
					await service.TryAddToServerAsync(Book.Id, start, end, title, note);
				}
				catch (Exception)
				{
					// Saved on this device regardless.
				}
			});
		return saved;
	}

	/// <summary>A bookmark just added, taken back: here, and from Audible's server once its copy shows up there.</summary>
	private void RemoveBookmark(LocalAnnotation? saved, TimeSpan at)
	{
		if (saved is not null)
			localAnnotations?.Remove(Book.Id, saved.Id);
		ShowAnnotationRows();
		ShowStatus("Bookmark removed.");
		if (annotations is not { } service)
			return;
		_ = Task.Run(async () =>
		{
			try
			{
				var records = await service.GetServerAnnotationsAsync(Book.Id);
				foreach (var record in records.Where(r => r is not Clip && IsSame(r.Start, null, at, null)))
					await service.DeleteFromServerAsync(Book.Id, record);
			}
			catch (Exception)
			{
				// Offline: only the copy here went.
			}
		});
	}

	[RelayCommand]
	private async Task DeleteAnnotation(AnnotationRowViewModel row)
	{
		if (row.Local is { } local)
			localAnnotations?.Remove(Book.Id, local.Id);

		// Remove the server's copy too, whether the row came from there or was mirrored there.
		var onServer = row.Server ?? serverAnnotations.FirstOrDefault(r => IsSame(r.Start, (r as Clip)?.End, row.Start, row.End));
		if (onServer is not null && annotations is { } service)
		{
			try
			{
				await service.DeleteFromServerAsync(Book.Id, onServer);
				serverAnnotations = serverAnnotations.Where(r => r != onServer).ToList();
			}
			catch (Exception)
			{
				if (row.Local is null)
					ShowStatus("That one is in your Audible account. Deleting it needs a connection.");
			}
		}
		ShowAnnotationRows();
	}

	/// <summary>Jump to a bookmark, or play a clip from its start and stop at its end.</summary>
	[RelayCommand]
	private void OpenAnnotation(AnnotationRowViewModel row)
	{
		seekSource = "bookmark";
		Seek(row.Start);
		clipEnd = row.End;
		IsAnnotationListOpen = false;
		if (!player.IsPlaying)
			PlayPause();
	}

	[RelayCommand]
	private void ShowAnnotations()
	{
		IsAnnotationListOpen = true;
		_ = LoadServerAnnotationsAsync();
	}

	[RelayCommand]
	private void HideAnnotations() => IsAnnotationListOpen = false;

	private async Task LoadServerAnnotationsAsync()
	{
		if (annotations is null)
			return;
		try
		{
			serverAnnotations = await annotations.GetServerAnnotationsAsync(Book.Id);
		}
		catch (Exception)
		{
			// Offline: show what is saved on this device.
			return;
		}
		if (!disposed)
			ShowAnnotationRows();
	}

	/// <summary>Everything saved on this device, plus anything on the server that is not already among it.</summary>
	private void ShowAnnotationRows()
	{
		string? chapterAt(TimeSpan start) => HasChapters ? Chapters.LastOrDefault(c => c.StartOffset <= start)?.Title : null;

		var local = localAnnotations?.Get(Book.Id) ?? [];
		// Creating a clip on the server also creates a bookmark at the same place; show only the clip.
		var serverClipStarts = serverAnnotations.OfType<Clip>().Select(c => c.Start).ToHashSet();
		var serverOnly = serverAnnotations
			.Where(r => r is Clip || !serverClipStarts.Contains(r.Start))
			.Where(r => !local.Any(l => IsSame(l.Start, l.End, r.Start, (r as Clip)?.End)));

		var rows = local.Select(l => new AnnotationRowViewModel(l, chapterAt(l.Start)))
			.Concat(serverOnly.Select(r => new AnnotationRowViewModel(r, chapterAt(r.Start))))
			.OrderBy(r => r.Start);

		Annotations.Clear();
		foreach (var row in rows)
			Annotations.Add(row);
	}

	private static bool IsSame(TimeSpan startA, TimeSpan? endA, TimeSpan startB, TimeSpan? endB)
		=> (endA is null) == (endB is null) && (startA - startB).Duration() < SameAnnotationTolerance;

	#endregion

	/// <summary>Called on each tick: stop at the end of a clip being played, and sync the position now and then.</summary>
	private void UpdateAnnotations()
	{
		if (ClipEditor is { } editor)
		{
			// Keep playback inside the clip as it is played and as its ends are moved.
			if (player.IsPlaying && Position >= editor.End)
				PlayPause();
			else if (player.IsPlaying && Position < editor.Start)
				Seek(editor.Start);
			OnPropertyChanged(nameof(ClipPlayheadSeconds));
			OnPropertyChanged(nameof(ClipPlayheadText));
			OnPropertyChanged(nameof(ClipPreviewSeconds));
		}
		else if (clipEnd is TimeSpan end && Position >= end)
		{
			clipEnd = null;
			if (player.IsPlaying)
				PlayPause();
		}
		if (IsPlaying)
		{
			lastLocalActivity = DateTimeOffset.UtcNow;
			if (DateTime.UtcNow - lastRemoteSave > RemoteSaveInterval)
				PushPosition();
		}
		else if (DateTime.UtcNow - lastRemoteCheck > RemoteCheckInterval)
			_ = SyncPositionAsync();
	}

	private void ShowStatus(string message, Action? undo = null)
	{
		StatusMessage = message;
		this.undo = undo;
		CanUndo = undo is not null;
		statusTimer?.Stop();
		statusTimer = new DispatcherTimer(StatusDuration, DispatcherPriority.Normal, (_, _) =>
		{
			statusTimer?.Stop();
			StatusMessage = null;
			undo = null;
			CanUndo = false;
		});
		statusTimer.Start();
	}
}
