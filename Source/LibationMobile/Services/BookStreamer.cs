using AAXClean;
using AudibleApi;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>Plays a book from Audible without downloading it: the file is read over the network as it plays.</summary>
public static class BookStreamer
{
	/// <summary>The book, ready to open in the player, reading its sound from Audible as it plays.</summary>
	/// <param name="getApi">Asked again for a new link, so the sign-in is renewed then if it has run out.</param>
	public static async Task<LocalBook> OpenAsync(Func<Task<Api>> getApi, CatalogBook book, LibraryCatalog catalog, MobileSettings settings, CancellationToken token = default)
	{
		var quality = settings.HighQualityDownloads ? DownloadQuality.High : DownloadQuality.Normal;
		var license = await (await getApi()).GetDownloadLicenseAsync(book.Asin, quality);
		// Needed to report the listening position to Audible, as for a download.
		if (license.Acr is string acr)
			settings.SetContentReference(book.Asin, acr);
		var url = license.ContentMetadata?.ContentUrl?.OfflineUrl
			?? throw new InvalidDataException("Audible did not provide a link to this book.");
		if (license.Voucher?.Key is not string key || license.Voucher.Iv is not string iv)
			throw new InvalidDataException("Audible did not provide a decryption key for this book.");

		var http = await HttpRangeStream.OpenAsync(new Uri(url), DeviceRegistrationProfile.Default.DownloadUserAgent, async t =>
		{
			var renewed = await (await getApi()).GetDownloadLicenseAsync(book.Asin, quality);
			return new Uri(renewed.ContentMetadata?.ContentUrl?.OfflineUrl ?? throw new IOException("Audible did not provide a new link to this book."));
		}, token);
		DecryptedAudibleStream audio;
		try
		{
			audio = await Task.Run(() => DecryptedAudibleStream.Open(http, Convert.FromHexString(key), Convert.FromHexString(iv)), token);
		}
		catch
		{
			http.Dispose();
			throw;
		}

		var chapters = BookDownloader.ToChapterInfo(license.ContentMetadata?.ChapterInfo)?.Chapters.ToList();
		if (chapters is null or { Count: 0 })
		{
			var whole = new Mpeg4Lib.ChapterInfo();
			whole.AddChapter(book.Title, audio.Duration);
			chapters = whole.Chapters.ToList();
		}
		var opened = false;
		return new LocalBook(book.Asin, catalog.BookPath(book.Asin), book.Title, book.Authors, book.Narrators, book.Length,
			await catalog.GetCoverAsync(book), chapters,
			OpenSource: () =>
			{
				// The network stream is read once: opening the book again streams it again.
				if (opened)
					throw new InvalidOperationException("This stream has already been opened.");
				opened = true;
				return AudioBackend.OpenStream(audio);
			})
		{ IsStreamed = true };
	}
}

/// <summary>
/// An Audible book's encrypted file seen as the plain MPEG-4 file the player opens: each audio frame decrypted as it is
/// read, and the header changed in place, at the same length, to name the audio as plain AAC. The layout is byte for
/// byte the same as the encrypted file's, so every offset in the file's own tables stays right.
/// </summary>
public sealed class DecryptedAudibleStream : Stream, AudioPlayer.IArrivingStream
{
	private const int AES_BLOCK = 16;
	/// <summary>Fetched ahead of the place playing before it plays, so the decoder never waits on the network.</summary>
	private const int READY_BYTES = 128 * 1024;
	/// <summary>Listening fetched ahead: a tunnel or a patch without signal plays through, at whatever speed.</summary>
	public static readonly TimeSpan ReadAhead = TimeSpan.FromMinutes(15);

	private readonly HttpRangeStream source;
	/// <summary>Bytes that differ from the encrypted file, outside the audio: the file type and the header.</summary>
	private readonly (long At, byte[] Bytes)[] patches;
	// The audio track's chunks in file order, and each frame's size: where every frame is, without a table of them all.
	private readonly long[] chunkOffsets;
	private readonly int[] chunkFirstFrame;
	private readonly int[] frameSizes;
	private readonly Aes aes;
	private readonly byte[] iv;
	private readonly Lock locker = new();
	private long position;

	public TimeSpan Duration { get; }

	/// <summary>A read is waiting for the network.</summary>
	public bool IsWaiting => source.IsWaiting;

	/// <summary>The speed playing: <see cref="ReadAhead"/> is that much more of the book.</summary>
	public float Speed
	{
		set => source.ReadAheadBytes = (long)(ReadAhead.TotalSeconds * Math.Max(1, value) * Length / Math.Max(1, Duration.TotalSeconds));
	}

	private DecryptedAudibleStream(HttpRangeStream source, (long, byte[])[] patches, long[] chunkOffsets, int[] chunkFirstFrame, int[] frameSizes,
		byte[] key, byte[] iv, TimeSpan duration)
	{
		this.source = source;
		this.patches = patches;
		this.chunkOffsets = chunkOffsets;
		this.chunkFirstFrame = chunkFirstFrame;
		this.frameSizes = frameSizes;
		aes = Aes.Create();
		aes.Key = key;
		this.iv = iv;
		Duration = duration;
		Speed = 1;
	}

