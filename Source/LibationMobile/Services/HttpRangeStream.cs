using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>
/// A file on a web server read as a seekable stream: fetched in blocks with range requests as they are needed, and
/// <see cref="ReadAheadBytes"/> ahead of the reader so playback does not wait for each one, or for a patch without signal. A read waits until its bytes arrive; a
/// dropped connection is tried again until it comes back or the stream is closed.
/// </summary>
public sealed class HttpRangeStream : Stream
{
	public const int BLOCK_SIZE = 256 * 1024;
	/// <summary>Blocks kept behind the one being read, as well as those ahead, so skipping back replays without fetching again.</summary>
	private const int KEEP = 48;
	/// <summary>Fetches ahead at once: enough to fill the read-ahead quickly, few enough that a block wanted now is not queued behind them.</summary>
	private const int AHEAD_IN_FLIGHT = 4;

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

	private readonly string userAgent;
	/// <summary>A fresh link to the same file, for when the one in use has expired.</summary>
	private readonly Func<CancellationToken, Task<Uri>> renewUrl;
	private Uri url;
	private readonly Lock locker = new();
	private readonly Dictionary<long, Task<byte[]>> blocks = [];
	private readonly LinkedList<long> recent = new();
	private readonly CancellationTokenSource closed = new();
	private long position;
	private int waiting;
	/// <summary>Fetches running for blocks ahead of the reader, not yet asked for. Under the lock.</summary>
	private int aheadInFlight;

	/// <summary>How far ahead of the reader to fetch. Kept in memory, so 15 minutes at 5x is around 80 MB.</summary>
	public long ReadAheadBytes { get; set; } = 8 * BLOCK_SIZE;

	public override long Length { get; }

	/// <summary>A read is waiting for the network.</summary>
	public bool IsWaiting => Volatile.Read(ref waiting) > 0;

	private HttpRangeStream(Uri url, string userAgent, Func<CancellationToken, Task<Uri>> renewUrl, long length, byte[] first)
	{
		this.url = url;
		this.userAgent = userAgent;
		this.renewUrl = renewUrl;
		Length = length;
		blocks[0] = Task.FromResult(first);
		recent.AddFirst(0);
	}

