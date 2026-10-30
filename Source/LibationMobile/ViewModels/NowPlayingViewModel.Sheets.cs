using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using System;
using System.Collections.Generic;

namespace LibationMobile.ViewModels;

/// <summary>
/// The player's chips and the sheets they open: speed, profile, training, sleep, and more. The screen keeps the book,
/// the place in it and the transport; each feature's controls are a tap away, in a sheet from the bottom. A status line
/// under the chips says what is running and which of the phone's data is in use.
/// </summary>
public partial class NowPlayingViewModel
{
	/// <summary>"" for none, else "speed", "listen", "profile", "mode", "sleep" or "more".</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSheetOpen), nameof(IsSpeedSheet), nameof(IsProfileSheet), nameof(IsModeSheet), nameof(IsListenSheet), nameof(IsSleepSheet), nameof(IsMoreSheet))]
	private string sheet = "";

	public bool IsSheetOpen => Sheet.Length > 0;
	public bool IsSpeedSheet => Sheet == "speed";
	public bool IsProfileSheet => Sheet == "profile";
	public bool IsModeSheet => Sheet == "mode";
	/// <summary>The listening mode: warm-up and progress. The training plans' sheet ("mode") is reached from it.</summary>
	public bool IsListenSheet => Sheet == "listen";
	public bool IsSleepSheet => Sheet == "sleep";
	public bool IsMoreSheet => Sheet == "more";

	[RelayCommand]
	private void OpenSheet(string name) => Sheet = Sheet == name ? "" : name;

	[RelayCommand]
	private void CloseSheet() => Sheet = "";

	/// <summary>From the More sheet: things the main view model opens. Set by it.</summary>
	public event Action<string>? MoreRequested;

	/// <summary>"details", "settings" or "sleep-settings": close the sheet and ask the main view model.</summary>
	[RelayCommand]
	private void More(string what)
	{
		Sheet = "";
		MoreRequested?.Invoke(what);
	}

	[RelayCommand]
	private void MoreHistory()
	{
		Sheet = "";
		ShowHistory();
	}

	[RelayCommand]
	private void MoreSaved()
	{
		Sheet = "";
		ShowAnnotationsCommand.Execute(null);
	}

	/// <summary>The speed chip: the speed, or that it is hidden in blind training.</summary>
	public string SpeedChipText => IsBlindMode ? "Speed hidden" : SpeedText;

	/// <summary>The training chip: what mode is on.</summary>
	public string ModeChipText => ListeningMode switch
	{
		"training" => "Training",
		"blind" => "Blind training",
		_ => "Train",
	};

	public bool IsModeOn => !IsNormalMode;

	public bool IsAutoPauseOn => settings.AutoPauseAsleep;

	/// <summary>Turn auto-pause on or off from the sleep sheet.</summary>
	[RelayCommand]
	private void ToggleAutoPause()
	{
		settings.AutoPauseAsleep = !settings.AutoPauseAsleep;
		OnPropertyChanged(nameof(IsAutoPauseOn));
		UpdateStatusLine();
		if (settings.AutoPauseAsleep && ListeningContext.Platform is { } context)
			_ = context.RequestMotionAsync();
	}

	/// <summary>Under the chips: what is running, and the phone's data in use, so nothing works unseen.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStatusLine))]
	private string statusLine = "";

	public bool HasStatusLine => StatusLine.Length > 0;

	private int statusTicks;

	private void UpdateStatusLine()
	{
		var parts = new List<string>();
		if (IsTrainingMode)
			parts.Add("Training");
		else if (IsBlindMode)
			parts.Add("Blind training");
		if (settings.AutoPauseAsleep)
		{
			parts.Add(settings.AutoPauseWatchesAt(DateTime.Now) ? "Auto-pause on" : $"Auto-pause from {SleepFinder.TimeOfDay(settings.AutoPauseFromMinute)}");
			var context = ListeningContext.Platform;
			if (context?.HeadLastMoved is not null)
				parts.Add("AirPods head movement");
			else if (context?.MotionPermission == "allowed")
				parts.Add("phone motion");
		}
		if (settings.UseHealthSleep)
			parts.Add("Apple Health sleep");
		if (UpNext is { } next)
			parts.Add($"Next: {next}");
		StatusLine = string.Join(" · ", parts);
		OnPropertyChanged(nameof(SpeedChipText));
		OnPropertyChanged(nameof(ModeChipText));
		OnPropertyChanged(nameof(IsModeOn));
		OnPropertyChanged(nameof(IsAutoPauseOn));
	}

	/// <summary>From Update: the status line once a second.</summary>
	private void StatusTick()
	{
		if (++statusTicks % 4 == 0)
			UpdateStatusLine();
	}
}