	/// <summary>Read the encrypted file's header, which only needs its first and header blocks, not the audio.</summary>
	/// <param name="encrypted">The encrypted file, which the result owns.</param>
	public static DecryptedAudibleStream Open(HttpRangeStream encrypted, byte[] key, byte[] iv)
	{
		var aax = new AaxFile(encrypted, encrypted.Length, additionalFixups: false);

		var chunks = new ChunkEntryList(aax.Moov.AudioTrack).OrderBy(c => c.ChunkOffset).ToList();
		var offsets = new long[chunks.Count];
		var firstFrames = new int[chunks.Count + 1];
		var sizes = new List<int>();
		for (var i = 0; i < chunks.Count; i++)
		{
			offsets[i] = chunks[i].ChunkOffset;
			firstFrames[i] = sizes.Count;
			sizes.AddRange(chunks[i].FrameSizes);
		}
		firstFrames[^1] = sizes.Count;

		// AaxFile has renamed the sample entry to mp4a and cleared the AAC config's core coder flag; the boxes that
		// describe the encryption become padding of the same size. Rendered, the header must be exactly as long as before.
		var entry = aax.AudioSampleEntry;
		for (var i = 0; i < entry.Children.Count; i++)
			if (entry.Children[i].Header.Type is "adrm" or "aabd")
				entry.Children[i] = FreeBox.Create(entry.Children[i].Header.TotalBoxSize, null);
		var moovAt = aax.Moov.Header.FilePosition;
		var moovLength = aax.Moov.Header.TotalBoxSize;
		var brandAt = aax.Ftyp.Header.FilePosition + aax.Ftyp.Header.HeaderSize;
		using var moov = new MemoryStream();
		aax.Moov.Save(moov);
		if (moov.Length != moovLength)
			throw new InvalidDataException($"This book's header could not be read for streaming ({moov.Length} bytes rendered, {moovLength} expected).");

		(long, byte[])[] patches = [(brandAt, "M4A "u8.ToArray()), (moovAt, moov.ToArray())];
		return new DecryptedAudibleStream(encrypted, patches, offsets, firstFrames, sizes.ToArray(), key, iv, aax.Duration);
	}

	/// <summary>Read from <paramref name="at"/> without moving the stream's position.</summary>
	public int ReadAt(long at, Span<byte> buffer)
	{
		var count = (int)Math.Min(buffer.Length, Length - at);
		if (count <= 0)
			return 0;
		lock (locker)
		{
			// Whole frames only can be decrypted: widen the read to the frames it touches.
			var start = at;
			var end = at + count;
			var frames = FramesOverlapping(at, at + count);
			if (frames.Count > 0)
			{
				start = Math.Min(start, frames[0].Offset);
				end = Math.Max(end, frames[^1].Offset + frames[^1].Size);
			}
			var raw = ArrayPool<byte>.Shared.Rent((int)(end - start));
			try
			{
				var span = raw.AsSpan(0, (int)(end - start));
				if (source.ReadAt(start, span) != span.Length)
					throw new IOException("The book ended early.");
				foreach (var (offset, size) in frames)
				{
					var frame = span.Slice((int)(offset - start), size & ~(AES_BLOCK - 1));
					if (frame.Length >= AES_BLOCK)
						aes.DecryptCbc(frame, iv, frame, PaddingMode.None);
				}
				foreach (var (patchAt, bytes) in patches)
				{
					var from = Math.Max(start, patchAt);
					var to = Math.Min(end, patchAt + bytes.Length);
					if (from < to)
						bytes.AsSpan((int)(from - patchAt), (int)(to - from)).CopyTo(span[(int)(from - start)..]);
				}
				span.Slice((int)(at - start), count).CopyTo(buffer);
				return count;
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(raw);
			}
		}
	}

	public bool IsReady(TimeSpan time)
	{
		if (frameSizes.Length == 0)
			return true;
		// Every AAC frame lasts as long as the next, so the frame playing is in proportion to the time; start a
		// little before it, as the decoder reads back a frame or two.
		var frame = (int)Math.Clamp(time / Duration * frameSizes.Length - 4, 0, frameSizes.Length - 1);
		var chunk = Array.BinarySearch(chunkFirstFrame, 0, chunkOffsets.Length, frame);
		if (chunk < 0)
			chunk = ~chunk - 1;
		var at = chunkOffsets[chunk];
		for (var f = chunkFirstFrame[chunk]; f < frame; f++)
			at += frameSizes[f];
		return source.IsAvailable(at, READY_BYTES);
	}

	/// <summary>The audio frames that have any byte in [<paramref name="start"/>, <paramref name="end"/>), in order.</summary>
	private List<(long Offset, int Size)> FramesOverlapping(long start, long end)
	{
		List<(long, int)> frames = [];
		// The last chunk starting at or before start, which may run into the range.
		var chunk = Array.BinarySearch(chunkOffsets, start);
		if (chunk < 0)
			chunk = Math.Max(0, ~chunk - 1);
		for (; chunk < chunkOffsets.Length && chunkOffsets[chunk] < end; chunk++)
		{
			var offset = chunkOffsets[chunk];
			for (var f = chunkFirstFrame[chunk]; f < chunkFirstFrame[chunk + 1] && offset < end; f++)
			{
				if (offset + frameSizes[f] > start)
					frames.Add((offset, frameSizes[f]));
				offset += frameSizes[f];
			}
		}
		return frames;
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

	public override long Length => source.Length;
	public override long Position { get => position; set => position = value; }
	public override bool CanRead => true;
	public override bool CanSeek => true;
	public override bool CanWrite => false;
	public override void Flush() { }
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			source.Dispose();
			aes.Dispose();
		}
		base.Dispose(disposing);
	}
}
