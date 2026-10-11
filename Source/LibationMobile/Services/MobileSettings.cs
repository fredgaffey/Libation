using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace LibationMobile.Services;

/// <summary>Playback speed and each book's resume position, saved as JSON in the app's data folder.</summary>
public class MobileSettings
{
	private const float DEFAULT_SPEED = 1f;

	private readonly string path;
	private readonly Lock locker = new();
	private State state;

	internal class State
	{
		public float Speed { get; set; } = DEFAULT_SPEED;
		public Dictionary<string, TimeSpan> Positions { get; set; } = new();
		/// <summary>When each position was saved, to compare with the one Audible holds from other devices.</summary>
		public Dictionary<string, DateTimeOffset> PositionTimes { get; set; } = new();
		/// <summary>Audible's content reference for each downloaded book, needed to report a position.</summary>
		public Dictionary<string, string> ContentReferences { get; set; } = new();
		public string? LastBookId { get; set; }
		public string? RegionName { get; set; }
		public int SkipSeconds { get; set; } = 30;
		public int ClipSeconds { get; set; } = 30;
		public bool SyncPosition { get; set; } = true;
		public bool HighQualityDownloads { get; set; } = true;
		public float Nonlinearity { get; set; } = 1f;
		public bool UseNonlinearSpeed { get; set; } = true;
		public string? LibrarySort { get; set; }
		public bool ScrubByChapter { get; set; } = true;
		public Dictionary<string, float> BookSpeeds { get; set; } = new();
		public double BookSecondsHeard { get; set; }
		public double SecondsSpentListening { get; set; }
		public DateTimeOffset? ListeningCountedSince { get; set; }
		public float AudibleAppSpeed { get; set; } = 1f;
		public bool AutoPauseAsleep { get; set; }
		public int AutoPauseMinutes { get; set; } = 20;
		public bool AutoPauseNightOnly { get; set; } = true;
		public int AutoPauseFromMinute { get; set; } = 21 * 60;
		public int AutoPauseUntilMinute { get; set; } = 7 * 60;
		public bool AutoPauseHoursChosen { get; set; }
		public bool UseHealthSleep { get; set; }
		public int SleepTimerCustomMinutes { get; set; } = 20;
		public double TargetSyllableRate { get; set; } = 12;
		public bool UseTargetSyllableRate { get; set; }
		public bool UseHeadMovement { get; set; } = true;
		/// <summary>When each downloaded book's download was asked for, so a new download sorts as just listened to.</summary>
		public Dictionary<string, DateTimeOffset> DownloadTimes { get; set; } = new();
		public bool Training { get; set; }
		public float TrainingStartSpeed { get; set; } = 3.5f;
		public bool TrainingFromBelow { get; set; } = true;
		public bool BlindTraining { get; set; }
		public string TrainingPlan { get; set; } = "ramp";
		public float TrainingStartBelow { get; set; } = 1f;
		public float TrainingCeiling { get; set; } = 10f;
		public float TrainingRestartMinutes { get; set; } = 10f;
		public float PauseCap { get; set; }
		public bool KeepSpeed { get; set; } = true;
		public float SpeedFloor { get; set; }
		public float RhythmGap { get; set; }
		public float RhythmRate { get; set; } = 6f;
		public float TrainingStep { get; set; } = 0.1f;
		public float TrainingMinutes { get; set; } = 1.5f;
		public bool TrainingClimb { get; set; }
		public bool ShowSyllableRate { get; set; } = true;
		public bool AskFollowRating { get; set; } = true;
		public bool AskAlertness { get; set; }
		public bool VoicePrompts { get; set; }
		public bool PlayNextInSeries { get; set; } = true;
		public bool DownloadNextInSeries { get; set; }
		public Dictionary<string, SpeedProfile> Profiles { get; set; } = new();
		public string? ActiveProfile { get; set; }
		/// <summary>Which set of defaults the saved values were made with, to move ones never changed to newer defaults.</summary>
		public int DefaultsVersion { get; set; }
	}

