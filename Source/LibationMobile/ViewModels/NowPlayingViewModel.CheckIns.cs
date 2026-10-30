using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

/// <summary>
/// A question at each end of a session, for the trainer to learn from (Services/Experiments.cs, SessionCheckIn): at the
/// start of a sitting how alert the listener is, at the end how well they followed, both 0 to 4, each kept with the
/// session's context. Pausing on the headphones asks the end question aloud and listens for the answer.
/// </summary>
public partial class NowPlayingViewModel
{
	/// <summary>A start question is asked only after a break this long: a new sitting, not a pause.</summary>
	private static readonly TimeSpan NewSittingGap = TimeSpan.FromHours(1);
	/// <summary>A pause this soon after a headphone button press came from it.</summary>
	private static readonly TimeSpan ButtonPauseWindow = TimeSpan.FromSeconds(3);
	private const string END_QUESTION = "How much did you follow? Zero to four.";

	private sealed record PendingCheckIn(string Moment, DateTimeOffset At, SessionContext Context, Task<ActivityContext?>? Activity);

	private double sessionStartSpeed, sessionMinSpeed, sessionMaxSpeed;
	private int sessionSkipsBack, sessionSpeedChanges;
	private double? sessionHoursSince;
	private PendingCheckIn? pendingStart, pendingEnd;
	private CancellationTokenSource? voiceCancel;

	[ObservableProperty]
	private bool isAskingAlertness;

	/// <summary>The end question has been asked aloud and the microphone is listening.</summary>
	[ObservableProperty]
	private bool isListeningForAnswer;

	/// <summary>A session has started: note where it starts from, and ask how alert the listener is at a new sitting.</summary>
	private void StartCheckIns()
	{
		sessionStartSpeed = sessionMinSpeed = sessionMaxSpeed = Speed;
		sessionSkipsBack = sessionSpeedChanges = 0;
		sessionHoursSince = HoursSinceLastSession();
		// Playing again instead of answering: the last session's question goes unanswered.
		voiceCancel?.Cancel();
		Record(ref pendingEnd, null, "none");
		Record(ref pendingStart, null, "none");
		FollowToRate = null;
		IsAskingAlertness = false;
		if (Experiments is null || !settings.AskAlertness || TrialPart != 0 || sessionHoursSince < NewSittingGap.TotalHours)
			return;
		pendingStart = new PendingCheckIn("start", DateTimeOffset.Now, Context(0, null), null);
		IsAskingAlertness = true;
	}

	/// <summary>From the event log: what a session's context counts.</summary>
	private void NoteSessionEvent(string kind, double? value)
	{
		if (sessionStarted is null)
			return;
		if (kind == "skip" && value < 0)
		{
			sessionSkipsBack++;
			NoteProgressSkipBack();
		}
		else if (kind == "speed" && value is double speed)
		{
			sessionSpeedChanges++;
			sessionMinSpeed = Math.Min(sessionMinSpeed, speed);
			sessionMaxSpeed = Math.Max(sessionMaxSpeed, speed);
		}
	}

	/// <summary>
	/// A session is ending: keep its context, and ask how well it was followed if it was long enough, aloud as well
	/// when the pause came from the headphones.
	/// </summary>
	private void OfferEndCheckIn(TimeSpan spent, double syllablesHeard)
	{
		if (Experiments is null || sessionStarted is not DateTimeOffset started || spent < ShortestLoggedSession)
			return;
		voiceCancel?.Cancel();
		Record(ref pendingEnd, null, "none");
		Record(ref pendingStart, null, "none");
		IsAskingAlertness = false;

		var context = Context(spent.TotalMinutes, syllablesHeard > 0 ? syllablesHeard / spent.TotalSeconds : null);
		var activity = ListeningContext.Platform?.ActivityAsync(started, DateTimeOffset.Now);
		PendingCheckIn? end = new("end", DateTimeOffset.Now, context, activity);
		if (!settings.AskFollowRating || TrialPart != 0 || spent < RatedSessionLength)
		{
			Record(ref end, null, "not asked");
			return;
		}
		pendingEnd = end!;
		FollowToRate = context.Settings;
		if (settings.VoicePrompts && VoicePrompt.Platform is { } voice && mediaSession is { } media
			&& DateTime.UtcNow - media.LastButtonPress < ButtonPauseWindow)
			_ = AskByVoiceAsync(voice, pendingEnd);
	}

