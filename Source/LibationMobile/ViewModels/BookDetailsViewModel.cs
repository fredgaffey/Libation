using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LibationMobile.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

/// <summary>One book of a series on the details page: in the library, or only in the store.</summary>
public partial class SeriesRowViewModel : ObservableObject
{
	private static readonly HttpClient Http = new();

	public SeriesBook Book { get; }
	/// <summary>The listener's copy. Null for a book they do not have.</summary>
	public BookItemViewModel? Owned { get; }
	public bool IsOwned => Owned is not null;
	public bool IsNotOwned => Owned is null;
	/// <summary>The book whose details are showing.</summary>
	public bool IsCurrent { get; }

	public string Title => Book.Title;
	public string SequenceText => Book.Sequence is null ? "" : $"Book {Book.Sequence}";
	public string StatusText => IsCurrent ? "This book" : Owned is null ? "Not in your library" : Owned.IsDownloaded ? "In your library, downloaded" : "In your library";

	private Bitmap? cover;
	private bool coverRequested;
	public Bitmap? Cover
	{
		get
		{
			if (Owned is not null)
				return Owned.Cover;
			if (!coverRequested)
			{
				coverRequested = true;
				_ = LoadCoverAsync();
			}
			return cover;
		}
	}

	public SeriesRowViewModel(SeriesBook book, BookItemViewModel? owned, bool isCurrent)
	{
		Book = book;
		Owned = owned;
		IsCurrent = isCurrent;
		if (owned is not null)
			owned.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BookItemViewModel.Cover)) OnPropertyChanged(nameof(Cover)); };
	}

	private async Task LoadCoverAsync()
	{
		if (Book.CoverUrl is null)
			return;
		try
		{
			var bytes = await Http.GetByteArrayAsync(Book.CoverUrl);
			cover = await Task.Run(() => NowPlayingViewModel.LoadCover(bytes, 160));
			OnPropertyChanged(nameof(Cover));
		}
		catch (HttpRequestException)
		{
			// Offline: the row shows without a cover.
		}
	}
}

/// <summary>A series on the details page, with its books in order, and a button to download the ones not on the phone.</summary>
public partial class SeriesSectionViewModel : ObservableObject
{
	public string Name { get; }
	public IReadOnlyList<SeriesRowViewModel> Rows { get; }

	public SeriesSectionViewModel(string name, IReadOnlyList<SeriesRowViewModel> rows)
	{
		Name = name;
		Rows = rows;
		foreach (var owned in rows.Select(r => r.Owned).OfType<BookItemViewModel>().Distinct())
			owned.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(BookItemViewModel.State))
				{
					OnPropertyChanged(nameof(DownloadText));
					OnPropertyChanged(nameof(CanDownload));
				}
			};
	}

	public string CountText
	{
		get
		{
			var owned = Rows.Count(r => r.IsOwned);
			return owned == Rows.Count ? $"{Rows.Count} books, all in your library" : $"{Rows.Count} books, {owned} in your library";
		}
	}

	/// <summary>The listener's books in this series that are not on the phone, in reading order.</summary>
	public IReadOnlyList<BookItemViewModel> ToDownload()
		=> Rows.Select(r => r.Owned).OfType<BookItemViewModel>().Where(b => b.IsNotDownloaded).Distinct().ToList();

	public bool CanDownload => ToDownload().Count > 0;

	public string DownloadText => ToDownload().Count switch
	{
		1 => "Download the one not on this phone",
		var n => $"Download the {n} not on this phone",
	};
}

