using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LibationMobile.ViewModels;

/// <summary>A place to go back to: where in the book the listener was, and when.</summary>
/// <param name="asleep">Where the listener probably fell asleep: shown apart, as the place to go back to.</param>
public class HistoryPointViewModel(DateTimeOffset at, TimeSpan position, bool asleep = false)
{
	public TimeSpan Position { get; } = position;
	public string TimeText { get; } = asleep ? $"Asleep {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}" : at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
	public string PositionText { get; } = NowPlayingViewModel.FormatTime(position);
	public bool IsAsleep { get; } = asleep;
}

/// <summary>One session of this book: when, how far it went, and the points along the way.</summary>
public class HistorySessionViewModel
{
	public string Header { get; }
	public string DetailText { get; }
	public bool IsCurrent { get; }
	public IReadOnlyList<HistoryPointViewModel> Points { get; }
	public string AsleepText { get; } = "";
	public bool HasAsleep => AsleepText.Length > 0;

	/// <summary>Heard in another app, as the Audible app: only where it started and ended are known, not how it went.</summary>
	public bool IsElsewhere { get; }

	/// <summary>When it ended, for putting sessions here and elsewhere in order.</summary>
	public DateTimeOffset SortTime { get; }

	public HistorySessionViewModel(ElsewhereListening elsewhere)
	{
		var day = elsewhere.At.ToLocalTime().Date;
		var dayText = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("ddd d MMM", CultureInfo.CurrentCulture);
		Header = $"{dayText}, by {elsewhere.At.ToLocalTime():t}";
		DetailText = $"{NowPlayingViewModel.FormatTime(elsewhere.From)} to {NowPlayingViewModel.FormatTime(elsewhere.To)}, "
			+ $"{LogEntryViewModel.Describe(elsewhere.To - elsewhere.From)} of the book";
		IsElsewhere = true;
		SortTime = elsewhere.At;
		Points = [new(elsewhere.At, elsewhere.To), new(elsewhere.Since, elsewhere.From)];
	}

	public HistorySessionViewModel(ListeningSession session, bool isCurrent)
	{
		SortTime = session.Started;
		var day = session.Started.ToLocalTime().Date;
		var dayText = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("ddd d MMM", CultureInfo.CurrentCulture);
		var end = isCurrent ? "now" : session.Ended.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
		Header = $"{dayText}, {session.Started.ToLocalTime():t} to {end}";
		DetailText = $"{NowPlayingViewModel.FormatTime(session.From)} to {NowPlayingViewModel.FormatTime(session.To)} at {session.Speed:0.0}×";
		IsCurrent = isCurrent;
		// Newest first, like the sessions: the last place you remember is near the top.
		var points = new List<HistoryPointViewModel> { new(session.Started, session.From) };
		points.AddRange((session.Marks ?? []).Select(m => new HistoryPointViewModel(m.At, m.Position)));
		points.Add(new(session.Ended, session.To));
		if (session.AsleepAt is DateTimeOffset asleepAt && session.AsleepPosition is TimeSpan asleepPosition)
		{
			points.Add(new(asleepAt, asleepPosition, asleep: true));
			points.Sort((a, b) => a.Position.CompareTo(b.Position));
			AsleepText = session.AsleepSource == "health"
				? $"Asleep from about {asleepAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}, from your watch's sleep record."
				: $"Auto-paused as you seemed to be asleep; last touched at {asleepAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.";
		}
		points.Reverse();
		Points = points;
	}
}

/// <summary>
/// This book's listening history, like Audible's: every session with the time and place it started and ended, and where
/// you were every few minutes, so after dozing off you can find the last part you heard.
/// </summary>
public partial class NowPlayingViewModel
{
	/// <summary>How often a session notes where it is, and saves itself in case the app is closed before it ends.</summary>
	private static readonly TimeSpan MarkInterval = TimeSpan.FromMinutes(5);
	private readonly List<ListeningMark> sessionMarks = [];
	private DateTimeOffset nextMark;

	[ObservableProperty]
	private bool isHistoryOpen;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasHistory))]
	private IReadOnlyList<HistorySessionViewModel> history = [];

	public bool HasHistory => History.Count > 0;

	[RelayCommand]
	private void ShowHistory()
	{
		RefreshHistory();
		IsHistoryOpen = true;
		// A watch's sleep reaches Apple Health after it syncs: look again, and show any place it finds.
		if (settings.UseHealthSleep && log is { } l && ListeningContext.Platform is { } context)
			_ = System.Threading.Tasks.Task.Run(async () =>
			{
				if (await SleepFinder.RefineAsync(l, context) > 0)
					Avalonia.Threading.Dispatcher.UIThread.Post(RefreshHistory);
			});
	}

	[RelayCommand]
	private void HideHistory() => IsHistoryOpen = false;

	/// <summary>Go back to a point in the history. Where you are now goes into the history first, so you can come back.</summary>
	[RelayCommand]
	private void JumpToHistory(HistoryPointViewModel point)
	{
		endingForJump = true;
		EndSession();
		endingForJump = false;
		seekSource = "history";
		Seek(point.Position);
		IsHistoryOpen = false;
	}

	private void RefreshHistory()
	{
		var sessions = ListeningLog.Joined((log?.Sessions ?? []).Where(s => s.BookId == Book.Id && s.Started != sessionStarted), SessionResumeGap)
			.Select(s => new HistorySessionViewModel(s, isCurrent: false))
			.Concat((log?.Elsewhere ?? []).Where(e => e.BookId == Book.Id).Select(e => new HistorySessionViewModel(e)))
			.OrderByDescending(s => s.SortTime);
		History = (CurrentSession() is { } current ? sessions.Prepend(new HistorySessionViewModel(current, isCurrent: true)) : sessions).ToList();
	}

	/// <summary>The session under way, as it stands now. Null when not listening.</summary>
	private ListeningSession? CurrentSession()
		=> sessionStarted is DateTimeOffset started
			? new ListeningSession(Book.Id, Title, started, DateTimeOffset.Now, sessionFrom, Position,
				sessionBookTime.TotalSeconds, sessionSpent.TotalSeconds, (float)Speed, sessionMarks.ToList(),
				sessionSyllables > 0 ? Math.Round(sessionSyllables) : null)
			: null;

	private void StartMarks()
	{
		sessionMarks.Clear();
		nextMark = DateTimeOffset.Now + MarkInterval;
	}

	/// <summary>Every few minutes of a session: note the place, and save the session so far.</summary>
	private void MarkIfDue()
	{
		var now = DateTimeOffset.Now;
		if (sessionStarted is null || now < nextMark)
			return;
		nextMark = now + MarkInterval;
		sessionMarks.Add(new ListeningMark(now, Position));
		if (CurrentSession() is { } session)
			log?.Add(session);
	}
}