	public MobileSettings(string path)
	{
		this.path = path;
		state = Load(path);
		UpgradeDefaults();
		UpgradeProfiles();
		UpgradeSeriesDownload();
		UpgradeAlertness();
		AudioBackend.Nonlinearity = state.Nonlinearity;
		AudioBackend.UseNonlinear = state.UseNonlinearSpeed;
		AudioBackend.PauseCap = state.PauseCap;
		AudioBackend.KeepSpeed = state.KeepSpeed;
		AudioBackend.SpeedFloor = state.SpeedFloor;
		AudioBackend.RhythmGap = state.RhythmGap;
		AudioBackend.RhythmRate = state.RhythmRate;
		AudioBackend.Scaling = state.ActiveProfile is { } active ? Profiles.FirstOrDefault(p => p.Name == active) : null;
	}

	/// <summary>Move settings still at an old default to the current one; anything the listener chose stays.</summary>
	private void UpgradeDefaults()
	{
		if (state.DefaultsVersion >= 2)
			return;
		// 2026-10-08: training starts higher and climbs sooner, for listeners already at 3.5x without a warm-up.
		if (state.TrainingStartSpeed == 3f)
			state.TrainingStartSpeed = 3.5f;
		if (state.TrainingMinutes == 2f)
			state.TrainingMinutes = 1.5f;
		state.DefaultsVersion = 2;
		Save();
	}

	/// <summary>
	/// 2026-10-08, later: profiles retuned. A saved profile still exactly as an earlier default came goes back to the new
	/// one; one the listener changed and saved stays.
	/// </summary>
	private void UpgradeProfiles()
	{
		if (state.DefaultsVersion >= 4)
			return;
		// Profiles became rules that scale with speed; none had been saved yet, so start them afresh.
		state.Profiles.Clear();
		state.DefaultsVersion = 4;
		Save();
	}

	/// <summary>2026-10-09: downloading the next book of a series is off unless asked for; it was on for a few hours.</summary>
	private void UpgradeSeriesDownload()
	{
		if (state.DefaultsVersion >= 5)
			return;
		state.DownloadNextInSeries = false;
		state.DefaultsVersion = 5;
		Save();
	}

	/// <summary>2026-10-11: the question at the start of a sitting is off unless asked for: it was found intrusive.</summary>
	private void UpgradeAlertness()
	{
		if (state.DefaultsVersion >= 6)
			return;
		state.AskAlertness = false;
		state.DefaultsVersion = 6;
		Save();
	}

	/// <summary>Speed up with speechwarp (true) or with the original even method (false).</summary>
	public bool UseNonlinearSpeed
	{
		get { lock (locker) return state.UseNonlinearSpeed; }
		set
		{
			lock (locker) { state.UseNonlinearSpeed = value; Save(); }
			AudioBackend.UseNonlinear = value;
		}
	}

	/// <summary>How unevenly speech is sped up, 0 (evenly) to 1. See <see cref="AudioBackend.Nonlinearity"/>.</summary>
	public float Nonlinearity
	{
		get { lock (locker) return state.Nonlinearity; }
		set
		{
			lock (locker) { state.Nonlinearity = Math.Clamp(value, 0f, 1f); Save(); }
			AudioBackend.Nonlinearity = value;
		}
	}

	public float Speed
	{
		get { lock (locker) return state.Speed; }
		set { lock (locker) { state.Speed = value; Save(); } }
	}

	/// <summary>The speed this book was last played at. Null for a book not played here yet, which starts at <see cref="Speed"/>.</summary>
	public float? GetBookSpeed(string bookId)
	{
		lock (locker)
			return state.BookSpeeds.TryGetValue(bookId, out var speed) ? speed : null;
	}

	/// <summary>Remember this book's speed, and make it the speed new books start at.</summary>
	public void SetBookSpeed(string bookId, float speed)
	{
		lock (locker)
		{
			state.BookSpeeds[bookId] = speed;
			state.Speed = speed;
			Save();
		}
	}

	/// <summary>The book that was last opened, to restore the mini player on launch.</summary>
	public string? LastBookId
	{
		get { lock (locker) return state.LastBookId; }
		set { lock (locker) { state.LastBookId = value; Save(); } }
	}

