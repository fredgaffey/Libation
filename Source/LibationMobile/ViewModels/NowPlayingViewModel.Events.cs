using LibationMobile.Services;
using System;

namespace LibationMobile.ViewModels;

/// <summary>
/// The event log for the listener's research (Services/ListeningEvents.cs): play and pause, every speed change and who
/// made it, skips and seeks, chapters, mode changes, and the audio route when it changes. Nothing is sampled: position
/// and speed at any moment follow from these events, and the syllable rate at any point is a property of the audio,
/// measurable from the file at any time. Each event also notes the battery. Written as it happens, so nothing runs just
/// to collect it.
/// </summary>
public partial class NowPlayingViewModel
{
	/// <summary>Where events go. Set by the main view model.</summary>
	public ListeningEvents? Events { get; set; }

	/// <summary>Who is changing the speed or position next: set just before, read once by the change.</summary>
	private string speedSource = "you";
	private string seekSource = "scrubber";

	private DateTime? pausedAt;
	private string? lastRoute;

	private void LogEvent(string kind, double? value = null, string? detail = null)
	{
		NoteTouch(kind, detail);
		NoteSessionEvent(kind, value);
		var battery = ListeningContext.Platform?.Battery;
		Events?.Add(new ListeningEvent(DateTimeOffset.Now, Book.Id, kind, Math.Round(Position.TotalSeconds, 1), Math.Round(Speed, 2),
			value is double v ? Math.Round(v, 2) : null, detail, AudioBackend.Route?.Invoke(), IsBlindMode, battery?.Percent, battery?.Charging));
	}

	/// <summary>A speed change, logged with who made it; the next one is the listener's unless said otherwise.</summary>
	private void LogSpeedChange()
	{
		LogEvent("speed", Speed, speedSource);
		speedSource = "you";
	}

	private void LogSeek(TimeSpan from, TimeSpan to)
	{
		var moved = (to - from).TotalSeconds;
		if (Math.Abs(moved) >= 1)
			LogEvent(seekSource == "skip" ? "skip" : "seek", moved, seekSource == "skip" ? null : seekSource);
		seekSource = "scrubber";
	}

	/// <summary>
	/// What the listener was doing over a stretch just ended, from the phone's own motion record, logged as "activity"
	/// (Detail the mix, Value the steps). Asked after the fact, so it costs nothing while listening.
	/// </summary>
	private void LogActivity(DateTimeOffset from, DateTimeOffset to, string over)
	{
		if (ListeningContext.Platform is not { } context || Events is not { } events)
			return;
		var bookId = Book.Id;
		var position = Math.Round(Position.TotalSeconds, 1);
		var speed = Math.Round(Speed, 2);
		var blind = IsBlindMode;
		_ = System.Threading.Tasks.Task.Run(async () =>
		{
			if (await context.ActivityAsync(from, to) is { } activity)
				events.Add(new ListeningEvent(to, bookId, "activity", position, speed, activity.Steps, $"{over}: {activity.Summary}", null, blind));
		});
	}

	/// <summary>From Update: play and pause as they happen, and the audio route when it changes while playing.</summary>
	private void LogPlayback(bool wasPlaying)
	{
		if (IsPlaying && !wasPlaying)
		{
			LogEvent("play", pausedAt is DateTime paused ? (DateTime.UtcNow - paused).TotalSeconds : null);
			pausedAt = null;
			lastRoute = AudioBackend.Route?.Invoke();
		}
		else if (!IsPlaying && wasPlaying)
		{
			LogEvent("pause");
			pausedAt = DateTime.UtcNow;
		}
		else if (IsPlaying && AudioBackend.Route?.Invoke() is { } route && route != lastRoute)
		{
			lastRoute = route;
			LogEvent("route", detail: route);
		}
	}
}
