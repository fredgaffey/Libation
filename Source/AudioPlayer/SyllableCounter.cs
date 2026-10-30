using System;
using System.Collections.Generic;

namespace AudioPlayer;

/// <summary>
/// Estimates how many syllables a second are spoken, by finding syllable nuclei: peaks of loudness in voiced
/// sound, each separated from the last by a dip. This follows de Jong and Wempe, "Praat script to detect syllable
/// nuclei and measure speech rate automatically" (Behavior Research Methods, 2009), whose counts correlate with
/// hand counts at about 0.8 to 0.9. Expect an estimate within roughly 10 to 15%, not an exact count.
/// </summary>
/// <remarks>Feed it the audio at its original speed; multiply the result by the playback speed for the rate heard.</remarks>
public sealed class SyllableCounter
{
	private const double FRAME_SECONDS = 0.01;
	/// <summary>Loudness is averaged over this many frames, about the length of a short vowel.</summary>
	private const int SMOOTHING_FRAMES = 4;
	/// <summary>A peak must stand this far above the dip before it, and fall this far after. de Jong and Wempe use 2 dB; 1 dB
	/// with this smoothing counted best on test speech (within about 10%, see SyllableCounterTests).</summary>
	private const double DIP_DB = 1;
	/// <summary>Peaks quieter than this below the loudest recent speech are taken as background, not speech.</summary>
	private const double SILENCE_BELOW_MAX_DB = 25;
	/// <summary>How quickly the loudest-recent level forgets, so a loud passage does not mute a quiet one for long.</summary>
	private const double MAX_DECAY_DB_PER_SECOND = 0.5;
	private const double ABSOLUTE_FLOOR_DB = -60;
	/// <summary>Vowels cross zero a few hundred to about two thousand times a second; hiss such as "s" far more.</summary>
	private const double VOICED_MAX_CROSSINGS_PER_SECOND = 3000;
	/// <summary>Even very fast speech does not put two syllables closer than this.</summary>
	private const double MIN_GAP_SECONDS = 0.06;

	private readonly int channels;
	private readonly int sampleRate;
	private readonly int frameLength;
	private readonly Biquad highPass;
	private readonly Biquad lowPass;
	private readonly Biquad crossingHighPass;

	// The frame being filled.
	private int frameFill;
	private double frameEnergy;
	private int frameCrossings;
	private float lastSample;
	private long frameIndex;

	private readonly double[] recentEnergy = new double[SMOOTHING_FRAMES];
	private readonly double[] recentCrossings = new double[SMOOTHING_FRAMES];
	private double maxDb = double.NegativeInfinity;

	// Peak picking: rising towards a peak, or falling into the dip after one.
	private bool rising = true;
	private double extremeDb = double.NegativeInfinity;
	private long extremeFrame;
	private double extremeCrossings;
	private long lastSyllableFrame = long.MinValue / 2;

	/// <summary>Frame index (at the source's sample rate) of each syllable found, oldest first.</summary>
	private readonly Queue<long> syllables = new();
	/// <summary>Source frame where counting started, after a reset.</summary>
	private long startFrame;
	private long nextFrame;

	public SyllableCounter(int sampleRate, int channels)
	{
		this.sampleRate = sampleRate;
		this.channels = channels;
		frameLength = Math.Max(1, (int)(sampleRate * FRAME_SECONDS));
		// The band where vowels are loud and hiss and rumble are not.
		highPass = Biquad.HighPass(sampleRate, 250);
		lowPass = Biquad.LowPass(sampleRate, Math.Min(3000, sampleRate * 0.45));
		crossingHighPass = Biquad.HighPass(sampleRate, 100);
	}

	/// <summary>Start again from this source frame, as after a seek.</summary>
	public void Reset(long sourceFrame)
	{
		syllables.Clear();
		startFrame = nextFrame = sourceFrame;
		frameFill = 0;
		frameEnergy = 0;
		frameCrossings = 0;
		frameIndex = 0;
		Array.Clear(recentEnergy);
		Array.Clear(recentCrossings);
		maxDb = double.NegativeInfinity;
		rising = true;
		extremeDb = double.NegativeInfinity;
		lastSyllableFrame = long.MinValue / 2;
		highPass.Reset();
		lowPass.Reset();
		crossingHighPass.Reset();
	}

	/// <summary>Analyse interleaved samples that follow on from the last ones written.</summary>
	public void Write(ReadOnlySpan<float> interleaved)
	{
		for (var i = 0; i + channels <= interleaved.Length; i += channels)
		{
			float mono = 0;
			for (var c = 0; c < channels; c++)
				mono += interleaved[i + c];
			mono /= channels;

			var band = lowPass.Process(highPass.Process(mono));
			frameEnergy += band * band;
			var forCrossing = crossingHighPass.Process(mono);
			if ((forCrossing >= 0) != (lastSample >= 0))
				frameCrossings++;
			lastSample = forCrossing;

			if (++frameFill == frameLength)
				EndFrame();
		}
	}

