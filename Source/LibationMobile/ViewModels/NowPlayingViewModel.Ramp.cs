using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace LibationMobile.ViewModels;

/// <summary>
/// One general listening mode, in place of training plans: a warm-up at the start of each sitting, easing up to the
/// listener's own speed, and slow progress, a small step up after each hour that went well. Both work on the syllable
/// rate when following the narrator, and on the speed otherwise. Neither asks anything; the skip-back button is the
/// only signal used, as skipping back after fast listening is what missing something looks like.
/// </summary>
public partial class NowPlayingViewModel
{
	/// <summary>A gap this long since the last listening makes a new sitting, which warms up again.</summary>
	private static readonly TimeSpan NewSittingAfter = TimeSpan.FromMinutes(10);

	/// <summary>How often the warm-up moves the speed, when not following the narrator.</summary>
	private static readonly TimeSpan WarmUpInterval = TimeSpan.FromSeconds(5);

	/// <summary>An hour of listening per step when following the narrator; two when stepping the speed, whose steps are coarser.</summary>
	private TimeSpan ProgressStepAfter => settings.UseTargetSyllableRate ? TimeSpan.FromHours(1) : TimeSpan.FromHours(2);

	/// <summary>Syllables a second added, or taken off, at a step: about 1% at the rates fast listeners use.</summary>
	private const double PROGRESS_RATE_STEP = 0.2;
	private const double PROGRESS_SPEED_STEP = 0.1;
	/// <summary>Up to this many skips back in a step's listening still counts as going well.</summary>
	private const int PROGRESS_SKIPS_OK = 2;
	/// <summary>This many or more steps it back down.</summary>
	private const int PROGRESS_SKIPS_TOO_MANY = 6;

	/// <summary>Listening done in the warm-up so far; null when not warming up.</summary>
	private TimeSpan? warmUpListened;
	/// <summary>The speed the warm-up climbs to, when not following the narrator.</summary>
	private double warmUpTarget;
	private DateTime nextWarmUpStep;
	/// <summary>Listening not yet added to the saved progress, which is saved once a minute rather than four times a second.</summary>
	private TimeSpan unsavedProgress;

	public bool IsWarmingUp => warmUpListened is not null;

	/// <summary>0 at the start of the warm-up, 1 once done.</summary>
	private double WarmUpDone => warmUpListened is TimeSpan done ? Math.Clamp(done.TotalMinutes / settings.WarmUpMinutes, 0, 1) : 1;

	/// <summary>What the speed, or the rate, is multiplied by now: the warm-up's share below full.</summary>
	private double WarmUpFactor => 1 - settings.WarmUpPercent / 100.0 * (1 - WarmUpDone);

	/// <summary>The syllable rate to follow now: the target, less the warm-up.</summary>
	private double TargetRateNow => settings.TargetSyllableRate * WarmUpFactor;

	/// <summary>The chip under the player: what the mode is doing.</summary>
	[ObservableProperty]
	private string rampChipText = "";

	/// <summary>In the Listening sheet: where the warm-up and the progress have got to.</summary>
	[ObservableProperty]
	private string rampStatusText = "";

	public bool IsRampOn => settings.WarmUp || settings.Progress;

	public bool WarmUpEnabled
	{
		get => settings.WarmUp;
		set
		{
			settings.WarmUp = value;
			if (!value)
				EndWarmUp();
			LogEvent("mode", detail: value ? "warm-up on" : "warm-up off");
			RefreshRamp();
		}
	}

	public bool ProgressEnabled
	{
		get => settings.Progress;
		set
		{
			settings.Progress = value;
			LogEvent("mode", detail: value ? "progress on" : "progress off");
			RefreshRamp();
		}
	}

	public string WarmUpPercentText => $"{settings.WarmUpPercent:0}% slower";
	public string WarmUpMinutesText => $"over {settings.WarmUpMinutes:0} min";
	public string ProgressCeilingText => settings.UseTargetSyllableRate ? $"up to {settings.ProgressCeilingRate:0} syl/s" : $"up to {settings.ProgressCeilingSpeed:0.0}×";