	/// <summary>The Audible marketplace the account signed in to. Null when signed out.</summary>
	public string? RegionName
	{
		get { lock (locker) return state.RegionName; }
		set { lock (locker) { state.RegionName = value; Save(); } }
	}

	/// <summary>How far the skip buttons move, in seconds.</summary>
	public int SkipSeconds
	{
		get { lock (locker) return state.SkipSeconds; }
		set { lock (locker) { state.SkipSeconds = value; Save(); } }
	}

	/// <summary>Whether the player's progress bar covers the chapter playing now (true) or the whole book.</summary>
	public bool ScrubByChapter
	{
		get { lock (locker) return state.ScrubByChapter; }
		set { lock (locker) { state.ScrubByChapter = value; Save(); } }
	}

	/// <summary>How long a new clip starts out, in seconds.</summary>
	public int ClipSeconds
	{
		get { lock (locker) return state.ClipSeconds; }
		set { lock (locker) { state.ClipSeconds = value; Save(); } }
	}

	/// <summary>Whether the listening position is sent to Audible and taken from it.</summary>
	public bool SyncPosition
	{
		get { lock (locker) return state.SyncPosition; }
		set { lock (locker) { state.SyncPosition = value; Save(); } }
	}

	/// <summary>Download the larger, better-sounding file. Applies to books downloaded from now on.</summary>
	public bool HighQualityDownloads
	{
		get { lock (locker) return state.HighQualityDownloads; }
		set { lock (locker) { state.HighQualityDownloads = value; Save(); } }
	}

	#region Time saved

	/// <summary>How much of books has been heard, in seconds of the book at normal speed.</summary>
	public TimeSpan BookTimeHeard { get { lock (locker) return TimeSpan.FromSeconds(state.BookSecondsHeard); } }

	/// <summary>How long that took to listen to.</summary>
	public TimeSpan TimeSpentListening { get { lock (locker) return TimeSpan.FromSeconds(state.SecondsSpentListening); } }

	/// <summary>When counting began. Null if nothing has been counted.</summary>
	public DateTimeOffset? ListeningCountedSince { get { lock (locker) return state.ListeningCountedSince; } }

	/// <summary>
	/// The speed to assume for Audible's own app when it cannot be worked out from Audible's figures (see
	/// AudibleStats.EstimateSpeed). Audible's statistics count real (wall-clock) time; this turns them into book time.
	/// </summary>
	public float AudibleAppSpeed
	{
		get { lock (locker) return state.AudibleAppSpeed; }
		set { lock (locker) { state.AudibleAppSpeed = Math.Clamp(value, 1f, 10f); Save(); } }
	}

	/// <summary>Pause when the listener seems to have fallen asleep: no touch, and no movement, for a while.</summary>
	public bool AutoPauseAsleep
	{
		get { lock (locker) return state.AutoPauseAsleep; }
		set { lock (locker) { state.AutoPauseAsleep = value; Save(); } }
	}

	/// <summary>How long without a touch or movement counts as asleep.</summary>
	public int AutoPauseMinutes
	{
		get { lock (locker) return state.AutoPauseMinutes; }
		set { lock (locker) { state.AutoPauseMinutes = Math.Clamp(value, 5, 90); Save(); } }
	}

	/// <summary>Watch for sleep only between <see cref="AutoPauseFromMinute"/> and <see cref="AutoPauseUntilMinute"/>.</summary>
	public bool AutoPauseNightOnly
	{
		get { lock (locker) return state.AutoPauseNightOnly; }
		set { lock (locker) { state.AutoPauseNightOnly = value; Save(); } }
	}

	/// <summary>When auto-pause starts watching, in minutes after midnight. 9 p.m. unless changed.</summary>
	public int AutoPauseFromMinute
	{
		get { lock (locker) return state.AutoPauseFromMinute; }
		set { lock (locker) { state.AutoPauseFromMinute = ((value % 1440) + 1440) % 1440; Save(); } }
	}

