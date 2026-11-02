using AAXClean;
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;
using System;
using System.IO;

namespace AudioPlayer;

/// <summary>Decodes any format supported by SoundFlow's FFmpeg codec (m4b, m4a, mp3, flac, ...).</summary>
/// <remarks>
/// SoundFlow's <see cref="ISoundDecoder"/> exposes its length and seek position as int counts of interleaved samples, so
/// positions past <see cref="MaxSeekPosition"/> (about 6.7 hours of 44.1 kHz stereo, 13.5 hours of mono)
/// cannot be sought to, and the length of such files is only known exactly for MPEG-4 containers.
/// Sequential decoding past that point is unaffected.
/// Seeking is not sample accurate: SoundFlow discards the first packet after seeking, so decoding resumes up to
/// one packet (1024 frames for AAC) after the requested position.
/// </remarks>
public sealed class FFmpegPcmSource : IPcmSource
{
	public int SampleRate { get; }
	public int Channels { get; }
	public TimeSpan Duration { get; }
	public TimeSpan MaxSeekPosition => TimeSpan.FromSeconds((double)(int.MaxValue / Channels) / SampleRate);

	private readonly Stream stream;
	private readonly ISoundDecoder decoder;

	public FFmpegPcmSource(string path) : this(File.OpenRead(path), path) { }

	/// <summary>Decode from a seekable stream, such as a book played without downloading. The source owns the stream.</summary>
	public FFmpegPcmSource(Stream stream, string name = "stream")
	{
		this.stream = stream;
		try
		{
			var mpeg4Duration = TryGetMpeg4Duration(stream);
			stream.Position = 0;
			// Only the sample format of the hint is used. Channels and sample rate are the file's own.
			decoder = new FFmpegCodecFactory().TryCreateDecoder(stream, out _, new AudioFormat { Format = SampleFormat.F32 })
				?? throw new InvalidDataException($"Unsupported audio file: {name}");
			SampleRate = decoder.SampleRate;
			Channels = decoder.Channels;
			Duration = mpeg4Duration ?? TimeSpan.FromSeconds((double)Math.Max(0, decoder.Length) / Channels / SampleRate);
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	public int Read(Span<float> samples) => decoder.Decode(samples);

	public long Seek(TimeSpan position)
	{
		var frame = (long)(Clamp(position, TimeSpan.Zero, MaxSeekPosition).TotalSeconds * SampleRate);
		return decoder.Seek((int)(frame * Channels))
			? frame
			: throw new IOException($"Failed to seek to {position}.");
	}

	private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
		=> value < min ? min : value > max ? max : value;

	private static TimeSpan? TryGetMpeg4Duration(Stream stream)
	{
		try
		{
			// Not disposed: that would close the stream, which the decoder reads next.
			var mp4 = new Mp4File(stream, stream.Length);
			return mp4.Duration;
		}
		catch
		{
			// Not an MPEG-4 container.
			return null;
		}
	}

	public void Dispose()
	{
		decoder.Dispose();
		stream.Dispose();
	}
}
