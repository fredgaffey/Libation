using System;

namespace AudioPlayer;

/// <summary>A seekable source of decoded, interleaved 32-bit float PCM audio.</summary>
public interface IPcmSource : IDisposable
{
	int SampleRate { get; }
	int Channels { get; }
	TimeSpan Duration { get; }

	/// <summary>Read interleaved samples.</summary>
	/// <returns>The number of samples (not frames) read. Zero at end of stream.</returns>
	int Read(Span<float> samples);

	/// <summary>Seek to the frame nearest <paramref name="position"/>.</summary>
	/// <returns>The frame index decoding will resume from.</returns>
	long Seek(TimeSpan position);
}

/// <summary>A source that can run short for a while without having ended, such as one still arriving over the network.</summary>
public interface IWaitingSource : IPcmSource
{
	/// <summary>The last read came up short because more is still on its way, not because the end was reached.</summary>
	bool IsWaiting { get; }
}

/// <summary>
/// A source made as it plays, such as text being read aloud: it can run short while the next part is made, and its
/// frames map to the book's timeline through what is being read rather than one to one.
/// </summary>
public interface ILiveSource : IWaitingSource
{
	/// <summary>The place on the book's timeline of a frame, counted from the frame the last seek returned.</summary>
	TimeSpan TimeAt(long frame);
}

/// <summary>An audio file's bytes still arriving, as over the network: whether the part around a time is here yet.</summary>
public interface IArrivingStream
{
	/// <summary>Whether the sound around <paramref name="time"/> can be read without waiting. Asks for it if not.</summary>
	bool IsReady(TimeSpan time);
}