	/// <summary>When auto-pause stops watching, in minutes after midnight. 7 a.m. unless changed.</summary>
	public int AutoPauseUntilMinute
	{
		get { lock (locker) return state.AutoPauseUntilMinute; }
		set { lock (locker) { state.AutoPauseUntilMinute = ((value % 1440) + 1440) % 1440; Save(); } }
	}

	/// <summary>The listener has set the hours: no longer taken from their sleep in Apple Health.</summary>
	public bool AutoPauseHoursChosen
	{
		get { lock (locker) return state.AutoPauseHoursChosen; }
		set { lock (locker) { state.AutoPauseHoursChosen = value; Save(); } }
	}

	/// <summary>Whether auto-pause watches now: always, or within its hours.</summary>
	public bool AutoPauseWatchesAt(DateTime local)
		=> !AutoPauseNightOnly || SleepFinder.IsWithin(local, AutoPauseFromMinute, AutoPauseUntilMinute);

	/// <summary>Read sleep from Apple Health to find, afterwards, where in a book the listener fell asleep.</summary>
	public bool UseHealthSleep
	{
		get { lock (locker) return state.UseHealthSleep; }
		set { lock (locker) { state.UseHealthSleep = value; Save(); } }
	}

	/// <summary>Use head movement from AirPods: for auto-pause, and a nod or shake to keep listening.</summary>
	public bool UseHeadMovement
	{
		get { lock (locker) return state.UseHeadMovement; }
		set { lock (locker) { state.UseHeadMovement = value; Save(); } }
	}

	/// <summary>Syllables a second to hold the speed at, when <see cref="UseTargetSyllableRate"/> is on.</summary>
	public double TargetSyllableRate
	{
		get { lock (locker) return state.TargetSyllableRate; }
		set { lock (locker) { state.TargetSyllableRate = Math.Round(Math.Clamp(value, 3, 40), 1); Save(); } }
	}

	/// <summary>Set the speed from the narrator's measured syllable rate, so every book is heard at the same rate.</summary>
	public bool UseTargetSyllableRate
	{
		get { lock (locker) return state.UseTargetSyllableRate; }
		set { lock (locker) { state.UseTargetSyllableRate = value; Save(); } }
	}

	/// <summary>The sleep timer's own length, as last set.</summary>
	public int SleepTimerCustomMinutes
	{
		get { lock (locker) return state.SleepTimerCustomMinutes; }
		set { lock (locker) { state.SleepTimerCustomMinutes = Math.Clamp(value, 1, 480); Save(); } }
	}

	public void AddListening(TimeSpan bookTime, TimeSpan timeSpent)
	{
		lock (locker)
		{
			state.ListeningCountedSince ??= DateTimeOffset.UtcNow;
			state.BookSecondsHeard += bookTime.TotalSeconds;
			state.SecondsSpentListening += timeSpent.TotalSeconds;
			Save();
		}
	}

	public void ResetListening()
	{
		lock (locker)
		{
			state.BookSecondsHeard = 0;
			state.SecondsSpentListening = 0;
			state.ListeningCountedSince = null;
			Save();
		}
	}

	#endregion

	#region Speed profiles

	/// <summary>The profiles, in order, with any the listener has changed: Casual, School, Hard and Max.</summary>
	public IReadOnlyList<SpeedProfile> Profiles
	{
		get
		{
			lock (locker)
				return SpeedProfile.Defaults.Select(d => state.Profiles.TryGetValue(d.Name, out var saved) ? saved with { Name = d.Name } : d).ToList();
		}
	}

	/// <summary>The profile chosen last. Null until one is chosen; changing a setting by hand does not clear it.</summary>
	public string? ActiveProfile
	{
		get { lock (locker) return state.ActiveProfile; }
	}

	/// <summary>
	/// Use a profile: its speed as the speed new books start at, its method, and its rules for the very-high-speed options,
	/// which from now on follow whatever speed plays (<see cref="AudioBackend.Scaling"/>).
	/// </summary>
	public void ApplyProfile(SpeedProfile profile)
	{
		lock (locker)
		{
			state.ActiveProfile = profile.Name;
			state.Speed = profile.Speed;
			Save();
		}
		UseNonlinearSpeed = profile.UseNonlinear;
		Nonlinearity = profile.Nonlinearity;
		AudioBackend.Scaling = profile;
	}