	/// <summary>Fetch the first block, which also gives the file's length.</summary>
	public static async Task<HttpRangeStream> OpenAsync(Uri url, string userAgent, Func<CancellationToken, Task<Uri>> renewUrl, CancellationToken token)
	{
		using var response = await SendAsync(url, userAgent, 0, BLOCK_SIZE - 1, token);
		if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not long length)
			throw new IOException("The server does not allow playing this book without downloading it.");
		var first = await response.Content.ReadAsByteArrayAsync(token);
		return new HttpRangeStream(url, userAgent, renewUrl, length, first);
	}

	private static async Task<HttpResponseMessage> SendAsync(Uri url, string userAgent, long from, long to, CancellationToken token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
		request.Headers.Range = new RangeHeaderValue(from, to);
		var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
		if (response.IsSuccessStatusCode)
			return response;
		response.Dispose();
		throw new HttpRequestException($"The server answered {(int)response.StatusCode}.", null, response.StatusCode);
	}

	/// <summary>Read from <paramref name="at"/> without moving the stream's position. Safe from any thread.</summary>
	public int ReadAt(long at, Span<byte> buffer)
	{
		if (at >= Length || buffer.Length == 0)
			return 0;
		var count = (int)Math.Min(buffer.Length, Length - at);
		var done = 0;
		while (done < count)
		{
			var index = (at + done) / BLOCK_SIZE;
			var block = Wait(Block(index, readAhead: true));
			var offset = (int)(at + done - index * BLOCK_SIZE);
			var n = Math.Min(count - done, block.Length - offset);
			if (n <= 0)
				throw new IOException("The server sent less than the file's length.");
			block.AsSpan(offset, n).CopyTo(buffer[done..]);
			done += n;
		}
		return done;
	}

	/// <summary>Whether [<paramref name="at"/>, +<paramref name="count"/>) is fetched. Starts fetching it, and ahead of it, if not.</summary>
	public bool IsAvailable(long at, int count)
	{
		var end = Math.Min(Length, at + count);
		var ready = true;
		for (var index = Math.Max(0, at) / BLOCK_SIZE; index * BLOCK_SIZE < end; index++)
			ready &= Block(index, readAhead: index == Math.Max(0, at) / BLOCK_SIZE).IsCompletedSuccessfully;
		return ready;
	}

	private byte[] Wait(Task<byte[]> task)
	{
		if (task.IsCompletedSuccessfully)
			return task.Result;
		Interlocked.Increment(ref waiting);
		try
		{
			task.Wait(closed.Token);
			return task.Result;
		}
		catch (OperationCanceledException)
		{
			throw new ObjectDisposedException(nameof(HttpRangeStream));
		}
		finally
		{
			Interlocked.Decrement(ref waiting);
		}
	}

	private Task<byte[]> Block(long index, bool readAhead)
	{
		lock (locker)
		{
			var task = Fetch(index, ahead: false);
			var aheadBlocks = Math.Max(1, ReadAheadBytes / BLOCK_SIZE);
			if (readAhead)
				for (var next = index + 1; next <= index + aheadBlocks && next * BLOCK_SIZE < Length && aheadInFlight < AHEAD_IN_FLIGHT; next++)
					if (!blocks.ContainsKey(next))
						Fetch(next, ahead: true);
			// Least recently used first out, but never the one asked for or those ahead of it.
			for (var node = recent.Last; recent.Count > KEEP + aheadBlocks && node is not null;)
			{
				var older = node.Previous;
				if (node.Value < index || node.Value > index + aheadBlocks)
				{
					blocks.Remove(node.Value);
					recent.Remove(node);
				}
				node = older;
			}
			return task;
		}
	}

	/// <summary>The block's fetch, started if it is not held. Called holding the lock.</summary>
	/// <param name="ahead">Fetched ahead of the reader, and counted against <see cref="AHEAD_IN_FLIGHT"/>.</param>
	private Task<byte[]> Fetch(long index, bool ahead)
	{
		if (blocks.TryGetValue(index, out var task))
		{
			recent.Remove(index);
			recent.AddFirst(index);
			return task;
		}
		if (ahead)
		{
			aheadInFlight++;
			task = Task.Run(async () =>
			{
				try
				{
					return await FetchAsync(index);
				}
				finally
				{
					lock (locker)
						aheadInFlight--;
				}
			});
		}
		else
			task = Task.Run(() => FetchAsync(index));
		blocks[index] = task;
		recent.AddFirst(index);
		return task;
	}

	private async Task<byte[]> FetchAsync(long index)
	{
		var from = index * BLOCK_SIZE;
		var to = Math.Min(Length, from + BLOCK_SIZE) - 1;
		for (var attempt = 0; ; attempt++)
		{
			closed.Token.ThrowIfCancellationRequested();
			try
			{
				using var response = await SendAsync(url, userAgent, from, to, closed.Token);
				var bytes = await response.Content.ReadAsByteArrayAsync(closed.Token);
				if (response.StatusCode == HttpStatusCode.PartialContent && bytes.Length == to - from + 1)
					return bytes;
				throw new IOException($"Asked for {to - from + 1} bytes, got {bytes.Length}.");
			}
			catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
			{
				// Audible's links expire after a while: ask for a new one to the same file.
				Console.WriteLine($"Stream link refused ({ex.StatusCode}), renewing it");
				if (attempt > 0)
					await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), closed.Token);
				url = await renewUrl(closed.Token);
			}
			catch (Exception ex) when (ex is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError } or IOException or TaskCanceledException
				&& !closed.IsCancellationRequested)
			{
				// No signal, a dropped connection: keep trying, more slowly, until it is back.
				Console.WriteLine($"Stream block {index} failed, trying again: {ex.Message}");
				await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt, 4))), closed.Token);
			}
		}
	}

	public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

	public override int Read(Span<byte> buffer)
	{
		var n = ReadAt(position, buffer);
		position += n;
		return n;
	}

	public override long Seek(long offset, SeekOrigin origin)
		=> position = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => position + offset,
			_ => Length + offset
		};

	public override long Position { get => position; set => position = value; }
	public override bool CanRead => true;
	public override bool CanSeek => true;
	public override bool CanWrite => false;
	public override void Flush() { }
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (disposing && !closed.IsCancellationRequested)
		{
			closed.Cancel();
			lock (locker)
				blocks.Clear();
		}
		base.Dispose(disposing);
	}
}