/// <summary>More about one book: what it is about, and the rest of its series.</summary>
public partial class BookDetailsViewModel : ObservableObject
{
	public BookItemViewModel Item { get; }
	public string Title => Item.Title;
	public string? Subtitle => Item.Book.Subtitle;
	public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
	public string Author => Item.Author;
	public string NarratorText => string.IsNullOrWhiteSpace(Item.Book.Narrators) ? "" : $"Read by {Item.Book.Narrators}";
	public bool HasPdf => Item.Book.HasPdf;
	public string LengthText => $"{(int)Item.Book.Length.TotalHours}h {Item.Book.Length.Minutes}m";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSummary))]
	private string? summary;
	public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

	[ObservableProperty]
	private IReadOnlyList<SeriesSectionViewModel> series = [];

	[ObservableProperty]
	private bool isLoading = true;

	[ObservableProperty]
	private string? message;

	public TranscriptJobs Transcripts { get; }

	public BookDetailsViewModel(BookItemViewModel item, AudibleSeries store, LibraryViewModel library, TranscriptJobs transcripts)
	{
		Item = item;
		AudioPath = library.BookPath(item.Book.Asin);
		Transcripts = transcripts;
		// Held weakly, as a details page is made for each book shown while the jobs live as long as the app.
		var page = new WeakReference<BookDetailsViewModel>(this);
		System.ComponentModel.PropertyChangedEventHandler? changed = null;
		changed = (_, _) =>
		{
			if (page.TryGetTarget(out var target))
				target.TranscriptChanged();
			else
				transcripts.PropertyChanged -= changed;
		};
		transcripts.PropertyChanged += changed;
		item.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName != nameof(BookItemViewModel.State))
				return;
			TranscriptChanged();
			OnPropertyChanged(nameof(CanShareAudio));
		};
		_ = LoadAsync(store, library);
	}

	/// <summary>The book's audio file, DRM-free, which can be shared once downloaded.</summary>
	public string AudioPath { get; }
	public bool CanShareAudio => Item.IsDownloaded;

	/// <summary>A downloaded book can be transcribed, on a device that can.</summary>
	public bool CanTranscribe => TranscriptJobs.IsSupported && (Item.IsDownloaded || TranscriptDone);
	public bool TranscriptDone => Transcripts.Transcripts.IsDone(Item.Book.Asin);
	public bool IsTranscribing => Transcripts.IsRunning(Item.Book.Asin);

	public string TranscriptText
	{
		get
		{
			if (IsTranscribing)
				return Transcripts.Status ?? $"Stop transcribing ({Transcripts.Fraction:P0})";
			if (TranscriptDone)
				return "Share transcript";
			if (Transcripts.Transcripts.HasAny(Item.Book.Asin))
				return $"Continue transcript ({Transcripts.Done(Item.Book):P0})";
			return "Make transcript";
		}
	}

	/// <summary>Why the last transcript stopped, if it went wrong; shown on its own book's page only.</summary>
	public string? TranscriptError => Transcripts.Asin is null && Transcripts.Status is { } s && lastTranscribed == Item.Book.Asin ? s : null;
	public bool HasTranscriptError => TranscriptError is not null;
	private string? lastTranscribed;

	/// <summary>Start, stop or carry on transcribing. Sharing a finished one is the view's.</summary>
	public void ToggleTranscript()
	{
		if (IsTranscribing)
			Transcripts.Stop();
		else if (Transcripts.Asin is null && Item.IsDownloaded)
		{
			lastTranscribed = Item.Book.Asin;
			Transcripts.Start(Item.Book);
		}
		TranscriptChanged();
	}

	private void TranscriptChanged()
	{
		OnPropertyChanged(nameof(CanTranscribe));
		OnPropertyChanged(nameof(TranscriptDone));
		OnPropertyChanged(nameof(IsTranscribing));
		OnPropertyChanged(nameof(TranscriptText));
		OnPropertyChanged(nameof(TranscriptError));
		OnPropertyChanged(nameof(HasTranscriptError));
	}

	private async Task LoadAsync(AudibleSeries store, LibraryViewModel library)
	{
		try
		{
			var memberships = Item.Book.Series ?? [];
			try
			{
				var info = await store.GetBookInfoAsync(Item.Book.Asin);
				Summary = info.Summary;
				// A library saved before series were kept has none; the store knows.
				if (memberships.Count == 0)
					memberships = info.Series;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Could not load details of {Item.Book.Asin}: {ex.Message}");
			}

			var sections = new List<SeriesSectionViewModel>();
			foreach (var membership in memberships)
			{
				var books = await store.GetSeriesBooksAsync(membership.Id);
				var rows = books
					.Select(b =>
					{
						// The listener may own a different edition of the same book.
						var owned = library.Find(b.Asin) ?? library.FindByTitle(b.Title, b.Authors);
						return new SeriesRowViewModel(b, owned, owned == Item);
					})
					// The store lists each edition of a book. Show one: the listener's own if they have it.
					.GroupBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
					.Select(editions => editions.FirstOrDefault(r => r.IsCurrent) ?? editions.FirstOrDefault(r => r.IsOwned) ?? editions.First())
					.ToList();
				if (rows.Count > 0)
					sections.Add(new SeriesSectionViewModel(membership.Name, rows));
			}
			Series = sections;
			if (memberships.Count == 0)
				Message = "This book is not part of a series.";
		}
		catch (Exception ex)
		{
			Message = $"The series could not be loaded: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}
}
