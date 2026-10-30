using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace LibationMobile.ViewModels;

/// <summary>
/// Speed by syllable rate: pick how many syllables a second to hear, and the speed follows the narrator. A slow narrator
/// is sped up more, a fast one less, so every book and chapter is heard at about the same rate.
/// </summary>
public partial class NowPlayingViewModel
{
	/// <summary>How often the speed is checked against the target. The rate is measured over the last minute, so it moves slowly.</summary>
	private static readonly TimeSpan TargetRateInterval = TimeSpan.FromSeconds(5);

	/// <summary>Smaller changes than this are left alone, so the speed does not wobble with every sentence.</summary>
	private const double TargetRateDeadband = 0.15;

	private DateTime nextTargetCheck;

	/// <summary>
	/// The most the speed moves at one check, so a change in the narration eases the speed over half a minute rather
	/// than throwing it about; a chapter's opening once took it from 6.8x to 8.6x and down to 5x within 25 seconds.
	/// </summary>
	private const double TargetRateMaxStep = 0.2;

	/// <summary>After a new chapter begins, the speed is held this long: its title and the pause around it are not narration.</summary>
	private static readonly TimeSpan ChapterHold = TimeSpan.FromSeconds(20);

	private DateTime chapterStartedAt = DateTime.MinValue;

	public const double MIN_TARGET_RATE = 4;
	public const double MAX_TARGET_RATE = 40;
	public static IReadOnlyList<double> TargetRateStops { get; } = [5, 10, 15, 20, 25, 30, 35, 40];

	public bool IsTargetRateOn => settings.UseTargetSyllableRate;
	/// <summary>The Speed sheet's main control while on: in the sheet's big number and on the rail.</summary>
	public bool ShowsTargetRate => ShowsSpeed && IsTargetRateOn;
	public bool ShowsSpeedRail => ShowsSpeed && !IsTargetRateOn;
	public string TargetRateText => $"{settings.TargetSyllableRate:0.0}";

	/// <summary>The target, set from the rail or the ± buttons. The speed follows at once, from the rate last measured.</summary>
	public double TargetRate
	{
		get => settings.TargetSyllableRate;
		set
		{
			if (Math.Abs(value - settings.TargetSyllableRate) < 0.01)
				return;
			settings.TargetSyllableRate = value;
			OnTargetRateChanged();
			FollowTargetRate(now: true);
		}
	}

	/// <summary>Below the target: the speed it takes with this narrator.</summary>
	[ObservableProperty]
	private string targetSpeedText = "";

	[RelayCommand]
	private void ToggleTargetRate()
	{
		settings.UseTargetSyllableRate = !settings.UseTargetSyllableRate;
		// Start from what is heard now, so turning it on does not jump the speed.
		if (settings.UseTargetSyllableRate && HeardRate() is double heard)
			settings.TargetSyllableRate = Math.Clamp(heard, MIN_TARGET_RATE, MAX_TARGET_RATE);
		nextTargetCheck = DateTime.MinValue;
		LogEvent("mode", detail: settings.UseTargetSyllableRate ? $"target rate {settings.TargetSyllableRate:0.0}" : "target rate off");
		OnTargetRateChanged();
	}

	/// <summary>"+" or "-": a tenth of a syllable a second.</summary>
	[RelayCommand]
	private void NudgeTargetRate(string direction) => TargetRate = settings.TargetSyllableRate + (direction == "-" ? -0.1 : 0.1);

	private void OnTargetRateChanged()
	{
		OnPropertyChanged(nameof(IsTargetRateOn));
		OnPropertyChanged(nameof(ShowsTargetRate));
		OnPropertyChanged(nameof(ShowsSpeedRail));
		OnPropertyChanged(nameof(TargetRateText));
		OnPropertyChanged(nameof(TargetRate));
		UpdateSpeedChip();
	}

	/// <summary>The rate heard now, by the measure that steers the speed: long pauses count as a second at most.</summary>
	private double? HeardRate() => player.SourceSpeechRate is double rate && rate > 0 ? rate * Speed : null;

	/// <summary>The listener moved the speed by hand: take the rate now heard as the new target.</summary>
	private void RetargetRate()
	{
		if (!settings.UseTargetSyllableRate || speedSource == "rate" || HeardRate() is not double heard)
			return;
		settings.TargetSyllableRate = heard;
		nextTargetCheck = DateTime.UtcNow + TargetRateInterval;
		OnPropertyChanged(nameof(TargetRateText));
		OnPropertyChanged(nameof(TargetRate));
	}

	/// <summary>Each tick: every few seconds, move the speed toward the one that gives the target rate.</summary>
	private void TargetRateTick()
	{
		if (IsPlaying && DateTime.UtcNow >= nextTargetCheck)
			FollowTargetRate(now: false);
	}

	/// <param name="now">The listener just set the target: move straight to it, however small the change.</param>
	private void FollowTargetRate(bool now)
	{
		if (!settings.UseTargetSyllableRate || IsTraining || IsTrialRunning)
			return;
		nextTargetCheck = DateTime.UtcNow + TargetRateInterval;
		if (!now && DateTime.UtcNow - chapterStartedAt < ChapterHold)
			return;
		if (player.SourceSpeechRate is not double rate || rate < 1)
			return;
		var wanted = Math.Clamp(TargetRateNow / rate, MIN_SPEED, MAX_SPEED);
		// Warming up moves in small steps, so the climb is smooth rather than in jumps.
		if (Math.Abs(wanted - Speed) < (now || IsWarmingUp ? 0.05 : TargetRateDeadband))
			return;
		if (!now)
			wanted = Speed + Math.Clamp(wanted - Speed, -TargetRateMaxStep, TargetRateMaxStep);
		speedSource = "rate";
		ApplySpeed(wanted, save: true);
		speedSource = "you";
	}

	/// <summary>The player's speed chip: × and syllables a second, the one in charge first.</summary>
	[ObservableProperty]
	private string speedChipPrimary = "";

	[ObservableProperty]
	private string speedChipSecondary = "";

	/// <summary>Each tick, as the measured rate moves, and when the speed or mode changes.</summary>
	private void UpdateSpeedChip()
	{
		var heard = HeardRate();
		if (IsBlindMode)
		{
			SpeedChipPrimary = "Speed hidden";
			SpeedChipSecondary = "";
		}
		else if (settings.UseTargetSyllableRate)
		{
			SpeedChipPrimary = $"{settings.TargetSyllableRate:0.0} syl/s";
			SpeedChipSecondary = SpeedText;
		}
		else
		{
			SpeedChipPrimary = SpeedText;
			SpeedChipSecondary = heard is double h ? $"{h:0.0} syl/s" : "";
		}
		TargetSpeedText = heard is double ? SpeedText : $"{SpeedText} · measuring…";
	}
}