	/// <summary>Leave the profile: the very-high-speed options are the sliders' own values again.</summary>
	public void UseCustom()
	{
		lock (locker)
		{
			if (state.ActiveProfile is null)
				return;
			state.ActiveProfile = null;
			Save();
		}
		AudioBackend.Scaling = null;
	}

	/// <summary>
	/// Keep the current settings as the named profile. With a profile in use its rules are kept; with custom settings,
	/// the pause cap is turned into the heard pause it gives at this speed.
	/// </summary>
	public void SaveProfile(string name, float speed)
	{
		lock (locker)
		{
			var rules = AudioBackend.Scaling;
			state.Profiles[name] = rules is not null
				? rules with { Name = name, Speed = speed, UseNonlinear = state.UseNonlinearSpeed, Nonlinearity = state.Nonlinearity }
				: new SpeedProfile(name, speed, state.UseNonlinearSpeed, state.Nonlinearity, state.PauseCap / speed, 1f,
					state.SpeedFloor, 1f, 1f, state.KeepSpeed, state.RhythmGap, state.RhythmRate);
			state.ActiveProfile = name;
			Save();
			AudioBackend.Scaling = state.Profiles[name];
		}
	}

	/// <summary>Put a profile back to how it came.</summary>
	public void ResetProfile(string name)
	{
		lock (locker)
		{
			if (state.Profiles.Remove(name))
				Save();
		}
	}

	#endregion

	#region Speed listening

	/// <summary>
	/// Training mode: each session starts <see cref="TrainingStartBelow"/> under the book's speed and rises by
	/// <see cref="TrainingStep"/> every <see cref="TrainingMinutes"/> of listening until it gets there. Listeners adapt to
	/// fast speech within minutes, and the adaptation fades between sessions; see speechwarp's docs/research-high-speed.md.
	/// </summary>
	public bool Training
	{
		get { lock (locker) return state.Training; }
		set { lock (locker) { state.Training = value; Save(); } }
	}

	/// <summary>Where the warm-up starts: a speed already comfortable without one. A book set at or below it has no warm-up.</summary>
	public float TrainingStartSpeed
	{
		get { lock (locker) return state.TrainingStartSpeed; }
		set { lock (locker) { state.TrainingStartSpeed = Math.Clamp(value, 1f, 10f); Save(); } }
	}

	/// <summary>Start the warm-up <see cref="TrainingStartBelow"/> under the book's speed (true), or at <see cref="TrainingStartSpeed"/>.</summary>
	public bool TrainingFromBelow
	{
		get { lock (locker) return state.TrainingFromBelow; }
		set { lock (locker) { state.TrainingFromBelow = value; Save(); } }
	}

	/// <summary>How far under the book's speed the warm-up starts.</summary>
	public float TrainingStartBelow
	{
		get { lock (locker) return state.TrainingStartBelow; }
		set { lock (locker) { state.TrainingStartBelow = Math.Clamp(value, 0.25f, 5f); Save(); } }
	}

	/// <summary>Training with the speed hidden everywhere, so the algorithm can try speeds and placebos unnoticed.</summary>
	public bool BlindTraining
	{
		get { lock (locker) return state.BlindTraining; }
		set { lock (locker) { state.BlindTraining = value; Save(); } }
	}

	/// <summary>"ramp", "intervals", "pyramid" or "tracking" (Services/SessionPlanner.cs).</summary>
	public string TrainingPlan
	{
		get { lock (locker) return state.TrainingPlan; }
		set { lock (locker) { state.TrainingPlan = value; Save(); } }
	}

	/// <summary>The fastest that keep climbing goes.</summary>
	public float TrainingCeiling
	{
		get { lock (locker) return state.TrainingCeiling; }
		set { lock (locker) { state.TrainingCeiling = Math.Clamp(value, 1f, 10f); Save(); } }
	}

