using AAXClean;
using AudibleApi;
using AudibleApi.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>Downloads a book from Audible and decrypts it to a plain m4b the player can open.</summary>
public class BookDownloader
{
	// Share of the progress bar given to the network download; decrypting fills the rest.
	private const double DOWNLOAD_SHARE = 0.9;
	private const int BUFFER_SIZE = 1 << 16;

	private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

	/// <summary>How long the app's own download may wait for a byte before trying the connection again.</summary>
	private static readonly TimeSpan ReadStall = TimeSpan.FromSeconds(30);
	private const int ATTEMPTS = 4;

	/// <summary>
	/// The system's background downloader stalled once since the app started: it tends to stay stuck until the phone
	/// restarts, so later downloads go straight to the app's own.
	/// </summary>
	private static volatile bool systemDownloaderStalled;

	private readonly LibraryCatalog catalog;
	private readonly MobileSettings settings;

	public BookDownloader(LibraryCatalog catalog, MobileSettings settings)
	{
		this.catalog = catalog;
		this.settings = settings;
	}

	/// <summary>Marks a book whose download has started and not finished, so it can be picked up after a restart.</summary>
	private string PendingPath(string asin) => Path.Combine(catalog.BooksDirectory, asin + ".pending");

	/// <summary>Books whose download was interrupted by the app closing.</summary>
	public IEnumerable<string> PendingDownloads()
		=> Directory.EnumerateFiles(catalog.BooksDirectory, "*.pending").Select(Path.GetFileNameWithoutExtension).OfType<string>();

	/// <param name="progress">Reports 0 to 1.</param>
	public async Task DownloadAsync(Api api, CatalogBook book, IProgress<double> progress, CancellationToken token)
	{
		// iOS gives a little time to finish after the app leaves the screen; the file itself downloads in the
		// system's background session, which carries on beyond that.
		using var backgroundWork = FileTransfer.BeginBackgroundWork($"Download {book.Asin}");
		File.WriteAllText(PendingPath(book.Asin), "");
		try
		{
			await DownloadAndDecryptAsync(api, book, progress, token);
			File.Delete(PendingPath(book.Asin));
		}
		catch (OperationCanceledException)
		{
			File.Delete(PendingPath(book.Asin));
			throw;
		}
	}

	private async Task DownloadAndDecryptAsync(Api api, CatalogBook book, IProgress<double> progress, CancellationToken token)
	{
		var license = await api.GetDownloadLicenseAsync(book.Asin, settings.HighQualityDownloads ? DownloadQuality.High : DownloadQuality.Normal);
		// Needed later to report the listening position for this book.
		if (license.Acr is string acr)
			settings.SetContentReference(book.Asin, acr);
		var url = license.ContentMetadata?.ContentUrl?.OfflineUrl
			?? throw new InvalidDataException("Audible did not provide a download link for this book.");
		if (license.Voucher?.Key is not string key || license.Voucher.Iv is not string iv)
			throw new InvalidDataException("Audible did not provide a decryption key for this book.");

		var encrypted = Path.Combine(catalog.BooksDirectory, book.Asin + ".aaxc");
		var decrypted = catalog.BookPath(book.Asin) + ".partial";

		var transferred = false;
		if (FileTransfer.Platform is { } transfer && !systemDownloaderStalled)
		{
			try
			{
				await transfer.DownloadAsync(new Uri(url), encrypted, DeviceRegistrationProfile.Default.DownloadUserAgent, p => progress.Report(p * DOWNLOAD_SHARE), token);
				transferred = true;
			}
			catch (IOException ex)
			{
				// The system's background downloader can be unavailable or stuck; download in the app instead, as before.
				Console.WriteLine($"Background download of {book.Asin} failed, downloading in the app: {ex.Message}");
				if (ex is DownloadStalledException)
					systemDownloaderStalled = true;
			}
		}
		if (!transferred)
			await DownloadWithRetriesAsync(url, encrypted, p => progress.Report(p * DOWNLOAD_SHARE), token);

		try
		{
			await using (var input = File.OpenRead(encrypted))
			await using (var output = File.Create(decrypted))
			{
				var aax = new AaxFile(input);
				aax.SetDecryptionKey(Convert.FromHexString(key), Convert.FromHexString(iv));
				// Audible's own chapter list, as Libation desktop writes it: better titles than the file's generic marks.
				var operation = ToChapterInfo(license.ContentMetadata?.ChapterInfo) is Mpeg4Lib.ChapterInfo chapters
					? aax.ConvertToMp4aAsync(output, chapters)
					: aax.ConvertToMp4aAsync(output);
				operation.ConversionProgressUpdate += (_, e) => progress.Report(DOWNLOAD_SHARE + e.FractionCompleted * (1 - DOWNLOAD_SHARE));
				using var registration = token.Register(() => _ = operation.CancelAsync());
				await operation;
				token.ThrowIfCancellationRequested();
			}
			File.Move(decrypted, catalog.BookPath(book.Asin), overwrite: true);
			File.Delete(encrypted);
			progress.Report(1);
		}
		finally
		{
			File.Delete(decrypted);
		}
	}