	private void EndFrame()
	{
		var slot = (int)(frameIndex % SMOOTHING_FRAMES);
		recentEnergy[slot] = frameEnergy / frameLength;
		recentCrossings[slot] = frameCrossings / FRAME_SECONDS;
		frameFill = 0;
		frameEnergy = 0;
		frameCrossings = 0;
		frameIndex++;
		nextFrame += frameLength;

		double energy = 0, crossings = 0;
		for (var j = 0; j < SMOOTHING_FRAMES; j++)
		{
			energy += recentEnergy[j];
			crossings += recentCrossings[j];
		}
		var db = 10 * Math.Log10(energy / SMOOTHING_FRAMES + 1e-12);
		crossings /= SMOOTHING_FRAMES;
		// The smoothed value is centred half a window back.
		var centre = nextFrame - frameLength * (SMOOTHING_FRAMES + 1) / 2;

		maxDb = Math.Max(db, maxDb - MAX_DECAY_DB_PER_SECOND * FRAME_SECONDS);

		if (rising)
		{
			if (db > extremeDb)
			{
				extremeDb = db;
				extremeFrame = centre;
				extremeCrossings = crossings;
			}
			else if (db < extremeDb - DIP_DB)
			{
				// The peak is behind us: count it if it was loud, voiced and not too close to the last.
				if (extremeDb > ABSOLUTE_FLOOR_DB && extremeDb > maxDb - SILENCE_BELOW_MAX_DB
					&& extremeCrossings < VOICED_MAX_CROSSINGS_PER_SECOND
					&& extremeFrame - lastSyllableFrame >= MIN_GAP_SECONDS * sampleRate)
				{
					syllables.Enqueue(extremeFrame);
					lastSyllableFrame = extremeFrame;
				}
				rising = false;
				extremeDb = db;
			}
		}
		else if (db < extremeDb)
			extremeDb = db;
		else if (db > extremeDb + DIP_DB)
		{
			rising = true;
			extremeDb = db;
			extremeFrame = centre;
			extremeCrossings = crossings;
		}
	}

	/// <summary>
	/// Syllables a second of source audio over the last <paramref name="window"/> analysed, pauses included. Null until
	/// at least <paramref name="minimum"/> has been analysed since the last reset.
	/// </summary>
	public double? Rate(TimeSpan window, TimeSpan minimum)
	{
		var analysed = (nextFrame - startFrame) / (double)sampleRate;
		if (analysed < minimum.TotalSeconds)
			return null;
		var span = Math.Min(analysed, window.TotalSeconds);
		var from = nextFrame - (long)(span * sampleRate);
		while (syllables.Count > 0 && syllables.Peek() < from)
			syllables.Dequeue();
		return syllables.Count / span;
	}

	/// <summary>
	/// As <see cref="Rate(TimeSpan, TimeSpan)"/>, but a pause longer than <paramref name="longestPause"/> counts only as
	/// that long. The silence between chapters, or a slow reading of a chapter's title with gaps around it, then barely
	/// moves the rate, while the short pauses of ordinary narration count in full, so the figure stays on the same scale.
	/// </summary>
	public double? Rate(TimeSpan window, TimeSpan minimum, TimeSpan longestPause)
	{
		var analysed = (nextFrame - startFrame) / (double)sampleRate;
		if (analysed < minimum.TotalSeconds)
			return null;
		var span = Math.Min(analysed, window.TotalSeconds);
		var from = nextFrame - (long)(span * sampleRate);
		while (syllables.Count > 0 && syllables.Peek() < from)
			syllables.Dequeue();
		var cap = longestPause.TotalSeconds;
		var counted = 0.0;
		var previous = from;
		foreach (var frame in syllables)
		{
			counted += Math.Min(cap, (frame - previous) / (double)sampleRate);
			previous = frame;
		}
		counted += Math.Min(cap, (nextFrame - previous) / (double)sampleRate);
		return counted > 0 ? syllables.Count / counted : 0;
	}

	/// <summary>A second-order filter (Robert Bristow-Johnson's cookbook), Butterworth Q.</summary>
	private sealed class Biquad
	{
		private readonly double b0, b1, b2, a1, a2;
		private double x1, x2, y1, y2;

		private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
		{
			this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0; this.a1 = a1 / a0; this.a2 = a2 / a0;
		}

		public static Biquad LowPass(double rate, double frequency)
		{
			var (cos, alpha) = Coefficients(rate, frequency);
			return new((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
		}

		public static Biquad HighPass(double rate, double frequency)
		{
			var (cos, alpha) = Coefficients(rate, frequency);
			return new((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
		}

		private static (double cos, double alpha) Coefficients(double rate, double frequency)
		{
			var w = 2 * Math.PI * frequency / rate;
			return (Math.Cos(w), Math.Sin(w) / (2 * Math.Sqrt(0.5)));
		}

		public float Process(float x)
		{
			var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
			x2 = x1; x1 = x; y2 = y1; y1 = y;
			return (float)y;
		}

		public void Reset() => x1 = x2 = y1 = y2 = 0;
	}
}