	/// <summary>A break longer than this starts the warm-up again; a shorter one carries on where it was.</summary>
	public float TrainingRestartMinutes
	{
		get { lock (locker) return state.TrainingRestartMinutes; }
		set { lock (locker) { state.TrainingRestartMinutes = Math.Clamp(value, 1f, 240f); Save(); } }
	}

	public float TrainingStep
	{
		get { lock (locker) return state.TrainingStep; }
		set { lock (locker) { state.TrainingStep = Math.Clamp(value, 0.05f, 1f); Save(); } }
	}

	public float TrainingMinutes
	{
		get { lock (locker) return state.TrainingMinutes; }
		set { lock (locker) { state.TrainingMinutes = Math.Clamp(value, 0.5f, 30f); Save(); } }
	}

	/// <summary>Keep rising past the book's speed, and keep the speed reached for next time, as Rightspeed did.</summary>
	public bool TrainingClimb
	{
		get { lock (locker) return state.TrainingClimb; }
		set { lock (locker) { state.TrainingClimb = value; Save(); } }
	}

	/// <summary>See <see cref="AudioBackend.PauseCap"/>.</summary>
	public float PauseCap
	{
		get { lock (locker) return state.PauseCap; }
		set { lock (locker) { state.PauseCap = value; Save(); } AudioBackend.PauseCap = value; }
	}

	/// <summary>See <see cref="AudioBackend.KeepSpeed"/>.</summary>
	public bool KeepSpeed
	{
		get { lock (locker) return state.KeepSpeed; }
		set { lock (locker) { state.KeepSpeed = value; Save(); } AudioBackend.KeepSpeed = value; }
	}

	/// <summary>See <see cref="AudioBackend.SpeedFloor"/>.</summary>
	public float SpeedFloor
	{
		get { lock (locker) return state.SpeedFloor; }
		set { lock (locker) { state.SpeedFloor = value; Save(); } AudioBackend.SpeedFloor = value; }
	}

	/// <summary>See <see cref="AudioBackend.RhythmGap"/>.</summary>
	public float RhythmGap
	{
		get { lock (locker) return state.RhythmGap; }
		set { lock (locker) { state.RhythmGap = value; Save(); } AudioBackend.RhythmGap = value; }
	}

	/// <summary>See <see cref="AudioBackend.RhythmRate"/>.</summary>
	public float RhythmRate
	{
		get { lock (locker) return state.RhythmRate; }
		set { lock (locker) { state.RhythmRate = value; Save(); } AudioBackend.RhythmRate = value; }
	}

	/// <summary>After a session of 5 minutes or more, ask how well it was followed, for the listener's own research.</summary>
	public bool AskFollowRating
	{
		get { lock (locker) return state.AskFollowRating; }
		set { lock (locker) { state.AskFollowRating = value; Save(); } }
	}

	/// <summary>At the start of a sitting (an hour or more since the last), ask how alert the listener is, 0 to 4.</summary>
	public bool AskAlertness
	{
		get { lock (locker) return state.AskAlertness; }
		set { lock (locker) { state.AskAlertness = value; Save(); } }
	}

	/// <summary>Pausing on headphones asks the end-of-session question aloud, and listens for a spoken 0 to 4.</summary>
	public bool VoicePrompts
	{
		get { lock (locker) return state.VoicePrompts; }
		set { lock (locker) { state.VoicePrompts = value; Save(); } }
	}

	/// <summary>When a book ends, carry on with the next in its series, if it is on the phone.</summary>
	public bool PlayNextInSeries
	{
		get { lock (locker) return state.PlayNextInSeries; }
		set { lock (locker) { state.PlayNextInSeries = value; Save(); } }
	}

	/// <summary>When a book in a series is opened, download the next one, so it is ready when this one ends.</summary>
	public bool DownloadNextInSeries
	{
		get { lock (locker) return state.DownloadNextInSeries; }
		set { lock (locker) { state.DownloadNextInSeries = value; Save(); } }
	}

