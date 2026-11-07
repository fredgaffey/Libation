using System;
using System.Threading;

namespace AudioPlayer;

/// <summary>
/// Plays an <see cref="IPcmSource"/> with pitch-preserving speed control.
/// </summary>
/// <remarks>
/// Decoding and time stretching run on whichever thread the audio output calls its render callback on. <see cref="PlaybackEnded"/> is raised on a
/// thread pool thread; UI subscribers must marshal to their own thread.
/// </remarks>
public sealed class AudioFilePlayer : IDisposable
{
	public const float MIN_SPEED = 0.5f;
	public const float MAX_SPEED = 10f;
	// Samples decoded per pull from the source. Small enough to keep the audio callback short.
	private const int DECODE_CHUNK_FRAMES = 2048;

	public event EventHandler? PlaybackEnded;

	public TimeSpan Duration => source.Duration;

	/// <summary>Playing, but the sound has not arrived yet, as for a book streamed over a slow connection.</summary>
	public bool IsWaiting => source is IWaitingSource { IsWaiting: true };
	public bool IsPlaying => output.IsRunning;

	/// <summary>Playback speed, clamped to [<see cref="MIN_SPEED"/>, <see cref="MAX_SPEED"/>].</summary>
	public float Speed
	{
		get => stretcher.Speed;
		set
		{
			lock (locker)
				stretcher.Speed = Math.Clamp(value, MIN_SPEED, MAX_SPEED);
			if (source is IReadsAhead ahead)
				ahead.Speed = stretcher.Speed;
		}
	}

	/// <summary>Output gain, where 1 is unchanged.</summary>
	public float Volume
	{
		get => volume;
		set => volume = Math.Max(0f, value);
	}

	/// <summary>Position in the source that is currently being heard.</summary>
	public TimeSpan Position
	{
		get
		{
			lock (locker)
			{
				// Frames handed to the speed changer but not yet heard, and its output queued in the device.
				var unheard = stretcher.BufferedSourceFrames + (long)(output.BufferedFrames * stretcher.Speed);
				var frame = Math.Max(0, sourceFrame - unheard);
				return source is ILiveSource live ? live.TimeAt(frame) : TimeSpan.FromSeconds(frame / (double)source.SampleRate);
			}
		}
	}

	/// <summary>
	/// Syllables a second in the book itself, before any speed-up, over the last minute decoded. Multiply by the speed for
	/// the rate heard. Null until ten seconds have been decoded since the last seek.
	/// </summary>
	public double? SourceSyllablesPerSecond
	{
		get
		{
			lock (locker)
				return syllables.Rate(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
		}
	}

	/// <summary>
	/// As <see cref="SourceSyllablesPerSecond"/>, but with long pauses counted as a second at most, so a chapter break's
	/// silence does not pull it down. Null until thirty seconds have been decoded since the last seek: steering the speed
	/// by less than that chased every heading and pause.
	/// </summary>
	public double? SourceSpeechRate
	{
		get
		{
			lock (locker)
				return syllables.Rate(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
		}
	}

	private readonly Lock locker = new();
	private readonly SyllableCounter syllables;
	private readonly IPcmSource source;
	private readonly ITimeStretcher stretcher;
	private volatile float volume = 1f;
	private readonly IAudioOutput output;
	private readonly float[] decodeBuffer;
	/// <summary>Index of the next source frame to be handed to <see cref="stretcher"/>.</summary>
	private long sourceFrame;
	private bool sourceEnded;
	private bool stretcherFlushed;
	private bool endRaised;

	/// <param name="source">The audio to play. Ownership passes to the player, which disposes it.</param>
	/// <param name="outputFactory">Where to play it. Defaults to <see cref="SoundFlowAudioOutput"/>.</param>
	/// <param name="stretcherFactory">How to change speed. Defaults to <see cref="SonicTimeStretcher"/>.</param>
	public AudioFilePlayer(IPcmSource source, AudioOutputFactory? outputFactory = null, TimeStretcherFactory? stretcherFactory = null)
	{
		ArgumentNullException.ThrowIfNull(source, nameof(source));

		this.source = source;
		stretcher = stretcherFactory?.Invoke(source.SampleRate, source.Channels) ?? new SonicTimeStretcher(source.SampleRate, source.Channels);
		decodeBuffer = new float[DECODE_CHUNK_FRAMES * source.Channels];
		syllables = new SyllableCounter(source.SampleRate, source.Channels);
		syllables.Reset(0);
		output = (outputFactory ?? ((rate, channels, render) => new SoundFlowAudioOutput(rate, channels, render)))
			(source.SampleRate, source.Channels, FillBuffer);
	}

	public void Play()
	{
		lock (locker)
		{
			if (endRaised)
				SeekInternal(TimeSpan.Zero);
		}
		output.Start();
	}

	public void Pause() => output.Stop();

	public void Seek(TimeSpan position)
	{
		lock (locker)
			SeekInternal(position);
	}

	private void SeekInternal(TimeSpan position)
	{
		sourceFrame = source.Seek(position);
		syllables.Reset(sourceFrame);
		stretcher.Clear();
		output.Discard();
		sourceEnded = false;
		stretcherFlushed = false;
		endRaised = false;
	}

	/// <summary>Fill <paramref name="buffer"/> with stretched audio. Called on the audio output's thread.</summary>
	private void FillBuffer(Span<float> buffer)
	{
		lock (locker)
		{
			var written = 0;
			while (written < buffer.Length)
			{
				var read = stretcher.Read(buffer[written..]);
				written += read;
				if (read > 0)
					continue;

				if (!sourceEnded)
				{
					var decoded = source.Read(decodeBuffer);
					if (decoded > 0)
					{
						stretcher.Write(decodeBuffer.AsSpan(0, decoded));
						syllables.Write(decodeBuffer.AsSpan(0, decoded));
						sourceFrame += decoded / source.Channels;
					}
					else if (source is IWaitingSource { IsWaiting: true })
						break; // Still being made or fetched: silence until it is, without ending.
					else
						sourceEnded = true;
				}
				else if (!stretcherFlushed)
				{
					stretcher.Flush();
					stretcherFlushed = true;
				}
				else
				{
					if (!endRaised)
					{
						endRaised = true;
						ThreadPool.QueueUserWorkItem(_ => PlaybackEnded?.Invoke(this, EventArgs.Empty));
					}
					// The rest of the buffer stays silent.
					break;
				}
			}

			var gain = volume;
			if (gain != 1f)
			{
				foreach (ref var sample in buffer[..written])
					sample *= gain;
			}
		}
	}

	public void Dispose()
	{
		output.Dispose();
		source.Dispose();
		stretcher.Dispose();
	}
}