	private async Task AskByVoiceAsync(IVoicePrompt voice, PendingCheckIn asked)
	{
		using var cancel = new CancellationTokenSource();
		voiceCancel = cancel;
		IsListeningForAnswer = true;
		int? answer = null;
		try
		{
			answer = await voice.AskNumberAsync(END_QUESTION, 4, cancel.Token);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Console.WriteLine($"Voice prompt failed: {ex.Message}");
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			IsListeningForAnswer = false;
			if (voiceCancel == cancel)
				voiceCancel = null;
		}
		// Unheard: the question stays on the screen.
		if (answer is int follow && !disposed && ReferenceEquals(pendingEnd, asked))
			AnswerEnd(follow, "voice");
	}

	/// <summary>Answered on screen, 0 to 4.</summary>
	[RelayCommand]
	private void RateFollow(string score)
	{
		if (int.TryParse(score, out var follow))
			AnswerEnd(follow, "screen");
	}

	[RelayCommand]
	private void SkipFollow()
	{
		voiceCancel?.Cancel();
		Record(ref pendingEnd, null, "skipped");
		FollowToRate = null;
	}

	private void AnswerEnd(int follow, string by)
	{
		voiceCancel?.Cancel();
		// Also as a rating, 1 to 5, so the results by speed carry on from the ratings before check-ins.
		if (FollowToRate is { } rated)
			Experiments?.Add(new FollowRating(DateTimeOffset.Now, Book.Id, Title, follow + 1, rated));
		Record(ref pendingEnd, follow, by);
		FollowToRate = null;
	}

	[RelayCommand]
	private void RateAlertness(string score)
	{
		if (int.TryParse(score, out var alert))
			Record(ref pendingStart, alert, "screen");
		IsAskingAlertness = false;
	}

	[RelayCommand]
	private void SkipAlertness()
	{
		Record(ref pendingStart, null, "skipped");
		IsAskingAlertness = false;
	}

	/// <summary>Keep a check-in, with what the phone's motion record says once it has answered, and clear it.</summary>
	private void Record(ref PendingCheckIn? pending, int? answer, string by)
	{
		if (pending is not { } checkIn || Experiments is not { } experiments)
		{
			pending = null;
			return;
		}
		pending = null;
		var bookId = Book.Id;
		var title = Title;
		_ = Task.Run(async () =>
		{
			var context = checkIn.Context;
			if (checkIn.Activity is { } activity && await Task.WhenAny(activity, Task.Delay(TimeSpan.FromSeconds(5))) == activity
				&& activity.Result is { } found)
				context = context with { Activity = found.Steps is int steps ? $"{found.Summary}, {steps} steps" : found.Summary };
			experiments.Add(new SessionCheckIn(checkIn.At, bookId, title, checkIn.Moment, answer, by, context));
		});
	}

	/// <summary>The session's context so far.</summary>
	private SessionContext Context(double minutes, double? syllablesPerSecond)
	{
		var now = DateTime.Now;
		return new SessionContext(now.Hour, now.DayOfWeek.ToString(), sessionHoursSince is double h ? Math.Round(h, 2) : null,
			Math.Round(minutes, 1), Math.Round(sessionStartSpeed, 2), Math.Round(sessionMinSpeed, 2), Math.Round(sessionMaxSpeed, 2),
			syllablesPerSecond is double rate ? Math.Round(rate, 1) : null, sessionSkipsBack, sessionSpeedChanges, AudioBackend.Route?.Invoke(),
			ListeningMode, planner?.Plan, planner?.Done.Count ?? 0,
			Duration > TimeSpan.Zero ? Math.Round(Position / Duration, 3) : null, CurrentListening(syllablesPerSecond));
	}

	/// <summary>Hours since the last session of any book ended, before this one; null if there was none.</summary>
	private double? HoursSinceLastSession()
	{
		var last = (log?.Sessions ?? []).Where(s => s.Started != sessionStarted).Select(s => (DateTimeOffset?)s.Ended).Max();
		return last is DateTimeOffset ended ? Math.Max(0, (DateTimeOffset.Now - ended).TotalHours) : null;
	}

	/// <summary>Closing the book: unanswered questions are kept as such.</summary>
	private void EndCheckIns()
	{
		voiceCancel?.Cancel();
		Record(ref pendingStart, null, "none");
		Record(ref pendingEnd, null, "none");
	}

#if DEBUG
	/// <summary>Debug builds only: show a question's card, for a screenshot. Nothing is recorded.</summary>
	internal void ShowCheckInForTest(string moment)
	{
		if (moment == "start")
			IsAskingAlertness = true;
		else
			FollowToRate = CurrentListening();
	}
#endif
}