	/// <summary>Show syllables a second under the speed in the player.</summary>
	public bool ShowSyllableRate
	{
		get { lock (locker) return state.ShowSyllableRate; }
		set { lock (locker) { state.ShowSyllableRate = value; Save(); } }
	}

	#endregion

	/// <summary>The order the library is listed in. Null is the newest purchase first.</summary>
	public string? LibrarySort
	{
		get { lock (locker) return state.LibrarySort; }
		set { lock (locker) { state.LibrarySort = value; Save(); } }
	}

	public TimeSpan? GetPosition(string bookId)
	{
		lock (locker)
			return state.Positions.TryGetValue(bookId, out var position) ? position : null;
	}

	/// <param name="savedAt">When the position was reached. Defaults to now; pass Audible's time for one taken from there.</param>
	public void SetPosition(string bookId, TimeSpan position, DateTimeOffset? savedAt = null)
	{
		lock (locker)
		{
			state.Positions[bookId] = position;
			state.PositionTimes[bookId] = savedAt ?? DateTimeOffset.UtcNow;
			Save();
		}
	}

	/// <summary>Take positions from Audible for books where they are newer than what is saved here.</summary>
	/// <returns>The number of books whose position changed.</returns>
	public int MergeRemotePositions(IReadOnlyDictionary<string, RemotePosition> remote, TimeSpan margin, Func<string, bool> skip)
	{
		var changed = 0;
		lock (locker)
		{
			foreach (var (bookId, position) in remote)
			{
				// A position at the very start means "not started there".
				if (skip(bookId) || position.Position < TimeSpan.FromSeconds(10))
					continue;
				var hasLocal = state.Positions.TryGetValue(bookId, out var local) && local > TimeSpan.Zero;
				var hasTime = state.PositionTimes.TryGetValue(bookId, out var localTime);
				// A local position of unknown age is never overridden.
				if (hasLocal && !hasTime)
					continue;
				if (position.Updated <= (hasTime ? localTime : DateTimeOffset.MinValue) + margin || (hasLocal && (position.Position - local).Duration() < TimeSpan.FromSeconds(10)))
					continue;
				state.Positions[bookId] = position.Position;
				state.PositionTimes[bookId] = position.Updated;
				changed++;
			}
			if (changed > 0)
				Save();
		}
		return changed;
	}

	/// <summary>Audible's content reference (ACR) for a book, from its download license. Null if not known yet.</summary>
	public string? GetContentReference(string bookId)
	{
		lock (locker)
			return state.ContentReferences.TryGetValue(bookId, out var acr) ? acr : null;
	}

	public void SetContentReference(string bookId, string acr)
	{
		lock (locker)
		{
			state.ContentReferences[bookId] = acr;
			Save();
		}
	}

	/// <summary>When the position was last saved on this device. Null if never, or saved before this was recorded.</summary>
	public DateTimeOffset? GetPositionTime(string bookId)
	{
		lock (locker)
			return state.PositionTimes.TryGetValue(bookId, out var time) ? time : null;
	}

	/// <summary>When the book's download was asked for. Null if it is not downloaded, or was downloaded before this was recorded.</summary>
	public DateTimeOffset? GetDownloadTime(string bookId)
	{
		lock (locker)
			return state.DownloadTimes.TryGetValue(bookId, out var time) ? time : null;
	}

	/// <summary>Record a download asked for now, or forget it (null) when the download is removed or fails.</summary>
	public void SetDownloadTime(string bookId, DateTimeOffset? time)
	{
		lock (locker)
		{
			if (time is { } at)
				state.DownloadTimes[bookId] = at;
			else if (!state.DownloadTimes.Remove(bookId))
				return;
			Save();
		}
	}

	public void RemovePosition(string bookId)
	{
		lock (locker)
		{
			state.PositionTimes.Remove(bookId);
			if (state.Positions.Remove(bookId))
				Save();
		}
	}

	private static State Load(string path)
	{
		try
		{
			if (File.Exists(path))
				return JsonSerializer.Deserialize(File.ReadAllText(path), MobileSettingsJsonContext.Default.State) ?? new();
		}
		catch (JsonException)
		{
			// A corrupt settings file loses positions, not the app. It is rewritten on the next save.
		}
		return new();
	}