	/// <summary>
	/// Flatten Audible's chapter tree into one list, the way Libation desktop does with its default settings
	/// (<c>DownloadOptions.flattenChapters</c>, nested titles joined with ": ").
	/// </summary>
	internal static Mpeg4Lib.ChapterInfo? ToChapterInfo(ChapterInfo? chapterInfo)
	{
		var chapters = Flatten(chapterInfo?.Chapters).OrderBy(c => c.StartOffsetMs).ToList();
		if (chapters.Count == 0)
			return null;

		var info = new Mpeg4Lib.ChapterInfo(TimeSpan.FromMilliseconds(chapters[0].StartOffsetMs));
		foreach (var chapter in chapters)
			info.AddChapter(chapter.Title ?? "", TimeSpan.FromMilliseconds(chapter.LengthMs));
		return info;
	}

	private static List<Chapter> Flatten(IList<Chapter>? chapters)
	{
		List<Chapter> flat = [];
		foreach (var c in chapters ?? [])
		{
			if (c.Chapters is null)
			{
				flat.Add(c);
				continue;
			}

			// A parent under ten seconds is just a heading: fold it into its first child.
			if (c.LengthMs < 10000)
			{
				c.Chapters[0].StartOffsetMs = c.StartOffsetMs;
				c.Chapters[0].StartOffsetSec = c.StartOffsetSec;
				c.Chapters[0].LengthMs += c.LengthMs;
			}
			else
				flat.Add(c);

			var children = Flatten(c.Chapters);
			foreach (var child in children)
				child.Title = $"{c.Title}: {child.Title}";
			flat.AddRange(children);
		}
		return flat;
	}

	/// <summary>A dropped or stalled connection is tried again from where it stopped, a few times, before giving up.</summary>
	private static async Task DownloadWithRetriesAsync(string url, string path, Action<double> progress, CancellationToken token)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				await DownloadFileAsync(url, path, progress, token);
				return;
			}
			catch (Exception ex) when (ex is IOException or HttpRequestException && attempt < ATTEMPTS && !token.IsCancellationRequested)
			{
				Console.WriteLine($"Download attempt {attempt} failed, trying again: {ex.Message}");
				await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token);
			}
		}
	}

	/// <summary>
	/// Download to <paramref name="path"/>, resuming from what is already there. A phone connection can drop
	/// halfway through a book, and restarting a 500 MB download from zero would be wasteful.
	/// </summary>
	internal static async Task DownloadFileAsync(string url, string path, Action<double> progress, CancellationToken token, string? userAgent = null)
	{
		var existing = File.Exists(path) ? new FileInfo(path).Length : 0;

		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? DeviceRegistrationProfile.Default.DownloadUserAgent);
		if (existing > 0)
			request.Headers.Range = new RangeHeaderValue(existing, null);

		// A connection that goes quiet is given up on, rather than left at the same percentage for ever.
		using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
		stall.CancelAfter(ReadStall);
		using var response = await StalledAsIOException(Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token), token);
		if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
		{
			// Already complete.
			progress(1);
			return;
		}
		response.EnsureSuccessStatusCode();

		// The server may ignore the range and send the whole file again.
		var resuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
		if (!resuming)
			existing = 0;
		var total = existing + (response.Content.Headers.ContentLength ?? 0);

		await using var source = await response.Content.ReadAsStreamAsync(token);
		await using var target = new FileStream(path, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE);

		var buffer = new byte[BUFFER_SIZE];
		var written = existing;
		int read;
		while (true)
		{
			stall.CancelAfter(ReadStall);
			if ((read = await StalledAsIOException(source.ReadAsync(buffer, stall.Token).AsTask(), token)) <= 0)
				break;
			await target.WriteAsync(buffer.AsMemory(0, read), token);
			written += read;
			if (total > 0)
				progress((double)written / total);
		}
	}

	private static async Task<T> StalledAsIOException<T>(Task<T> task, CancellationToken token)
	{
		try
		{
			return await task;
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			throw new IOException("The connection stalled.");
		}
	}
}