	/// <summary>"percent", "minutes" or "ceiling", then "+" or "-", as "percent+".</summary>
	[RelayCommand]
	private void NudgeRamp(string what)
	{
		var up = what.EndsWith('+');
		switch (what[..^1])
		{
			case "percent":
				settings.WarmUpPercent += up ? 5 : -5;
				break;
			case "minutes":
				settings.WarmUpMinutes += up ? 1 : -1;
				break;
			case "ceiling" when settings.UseTargetSyllableRate:
				settings.ProgressCeilingRate += up ? 1 : -1;
				break;
			case "ceiling":
				settings.ProgressCeilingSpeed += up ? 0.5f : -0.5f;
				break;
		}
		RefreshRamp();
	}

	private void RefreshRamp()
	{
		OnPropertyChanged(nameof(IsRampOn));
		OnPropertyChanged(nameof(WarmUpEnabled));
		OnPropertyChanged(nameof(ProgressEnabled));
		OnPropertyChanged(nameof(WarmUpPercentText));
		OnPropertyChanged(nameof(WarmUpMinutesText));
		OnPropertyChanged(nameof(ProgressCeilingText));
		OnPropertyChanged(nameof(IsWarmingUp));
		UpdateRampText();
	}

	/// <summary>A session has started: a new sitting warms up, if the warm-up is on and the old training plans are not.</summary>
	private void StartWarmUp()
	{
		if (!settings.WarmUp || settings.Training || IsTrialRunning)
			return;
		if (HoursSinceLastSession() is double hours && hours < NewSittingAfter.TotalHours)
			return;
		warmUpListened = TimeSpan.Zero;
		warmUpTarget = settings.GetBookSpeed(Book.Id) ?? Speed;
		nextWarmUpStep = DateTime.MinValue;
		LogEvent("mode", detail: "warming up");
		StepWarmUp();
		OnPropertyChanged(nameof(IsWarmingUp));
	}

	/// <summary>Stop warming up: done, turned off, or the listener set the speed themselves, which they then keep.</summary>
	private void EndWarmUp()
	{
		if (warmUpListened is null)
			return;
		warmUpListened = null;
		OnPropertyChanged(nameof(IsWarmingUp));
		UpdateRampText();
	}

	/// <summary>Move the speed along the warm-up. Following the narrator, the rate follower does it from <see cref="TargetRateNow"/>.</summary>
	private void StepWarmUp()
	{
		if (warmUpListened is null)
			return;
		if (settings.UseTargetSyllableRate)
		{
			// Straight to the warm-up's rate at the start; the follower eases it from there.
			if (warmUpListened == TimeSpan.Zero)
				FollowTargetRate(now: true);
			return;
		}
		if (DateTime.UtcNow < nextWarmUpStep)
			return;
		nextWarmUpStep = DateTime.UtcNow + WarmUpInterval;
		speedSource = "warm-up";
		ApplySpeed(Math.Max(MIN_SPEED, warmUpTarget * WarmUpFactor), save: false);
		speedSource = "you";
	}

	/// <summary>Each stretch of listening: move the warm-up on, and count towards the next step.</summary>
	private void RampTick(TimeSpan listened)
	{
		if (warmUpListened is TimeSpan done)
		{
			warmUpListened = done + listened;
			StepWarmUp();
			if (WarmUpDone >= 1)
			{
				if (!settings.UseTargetSyllableRate)
				{
					speedSource = "warm-up";
					ApplySpeed(warmUpTarget, save: false);
					speedSource = "you";
				}
				LogEvent("mode", detail: "warmed up");
				EndWarmUp();
			}
		}
		else if (settings.Progress && !settings.Training && !IsBlindMode)
		{
			unsavedProgress += listened;
			if (unsavedProgress >= TimeSpan.FromMinutes(1))
			{
				settings.ProgressSeconds += unsavedProgress.TotalSeconds;
				unsavedProgress = TimeSpan.Zero;
				if (settings.ProgressSeconds >= ProgressStepAfter.TotalSeconds)
					TakeProgressStep();
			}
		}
		if (++rampTicks % 4 == 0)
			UpdateRampText();
	}

