using AudioPlayer;
using System;
using System.IO;

namespace LibationMobile.Services;

/// <summary>How each platform decodes and plays audio. Set by the platform head at startup.</summary>
public static class AudioBackend
{
	/// <summary>Opens a downloaded book for decoding. Defaults to SoundFlow's FFmpeg decoder.</summary>
	public static Func<string, IPcmSource> OpenSource { get; set; } = path => new FFmpegPcmSource(path);

	/// <summary>Opens a book read through a stream, as one played without downloading. The source owns the stream.</summary>
	public static Func<Stream, IPcmSource> OpenStream { get; set; } = stream => new FFmpegPcmSource(stream);

	/// <summary>Creates the sound output. Null uses <see cref="AudioFilePlayer"/>'s default, SoundFlow.</summary>
	public static AudioOutputFactory? CreateOutput { get; set; }

	private static volatile float nonlinearity = 1f;
	/// <summary>
	/// How unevenly speech is sped up, 0 to 1: 1 hurries vowels and pauses and spares consonants, 0 speeds
	/// everything up evenly. Takes effect during playback.
	/// </summary>
	public static float Nonlinearity
	{
		get => nonlinearity;
		set => nonlinearity = Math.Clamp(value, 0f, 1f);
	}

	private static volatile bool useNonlinear = true;
	/// <summary>
	/// Which speed-up method plays: speechwarp (true), or the original even one built into the player (false).
	/// Takes effect during playback.
	/// </summary>
	public static bool UseNonlinear
	{
		get => useNonlinear;
		set => useNonlinear = value;
	}

	// Options for very high speeds, from speechwarp 0.2; see its docs/how-it-works.md. Read by the speed changer
	// on the audio thread, so changes take effect during playback.
	private static volatile float pauseCap;
	private static volatile bool keepSpeed = true;
	private static volatile float speedFloor;
	private static volatile float rhythmGap;
	private static volatile float rhythmRate = 6f;

	/// <summary>Shorten every pause to at most this many seconds before speeding up. 0 is off.</summary>
	public static float PauseCap { get => pauseCap; set => pauseCap = Math.Clamp(value, 0f, 1f); }

	/// <summary>With the pause cap or rhythm on, hold the overall speed: time saved in pauses plays the words slower.</summary>
	public static bool KeepSpeed { get => keepSpeed; set => keepSpeed = value; }

	/// <summary>No stretch of speech slower than this fraction of the speed. 0 is off. Speedy only.</summary>
	public static float SpeedFloor { get => speedFloor; set => speedFloor = Math.Clamp(value, 0f, 1f); }

	/// <summary>A silence of this many seconds put into the speech <see cref="RhythmRate"/> times a second. 0 is off.</summary>
	public static float RhythmGap { get => rhythmGap; set => rhythmGap = Math.Clamp(value, 0f, 0.2f); }

	/// <summary>Rhythm gaps a second.</summary>
	public static float RhythmRate { get => rhythmRate; set => rhythmRate = Math.Clamp(value, 1f, 16f); }

	/// <summary>Where the sound is going now, for the research log: "headphones", "bluetooth", "speaker", "car". Set by the platform.</summary>
	public static Func<string?>? Route { get; set; }

	private static volatile SpeedProfile? scaling;

	/// <summary>
	/// The profile in use, whose rules set the very-high-speed options for whatever speed plays, in place of the values
	/// above. Null for custom settings.
	/// </summary>
	public static SpeedProfile? Scaling { get => scaling; set => scaling = value; }

	/// <summary>Whether a high-speed option is on that the original method does not have, so speechwarp must play even then.</summary>
	public static bool NeedsSpeechwarp => Scaling is { } p ? p.HeardPause > 0 || p.RhythmGap > 0 : PauseCap > 0 || RhythmGap > 0;

	/// <summary>False if the speechwarp library could not be loaded on this device, so speed-up is always even.</summary>
	public static bool NonlinearAvailable { get; private set; } = true;

	/// <summary>The speed changer: both methods with the chosen one playing, or only the original one where speechwarp is missing.</summary>
	public static ITimeStretcher CreateStretcher(int sampleRate, int channels)
	{
		if (NonlinearAvailable)
		{
			try
			{
				return new SwitchingTimeStretcher(new SpeechwarpTimeStretcher(sampleRate, channels), new SonicTimeStretcher(sampleRate, channels));
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				Console.WriteLine($"speechwarp is not available, using even speed-up: {ex.Message}");
				NonlinearAvailable = false;
			}
		}
		return new SonicTimeStretcher(sampleRate, channels);
	}
}
