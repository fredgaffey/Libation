using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibationMobile.Services;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

/// <summary>
/// Transcribing downloaded books on the device, one at a time. A transcript stopped part way, by the listener or by
/// the app closing, carries on from where it got to.
/// </summary>
public partial class TranscriptJobs(BookTranscripts transcripts, Func<string, string> bookPath) : ObservableObject
{
	public BookTranscripts Transcripts { get; } = transcripts;

	public static bool IsSupported => Transcriber.Platform?.IsAvailable == true;

	/// <summary>The book being transcribed, by ASIN.</summary>
	[ObservableProperty]
	private string? asin;

	/// <summary>How much of it is done, 0 to 1.</summary>
	[ObservableProperty]
	private double fraction;

	/// <summary>What it is doing, or why it stopped.</summary>
	[ObservableProperty]
	private string? status;

	private CancellationTokenSource? cancel;

	public bool IsRunning(string asin) => Asin == asin;

	public void Stop() => cancel?.Cancel();

	/// <summary>How much of a book's transcript is done, 0 to 1, from what is on disk.</summary>
	public double Done(CatalogBook book)
		=> Transcripts.IsDone(book.Asin) ? 1 : book.Length.TotalSeconds > 0 ? Math.Clamp(Transcripts.ResumePoint(book.Asin) / book.Length.TotalSeconds, 0, 1) : 0;

	public void Start(CatalogBook book)
	{
		if (Transcriber.Platform is not { } platform || Asin is not null)
			return;
		Asin = book.Asin;
		Fraction = Done(book);
		Status = null;
		cancel = new CancellationTokenSource();
		_ = RunAsync(platform, book, cancel);
	}

	private async Task RunAsync(ITranscriber platform, CatalogBook book, CancellationTokenSource cancel)
	{
		var token = cancel.Token;
		var language = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : "en-US";
		var length = book.Length.TotalSeconds;
		using var work = Transcriber.Work?.Begin($"Transcribing {book.Title}", cancel.Cancel);
		var success = false;
		try
		{
			await platform.EnsureModelAsync(language, p => Post(() => Status = $"Downloading the speech model, {p:P0}"), token);
			Post(() => Status = null);
			var from = Transcripts.ResumePoint(book.Asin);
			await using (var writer = Transcripts.Append(book.Asin))
			{
				var reported = DateTime.MinValue;
				await platform.TranscribeAsync(bookPath(book.Asin), from, language,
					line => BookTranscripts.Write(writer, line),
					seconds =>
					{
						var done = length > 0 ? Math.Clamp(seconds / length, 0, 1) : 0;
						// Often enough to watch, not so often the screen redraws for nothing.
						if (DateTime.UtcNow - reported < TimeSpan.FromSeconds(0.5))
							return;
						reported = DateTime.UtcNow;
						work?.Report(done, $"{done:P0}");
						Post(() => Fraction = done);
					},
					token);
			}
			Transcripts.MarkDone(book.Asin);
			success = true;
			Post(() => Status = null);
		}
		catch (OperationCanceledException)
		{
			Post(() => Status = null);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Transcribing {book.Asin} stopped: {ex}");
			Post(() => Status = $"The transcript stopped: {ex.Message}");
		}
		finally
		{
			work?.Finish(success);
			cancel.Dispose();
			Post(() =>
			{
				this.cancel = null;
				Fraction = Done(book);
				Asin = null;
			});
		}
	}

	/// <summary>The finished transcript as text, with the book's chapters when it is still on the device.</summary>
	public async Task<string> ExportAsync(CatalogBook book)
	{
		var path = bookPath(book.Asin);
		var chapters = System.IO.File.Exists(path) ? await AudioPlayer.AudioFileChapters.ReadAsync(path) : [];
		return await Task.Run(() => Transcripts.ToText(book.Asin, book.Title, book.Authors, book.Narrators, chapters));
	}

	private static void Post(Action action) => Dispatcher.UIThread.Post(action);
}