	private int rampTicks;

	/// <summary>Skipped back during listening that counts towards a step.</summary>
	private void NoteProgressSkipBack()
	{
		if (settings.Progress && warmUpListened is null)
			settings.ProgressSkipsBack++;
	}

	/// <summary>An hour (or two) done: up a step if it went well, down if it did not, else hold.</summary>
	private void TakeProgressStep()
	{
		var skips = settings.ProgressSkipsBack;
		settings.ProgressSeconds = 0;
		settings.ProgressSkipsBack = 0;
		var direction = skips <= PROGRESS_SKIPS_OK ? 1 : skips >= PROGRESS_SKIPS_TOO_MANY ? -1 : 0;
		var hours = ProgressStepAfter.TotalHours == 1 ? "an hour" : $"{ProgressStepAfter.TotalHours:0} hours";
		if (direction == 0)
		{
			LogEvent("progress", value: 0, detail: $"held, {skips} skips back");
			ShowStatus($"{skips} skips back in {hours}: holding where you are.");
			return;
		}
		if (settings.UseTargetSyllableRate)
		{
			var before = settings.TargetSyllableRate;
			var after = Math.Clamp(before + direction * PROGRESS_RATE_STEP, MIN_TARGET_RATE, Math.Max(before, settings.ProgressCeilingRate));
			if (Math.Abs(after - before) < 0.01)
				return;
			TargetRate = after;
			LogEvent("progress", value: after, detail: $"{skips} skips back");
			ShowStatus(direction > 0
				? $"{hours} with {Few(skips)}: up to {after:0.0} syllables a second."
				: $"{skips} skips back in {hours}: down to {after:0.0} syllables a second.", () => TargetRate = before);
		}
		else
		{
			var before = Speed;
			var after = Math.Clamp(Math.Round(before + direction * PROGRESS_SPEED_STEP, 1), MIN_SPEED, Math.Max(before, settings.ProgressCeilingSpeed));
			if (Math.Abs(after - before) < 0.01)
				return;
			speedSource = "progress";
			ApplySpeed(after, save: true);
			speedSource = "you";
			LogEvent("progress", value: after, detail: $"{skips} skips back");
			ShowStatus(direction > 0
				? $"{hours} with {Few(skips)}: up to {after:0.0}×."
				: $"{skips} skips back in {hours}: down to {after:0.0}×.", () => ApplySpeed(before, save: true));
		}
	}

	private static string Few(int skips) => skips == 0 ? "no skips back" : skips == 1 ? "one skip back" : $"{skips} skips back";

	private void UpdateRampText()
	{
		if (warmUpListened is TimeSpan done)
		{
			var left = TimeSpan.FromMinutes(settings.WarmUpMinutes) - done;
			RampChipText = $"Warming up · {Math.Max(0, left.TotalMinutes):0}:{Math.Max(0, left.Seconds):00}";
		}
		else if (settings.Progress)
			RampChipText = "Progressing";
		else if (settings.WarmUp)
			RampChipText = "Warm-up";
		else
			RampChipText = "Listening";

		var lines = new System.Collections.Generic.List<string>();
		if (warmUpListened is not null)
			lines.Add(settings.UseTargetSyllableRate
				? $"Warming up: {TargetRateNow:0.0} of {settings.TargetSyllableRate:0.0} syllables a second."
				: $"Warming up: {Speed:0.0}× of {warmUpTarget:0.0}×.");
		if (settings.Progress)
		{
			var toGo = ProgressStepAfter - TimeSpan.FromSeconds(settings.ProgressSeconds) - unsavedProgress;
			lines.Add($"Next step in {Math.Max(0, (int)toGo.TotalMinutes)} min of listening; {Few(settings.ProgressSkipsBack)} so far.");
		}
		RampStatusText = string.Join(" ", lines);
	}
}
