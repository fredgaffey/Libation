using System;
using System.Threading;

namespace AudioPlayer;

/// <summary>
/// Runs a render callback ahead of the sound device on a thread of its own, keeping a short queue of finished
/// samples that the device's real-time callback only copies from. Decoding and time stretching then have that
/// much slack: a burst of other work, or a garbage collection, no longer breaks the sound up.
/// </summary>
public sealed class BufferedRender : IDisposable
{
	private readonly AudioRenderCallback render;
	private readonly int channels;
	private readonly float[] ring;
	private readonly float[] chunk;
	private readonly int targetSamples;
	/// <summary>
	/// Refilled only once the queue is down to this, and then to the top in one go: a few wakes a second rather than
	/// one for every short buffer the device takes, so the processor can sleep in between.
	/// </summary>
	private readonly int refillSamples;
	/// <summary>Producer only: filling the queue to the top, after it ran down to <see cref="refillSamples"/>.</summary>
	private bool refilling;
	// Running totals in samples: the producer only moves written, the device only moves read.
	private long written;
	private long read;
	/// <summary>Everything before this was discarded: the device skips to here.</summary>
	private long discardedTo;
	private int generation;
	private readonly Lock commitLocker = new();
	private readonly AutoResetEvent wake = new(false);
	private readonly Thread thread;
	private volatile bool disposed;

	/// <param name="render">Where the samples come from. Called on this class's own thread.</param>
	/// <param name="ahead">How much finished audio to keep ready.</param>
	public BufferedRender(AudioRenderCallback render, int sampleRate, int channels, TimeSpan ahead, int chunkFrames = 1024)
	{
		this.render = render;
		this.channels = channels;
		chunk = new float[chunkFrames * channels];
		targetSamples = (int)(ahead.TotalSeconds * sampleRate) * channels;
		refillSamples = targetSamples / 2;
		// Room for a full queue after a discard, while the device has yet to skip past the old one.
		ring = new float[2 * (targetSamples + chunk.Length)];
		thread = new Thread(Produce) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Audio render" };
		thread.Start();
	}

	/// <summary>Frames queued and not yet heard.</summary>
	public int BufferedFrames => (int)Queued() / channels;

	private long Queued() => Volatile.Read(ref written) - Math.Max(Volatile.Read(ref read), Volatile.Read(ref discardedTo));

	/// <summary>Times the device asked for more than was ready, and played silence instead.</summary>
	public int Underruns { get; private set; }

	/// <summary>
	/// Drop what is queued, such as after a seek. A chunk being rendered while this is called is dropped too,
	/// since it may come from before the change.
	/// </summary>
	public void Discard()
	{
		lock (commitLocker)
		{
			generation++;
			Volatile.Write(ref discardedTo, written);
		}
		wake.Set();
	}

	/// <summary>Fill <paramref name="output"/> from the queue. For the device's real-time callback: never blocks.</summary>
	public void Read(Span<float> output)
	{
		var readAt = Math.Max(Volatile.Read(ref read), Volatile.Read(ref discardedTo));
		var writtenAt = Volatile.Read(ref written);

		var count = (int)Math.Min(output.Length, writtenAt - readAt);
		var start = (int)(readAt % ring.Length);
		var first = Math.Min(count, ring.Length - start);
		ring.AsSpan(start, first).CopyTo(output);
		ring.AsSpan(0, count - first).CopyTo(output[first..]);
		output[count..].Clear();
		if (count < output.Length)
			Underruns++;

		Volatile.Write(ref read, readAt + count);
		if (Queued() <= refillSamples)
			wake.Set();
	}

	private void Produce()
	{
		while (!disposed)
		{
			// Also never past what the device has yet to skip over, or the ring would overwrite what it is reading.
			// Full, or still more than half full: sleep until the device, a discard or disposing wakes it. Paused, the
			// device reads nothing, so this sleeps until playing again.
			var queued = Queued();
			if (queued >= targetSamples || Volatile.Read(ref written) - Volatile.Read(ref read) > ring.Length - chunk.Length)
				refilling = false;
			else if (queued <= refillSamples)
				refilling = true;
			if (!refilling)
			{
				wake.WaitOne();
				continue;
			}

			var renderedFor = Volatile.Read(ref generation);
			chunk.AsSpan().Clear();
			try
			{
				render(chunk);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Audio render failed: {ex}");
				wake.WaitOne(100);
				continue;
			}

			lock (commitLocker)
			{
				if (renderedFor != generation)
					continue;
				var writtenAt = written;
				var start = (int)(writtenAt % ring.Length);
				var first = Math.Min(chunk.Length, ring.Length - start);
				chunk.AsSpan(0, first).CopyTo(ring.AsSpan(start));
				chunk.AsSpan(first).CopyTo(ring);
				Volatile.Write(ref written, writtenAt + chunk.Length);
			}
		}
	}

	public void Dispose()
	{
		disposed = true;
		wake.Set();
		thread.Join(500);
		wake.Dispose();
	}
}