	private void Save()
	{
		var temp = path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(state, MobileSettingsJsonContext.Default.State));
		File.Move(temp, path, overwrite: true);
	}
}

/// <summary>Source-generated serializer, so release builds can trim reflection metadata safely.</summary>
/// <summary>
/// A named set of speed settings to switch between, such as Casual for relaxed listening and Max for record attempts:
/// the speed books start at, the speed-up method, and the options for very high speeds.
/// </summary>
/// <param name="HeardPause">The longest pause you hear, in seconds of listening: pauses in the book are cut to this times the
/// speed, so the pause heard stays the same at any speed. Research at 3x found compressing silence harder than speech
/// helped, and short regular gaps helped listeners keep up; nothing is known above 4x, which the app's own trials test.
/// 0 leaves pauses alone.</param>
/// <param name="PauseFrom">The speed from which pauses are cut.</param>
/// <param name="Floor">The speed floor, as a fraction of the speed, at <paramref name="FloorFull"/> and above. 0 is off.</param>
/// <param name="FloorFrom">Below this speed there is no floor; between it and <paramref name="FloorFull"/> it blends in, so a
/// training climb never jumps.</param>
public record SpeedProfile(string Name, float Speed, bool UseNonlinear, float Nonlinearity, float HeardPause, float PauseFrom,
	float Floor, float FloorFrom, float FloorFull, bool KeepSpeed, float RhythmGap, float RhythmRate)
{
	/// <summary>Starting points, each changeable with "Save current settings". The options are rules, worked out for whatever speed plays.</summary>
	public static readonly IReadOnlyList<SpeedProfile> Defaults =
	[
		new("Casual", 2.5f, true, 1f, 0f, 0f, 0f, 0f, 0f, true, 0f, 6f),
		new("School", 2f, true, 0.7f, 0f, 0f, 0.6f, 1f, 1f, true, 0f, 6f),
		new("Hard", 4.5f, true, 1f, 0.025f, 3f, 0.5f, 3f, 5f, true, 0f, 6f),
		new("Max", 7.5f, true, 1f, 0.015f, 3f, 0.55f, 3f, 6f, true, 0f, 6f),
	];

	/// <summary>Pause caps outside this, in book time, are not useful: shorter clips speech, longer is no cap.</summary>
	private const float SHORTEST_CAP = 0.03f;
	private const float LONGEST_CAP = 0.4f;

	/// <summary>The pause cap at this speed, in seconds of the book: the heard pause times the speed. 0 for none.</summary>
	public float PauseCapAt(double speed)
		=> HeardPause > 0 && speed >= PauseFrom ? (float)Math.Clamp(HeardPause * speed, SHORTEST_CAP, LONGEST_CAP) : 0;

	/// <summary>The speed floor at this speed, as a fraction of it, blended in between FloorFrom and FloorFull. 0 for none.</summary>
	public float FloorAt(double speed)
	{
		if (Floor <= 0 || speed < FloorFrom)
			return 0;
		if (speed >= FloorFull || FloorFull <= FloorFrom)
			return Floor;
		return (float)(Floor * (speed - FloorFrom) / (FloorFull - FloorFrom));
	}

	/// <summary>What each profile is for, in a sentence, for the settings and the player's picker.</summary>
	public string Purpose => Name switch
	{
		"Casual" => "Novels and relaxed listening. Speedy as designed, nothing else.",
		"School" => "Learning and remembering. Slower, gentler, every sound kept clear, pauses left for thinking.",
		"Hard" => "A step past comfortable. Pauses trimmed to a short beat you can still hear, and the hardest sounds kept clear.",
		"Max" => "Record attempts. Shortest audible pauses, hardest sounds kept clear. No rhythm gaps: they felt like too much.",
		_ => "",
	};
}

[JsonSerializable(typeof(MobileSettings.State))]
internal partial class MobileSettingsJsonContext : JsonSerializerContext
{
}
