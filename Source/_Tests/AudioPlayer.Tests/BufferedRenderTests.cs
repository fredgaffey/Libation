using AudioPlayer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.Threading;

namespace BufferedRenderTests;

[TestClass]
public class BufferedRenderTests
{
	private const int RATE = 44100;
	private const int CHANNELS = 2;

	/// <summary>A source whose samples count up, so any gap, repeat or reordering shows.</summary>
	private sealed class Counter
	{
		private long next;
		public volatile int Generation;
		public void Render(Span<float> buffer)
		{
			for (var i = 0; i < buffer.Length; i++)
				buffer[i] = Generation * 1_000_000 + (next++ % 1_000_000);
		}
		public void Restart(int generation)
		{
			next = 0;
			Generation = generation;
		}
	}

	private static void WaitForFill(BufferedRender buffered, int frames)
	{
		var watch = Stopwatch.StartNew();
		while (buffered.BufferedFrames < frames && watch.ElapsedMilliseconds < 2000)
			Thread.Sleep(5);
	}

	[TestMethod]
	public void Rests_while_nothing_is_read_and_refills_only_once_half_is_played()
	{
		var renders = 0;
		using var buffered = new BufferedRender(b => { Interlocked.Increment(ref renders); b.Fill(0.1f); }, RATE, CHANNELS, TimeSpan.FromMilliseconds(400));
		WaitForFill(buffered, RATE * 2 / 5 - 1024);
		Thread.Sleep(100);
		var full = Volatile.Read(ref renders);

		// Paused: the device reads nothing, so nothing more is made.
		Thread.Sleep(300);
		Assert.AreEqual(full, Volatile.Read(ref renders), "rendered while nothing was read");

		// A short read leaves it over half full: still resting.
		buffered.Read(new float[512 * CHANNELS]);
		Thread.Sleep(100);
		Assert.AreEqual(full, Volatile.Read(ref renders), "woke for one short read");

		// Down to under half: back to the top.
		buffered.Read(new float[RATE / 4 * CHANNELS]);
		WaitForFill(buffered, RATE * 2 / 5 - 1024);
		Assert.IsTrue(Volatile.Read(ref renders) > full, "did not refill");
	}

	[TestMethod]
	public void Reads_come_out_in_order_without_gaps()
	{
		var counter = new Counter();
		using var buffered = new BufferedRender(counter.Render, RATE, CHANNELS, TimeSpan.FromMilliseconds(250));
		WaitForFill(buffered, RATE / 5);

		var expected = 0f;
		var output = new float[512 * CHANNELS];
		// About 2 seconds of device callbacks, faster than real time.
		for (var i = 0; i < 170; i++)
		{
			buffered.Read(output);
			foreach (var sample in output)
			{
				if (sample == 0 && expected != 0)
					Assert.Fail($"Silence at callback {i}: the producer fell behind");
				Assert.AreEqual(expected, sample, $"callback {i}");
				expected++;
			}
			Thread.Sleep(1);
		}
		Assert.AreEqual(0, buffered.Underruns);
	}

	[TestMethod]
	public void Keeps_about_the_amount_asked_for_ready()
	{
		var counter = new Counter();
		using var buffered = new BufferedRender(counter.Render, RATE, CHANNELS, TimeSpan.FromMilliseconds(250));
		WaitForFill(buffered, RATE / 4);
		Thread.Sleep(50);
		var ready = buffered.BufferedFrames;
		Assert.IsTrue(ready >= RATE / 4 && ready <= RATE / 4 + 1024, $"{ready} frames ready");
	}

	[TestMethod]
	public void Discard_drops_everything_from_before()
	{
		var counter = new Counter();
		using var buffered = new BufferedRender(counter.Render, RATE, CHANNELS, TimeSpan.FromMilliseconds(250));
		WaitForFill(buffered, RATE / 5);

		// A seek: the source restarts, and what was queued from before must not be heard.
		counter.Restart(1);
		buffered.Discard();
		WaitForFill(buffered, RATE / 5);

		var output = new float[512 * CHANNELS];
		for (var i = 0; i < 20; i++)
		{
			buffered.Read(output);
			foreach (var sample in output)
				Assert.IsTrue(sample >= 1_000_000, $"Heard {sample}, from before the seek");
		}
	}

	[TestMethod]
	public void Plays_silence_rather_than_blocking_when_empty()
	{
		using var buffered = new BufferedRender(buffer => Thread.Sleep(1000), RATE, CHANNELS, TimeSpan.FromMilliseconds(250));
		var output = new float[512 * CHANNELS];
		output.AsSpan().Fill(5);
		var watch = Stopwatch.StartNew();
		buffered.Read(output);
		Assert.IsTrue(watch.ElapsedMilliseconds < 50, "Read blocked");
		CollectionAssert.AreEqual(new float[output.Length], output);
		Assert.AreEqual(1, buffered.Underruns);
	}
}
