using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

public enum DownloadState
{
	NotDownloaded,
	Downloading,
	Downloaded
}

/// <summary>One title in the library list.</summary>
public partial class BookItemViewModel : ObservableObject
{
	public CatalogBook Book { get; private set; }
	public string Title => Book.Title;
	public string Author => Book.Authors;
	public string SearchableText { get; private set; }

	/// <summary>Take what a library refresh says about this book, keeping the row and any download in progress.</summary>
	public void Update(CatalogBook book)
	{
		if (Book == book)
			return;
		Book = book;
		SearchableText = $"{book.Title} {book.Subtitle} {book.Authors} {book.Narrators}";
		OnPropertyChanged(string.Empty);
	}

	private readonly Func<CatalogBook, Task<byte[]?>> loadCoverBytes;
	private Bitmap? cover;
	private bool coverRequested;

	/// <summary>
	/// Loaded the first time a row on screen asks for it, and decoded off the UI thread. A library can hold
	/// hundreds of books; decoding every cover up front froze the app.
	/// </summary>
	public Bitmap? Cover
	{
		get
		{
			if (!coverRequested)
			{
				coverRequested = true;
				_ = LoadCoverAsync();
			}
			return cover;
		}
	}

	private async Task LoadCoverAsync()
	{
		var bitmap = await Task.Run(async () => await loadCoverBytes(Book) is byte[] bytes ? NowPlayingViewModel.LoadCover(bytes, 160) : null);
		if (bitmap is null)
			return;
		cover = bitmap;
		OnPropertyChanged(nameof(Cover));
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotDownloaded), nameof(IsDownloading), nameof(IsDownloaded))]
	private DownloadState state;

	/// <summary>Download progress, 0 to 1.</summary>
	[ObservableProperty]
	private double downloadProgress;

	/// <summary>Listening progress, 0 to 1.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProgress), nameof(IsFinished), nameof(IsNotFinished))]
	private double progress;

	[ObservableProperty]
	private string statusText = "";

	public bool IsNotDownloaded => State == DownloadState.NotDownloaded;
	public bool IsDownloading => State == DownloadState.Downloading;
	public bool IsDownloaded => State == DownloadState.Downloaded;
	public bool HasPdf => Book.HasPdf;
	public bool HasProgress => Progress > 0;
	public bool IsFinished => Progress >= 1;
	public bool IsNotFinished => !IsFinished;
	public bool IsInSeries => Book.Series is { Count: > 0 };

	internal CancellationTokenSource? DownloadCancellation { get; set; }

	public BookItemViewModel(CatalogBook book, DownloadState state, Func<CatalogBook, Task<byte[]?>> loadCoverBytes)
	{
		Book = book;
		SearchableText = $"{book.Title} {book.Subtitle} {book.Authors} {book.Narrators}";
		this.state = state;
		this.loadCoverBytes = loadCoverBytes;
	}

	/// <summary>How far through the book, and how long is left at the given speed.</summary>
	public void Refresh(TimeSpan? position, double speed)
	{
		var length = Book.Length;
		var at = position ?? TimeSpan.Zero;
		Progress = length > TimeSpan.Zero ? Math.Clamp(at / length, 0, 1) : 0;
		StatusText = State switch
		{
			DownloadState.Downloading => $"Downloading, {DownloadProgress:P0}",
			_ when length > TimeSpan.Zero && at >= length => "Finished",
			_ when at > TimeSpan.Zero => $"{FormatDuration((length - at) / speed)} left at {speed:0.0}×",
			_ => FormatDuration(length)
		};
	}

	private static string FormatDuration(TimeSpan time)
		=> time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : time.TotalMinutes >= 1 ? $"{time.Minutes}m" : $"{Math.Max(0, time.Seconds)}s";
}

/// <summary>The user's Audible library, with each title's download state.</summary>
public partial class LibraryViewModel : ObservableObject
{
	private readonly LibraryCatalog catalog;
	private readonly AudibleAccount account;
	private readonly BookDownloader downloader;
	private readonly MobileSettings settings;
	private readonly AudibleAnnotations annotations;
	private readonly ListeningLog listeningLog;
	private readonly BookPdfs? pdfs;
	private List<BookItemViewModel> allBooks = [];
	private DateTime lastPositionRefresh = DateTime.MinValue;

	/// <summary>Returning to the app refreshes positions at most this often. The refresh button always does.</summary>
	private static readonly TimeSpan PositionRefreshInterval = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan SyncTimeMargin = TimeSpan.FromSeconds(5);

	/// <summary>The book loaded in the player, whose position the player itself keeps in step.</summary>
	public string? PlayingBookId { get; set; }

	/// <summary>The books shown, after the All / Downloaded filter. Replaced as a whole when the filter changes.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDownloadedEmpty), nameof(IsSearchEmpty), nameof(ResultCountText))]
	private IReadOnlyList<BookItemViewModel> books = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowAll))]
	private bool showDownloadedOnly;

	/// <summary>Words to find in titles, authors and narrators. Empty shows everything.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearching), nameof(IsSearchEmpty), nameof(IsDownloadedEmpty))]
	private string? searchText;

	public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
	public bool IsSearchEmpty => (IsSearching || HasActiveFilters) && Books.Count == 0;

	partial void OnSearchTextChanged(string? value) => ApplyFilter();

	[RelayCommand]
	private void ClearSearch() => SearchText = null;

	#region Filters

	/// <summary>Whether the filter choices are showing under the search box.</summary>
	[ObservableProperty]
	private bool isFilterPanelOpen;

	[RelayCommand]
	private void ToggleFilterPanel() => IsFilterPanelOpen = !IsFilterPanelOpen;

	// Each filter is one of a few named choices; "any" leaves the list alone.
	private string progressFilter = "any";
	private string lengthFilter = "any";
	private string seriesFilter = "any";
	private bool pdfFilter;
	public bool IsPdfOnly => pdfFilter;

	[RelayCommand]
	private void TogglePdfFilter()
	{
		pdfFilter = !pdfFilter;
		OnPropertyChanged(string.Empty);
		ApplyFilter();
	}

	public bool IsProgressAny => progressFilter == "any";
	public bool IsProgressNotStarted => progressFilter == "notstarted";
	public bool IsProgressStarted => progressFilter == "started";
	public bool IsProgressFinished => progressFilter == "finished";
	public bool IsLengthAny => lengthFilter == "any";
	public bool IsLengthShort => lengthFilter == "short";
	public bool IsLengthMedium => lengthFilter == "medium";
	public bool IsLengthLong => lengthFilter == "long";
	public bool IsSeriesAny => seriesFilter == "any";
	public bool IsSeriesOnly => seriesFilter == "series";
	public bool IsStandaloneOnly => seriesFilter == "standalone";

	private int ActiveFilterCount => (IsProgressAny ? 0 : 1) + (IsLengthAny ? 0 : 1) + (IsSeriesAny ? 0 : 1) + (pdfFilter ? 1 : 0);
	public bool HasActiveFilters => ActiveFilterCount > 0;
	public string FilterButtonText => ActiveFilterCount == 0 ? "Filters" : $"Filters ({ActiveFilterCount})";
	public string ResultCountText => Books.Count == 1 ? "1 book" : $"{Books.Count} books";

	[RelayCommand]
	private void SetProgressFilter(string choice) => SetFilter(ref progressFilter, choice);

	[RelayCommand]
	private void SetLengthFilter(string choice) => SetFilter(ref lengthFilter, choice);

	[RelayCommand]
	private void SetSeriesFilter(string choice) => SetFilter(ref seriesFilter, choice);

	[RelayCommand]
	private void ClearFilters()
	{
		progressFilter = lengthFilter = seriesFilter = "any";
		pdfFilter = false;
		SearchText = null;
		OnPropertyChanged(string.Empty);
		ApplyFilter();
	}

	private void SetFilter(ref string filter, string choice)
	{
		filter = choice;
		// Every "is this chosen" property may have changed.
		OnPropertyChanged(string.Empty);
		ApplyFilter();
	}

	private bool PassesFilters(BookItemViewModel book)
	{
		var hours = book.Book.Length.TotalHours;
		return (!pdfFilter || book.Book.HasPdf) && progressFilter switch
			{
				"notstarted" => book.Progress <= 0,
				"started" => book.Progress is > 0 and < 1,
				"finished" => book.Progress >= 1,
				_ => true
			}
			&& lengthFilter switch
			{
				"short" => hours < 5,
				"medium" => hours is >= 5 and < 15,
				"long" => hours >= 15,
				_ => true
			}
			&& seriesFilter switch
			{
				"series" => book.Book.Series is { Count: > 0 },
				"standalone" => book.Book.Series is not { Count: > 0 },
				_ => true
			};
	}

	#endregion

	[ObservableProperty]
	private bool isSyncing;

	[ObservableProperty]
	private string? message;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsLibraryEmpty), nameof(IsDownloadedEmpty))]
	private bool isLoaded;

	public bool ShowAll => !ShowDownloadedOnly;
	public bool IsLibraryEmpty => IsLoaded && !IsSyncing && allBooks.Count == 0;
	public bool IsDownloadedEmpty => IsLoaded && ShowDownloadedOnly && !IsSearching && !HasActiveFilters && Books.Count == 0 && allBooks.Count > 0;

	public LibraryViewModel(LibraryCatalog catalog, AudibleAccount account, BookDownloader downloader, MobileSettings settings, AudibleAnnotations annotations, ListeningLog listeningLog, BookPdfs? pdfs = null)
	{
		this.pdfs = pdfs;
		this.listeningLog = listeningLog;
		this.annotations = annotations;
		this.catalog = catalog;
		this.account = account;
		this.downloader = downloader;
		this.settings = settings;
	}

	partial void OnShowDownloadedOnlyChanged(bool value) => ApplyFilter();
	partial void OnIsSyncingChanged(bool value) => OnPropertyChanged(nameof(IsLibraryEmpty));

	[RelayCommand]
	private void ShowAllBooks() => ShowDownloadedOnly = false;

	[RelayCommand]
	private void ShowDownloadedBooks() => ShowDownloadedOnly = true;

	/// <summary>Show the cached library straight away; sync with Audible if there is none yet.</summary>
	public async Task LoadAsync()
	{
		SetBooks(await catalog.LoadAsync());
		IsLoaded = true;
		ResumeDownloads();
		_ = FetchMissingPdfsAsync();
		if (allBooks.Count == 0)
			await SyncAsync();
		else
			_ = RefreshPositionsAsync(force: true);
	}

	/// <summary>
	/// Take each book's position from Audible where it is newer than this device's, so rows show what was
	/// listened to elsewhere and a book opens in the right place.
	/// </summary>
	public async Task RefreshPositionsAsync(bool force)
	{
		if (allBooks.Count == 0 || !settings.SyncPosition || (!force && DateTime.UtcNow - lastPositionRefresh < PositionRefreshInterval))
			return;
		lastPositionRefresh = DateTime.UtcNow;
		try
		{
			var remote = await annotations.GetPositionsAsync(allBooks.Select(b => b.Book.Asin));
			if (settings.MergeRemotePositions(remote, SyncTimeMargin, skip: asin => asin == PlayingBookId) > 0)
				RefreshProgress();
		}
		catch (Exception)
		{
			// Offline: rows keep showing the positions saved on this device.
		}
	}

	[RelayCommand]
	public async Task SyncAsync()
	{
		if (IsSyncing)
			return;
		IsSyncing = true;
		Message = null;
		try
		{
			var api = await account.GetApiAsync();
			SetBooks(await catalog.SyncAsync(api));
			await RefreshPositionsAsync(force: true);
		}
		catch (Exception ex)
		{
			Message = $"Could not refresh your library: {ex.Message}";
		}
		finally
		{
			IsSyncing = false;
		}
	}

	[RelayCommand(AllowConcurrentExecutions = true)]
	private async Task Download(BookItemViewModel item)
	{
		if (item.State != DownloadState.NotDownloaded)
			return;

		using var cancellation = new CancellationTokenSource();
		item.DownloadCancellation = cancellation;
		item.DownloadProgress = 0;
		item.State = DownloadState.Downloading;
		RefreshItem(item);
		// A download resumed after a restart keeps its place in the list; a new one goes to the top.
		if (settings.GetDownloadTime(item.Book.Asin) is null)
		{
			settings.SetDownloadTime(item.Book.Asin, DateTimeOffset.UtcNow);
			if (Sort == SORT_LISTENED)
				ApplySort();
		}

		// Progress arrives on a worker thread; throttle to whole percents to keep the UI calm.
		var lastPercent = -1;
		var progress = new Progress<double>(p =>
		{
			var percent = (int)(p * 100);
			if (percent == lastPercent)
				return;
			lastPercent = percent;
			Dispatcher.UIThread.Post(() =>
			{
				item.DownloadProgress = p;
				RefreshItem(item);
			});
		});

		try
		{
			var api = await account.GetApiAsync();
			await Task.Run(() => downloader.DownloadAsync(api, item.Book, progress, cancellation.Token));
			item.State = DownloadState.Downloaded;
			// The PDF that comes with it, so it opens offline. A failure leaves the book downloaded; it is fetched on opening.
			if (item.Book.HasPdf && pdfs is not null)
				_ = Task.Run(async () =>
				{
					try
					{
						await pdfs.EnsureAsync(item.Book.Asin);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"PDF for {item.Book.Asin} not downloaded: {ex.Message}");
					}
				});
		}
		catch (OperationCanceledException)
		{
			item.State = DownloadState.NotDownloaded;
			settings.SetDownloadTime(item.Book.Asin, null);
		}
		catch (Exception ex)
		{
			item.State = DownloadState.NotDownloaded;
			settings.SetDownloadTime(item.Book.Asin, null);
			Message = $"{item.Title} could not be downloaded: {ex.Message}";
		}
		finally
		{
			item.DownloadCancellation = null;
			RefreshItem(item);
			ApplyFilter();
		}
	}

	/// <summary>PDFs for books downloaded before PDFs came with them, one at a time in the background.</summary>
	private async Task FetchMissingPdfsAsync()
	{
		if (pdfs is null)
			return;
		foreach (var item in allBooks.Where(b => b.IsDownloaded && b.Book.HasPdf && !pdfs.Has(b.Book.Asin)).ToList())
		{
			try
			{
				await pdfs.EnsureAsync(item.Book.Asin);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"PDF for {item.Book.Asin} not downloaded: {ex.Message}");
			}
		}
	}

	/// <summary>Carry on with downloads that were under way when the app was closed.</summary>
	private void ResumeDownloads()
	{
		foreach (var asin in downloader.PendingDownloads())
		{
			if (Find(asin) is { State: DownloadState.NotDownloaded } item)
				_ = DownloadCommand.ExecuteAsync(item);
		}
	}

	[RelayCommand]
	private void CancelDownload(BookItemViewModel item) => item.DownloadCancellation?.Cancel();

	/// <summary>Free up space. The book stays in the library and can be downloaded again.</summary>
	public void RemoveDownload(BookItemViewModel item)
	{
		catalog.DeleteDownload(item.Book.Asin);
		pdfs?.Delete(item.Book.Asin);
		settings.SetDownloadTime(item.Book.Asin, null);
		item.State = DownloadState.NotDownloaded;
		RefreshItem(item);
		ApplyFilter();
	}

	/// <summary>Set a book to finished, or back to not started, here and on Audible where the book has been downloaded.</summary>
	public void MarkFinished(BookItemViewModel item, bool finished)
	{
		var position = finished ? item.Book.Length : TimeSpan.Zero;
		// A position of zero is kept, with its time, so an older one from Audible does not come back.
		settings.SetPosition(item.Book.Asin, position);
		RefreshItem(item);
		ApplySort();

		if (!settings.SyncPosition || settings.GetContentReference(item.Book.Asin) is null)
			return;
		var asin = item.Book.Asin;
		_ = Task.Run(async () =>
		{
			try
			{
				await annotations.SetPositionAsync(asin, position);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Could not send the position of {asin} to Audible: {ex.Message}");
			}
		});
	}

	#region Export

	/// <summary>The books shown now, as JSON for an AI or a spreadsheet, with how much and how lately each was listened to.</summary>
	public string ExportJson() => LibraryExport.ToJson(
		Books.Select(b => new ExportInput(
			b.Book,
			b.State == DownloadState.Downloaded,
			settings.GetPosition(b.Book.Asin),
			settings.GetPositionTime(b.Book.Asin),
			settings.GetBookSpeed(b.Book.Asin))),
		listeningLog.Sessions,
		SelectionDescription,
		DateTimeOffset.Now);

	/// <summary>A file name for the export, such as "libation-library-2026-10-04.json".</summary>
	public static string ExportFileName => $"libation-library-{DateTime.Now:yyyy-MM-dd}.json";

	/// <summary>Which books the export holds, in words: the list, filters and search that were in force.</summary>
	private string SelectionDescription
	{
		get
		{
			var parts = new List<string> { ShowDownloadedOnly ? "downloaded books" : "whole library" };
			if (!IsProgressAny)
				parts.Add(progressFilter switch { "notstarted" => "not started", "started" => "in progress", _ => "finished" });
			if (!IsLengthAny)
				parts.Add(lengthFilter switch { "short" => "under 5 hours", "medium" => "5 to 15 hours", _ => "over 15 hours" });
			if (!IsSeriesAny)
				parts.Add(seriesFilter == "series" ? "in a series" : "standalone");
			if (pdfFilter)
				parts.Add("has a PDF");
			if (IsSearching)
				parts.Add($"search \"{SearchText!.Trim()}\"");
			return string.Join(", ", parts);
		}
	}

	#endregion

	#region Sorting

	public const string SORT_LISTENED = "listened";
	public const string SORT_FINISHED = "finished";
	public const string SORT_RECENT = "recent";
	public const string SORT_TITLE = "title";
	public const string SORT_AUTHOR = "author";
	public const string SORT_IN_PROGRESS = "progress";

	private string Sort => settings.LibrarySort ?? SORT_LISTENED;
	public bool IsSortListened => Sort == SORT_LISTENED;
	public bool IsSortFinished => Sort == SORT_FINISHED;
	public bool IsSortRecent => Sort == SORT_RECENT;
	public bool IsSortInProgress => Sort == SORT_IN_PROGRESS;
	public bool IsSortTitle => Sort == SORT_TITLE;
	public bool IsSortAuthor => Sort == SORT_AUTHOR;

	[RelayCommand]
	private void SortBy(string sort)
	{
		settings.LibrarySort = sort;
		OnPropertyChanged(nameof(IsSortListened));
		OnPropertyChanged(nameof(IsSortFinished));
		OnPropertyChanged(nameof(IsSortRecent));
		OnPropertyChanged(nameof(IsSortInProgress));
		OnPropertyChanged(nameof(IsSortTitle));
		OnPropertyChanged(nameof(IsSortAuthor));
		ApplySort();
	}

	/// <summary>When the book was last played, here or (through sync) on another device. Oldest for never.</summary>
	private DateTimeOffset LastListened(BookItemViewModel book)
		=> book.HasProgress && settings.GetPositionTime(book.Book.Asin) is DateTimeOffset time ? time : DateTimeOffset.MinValue;

	/// <summary>For the recent order: the last time the book was listened to, downloaded, or bought, so a book just bought is at the top.</summary>
	private DateTimeOffset LastActivity(BookItemViewModel book)
	{
		var latest = LastListened(book);
		if (book.State != DownloadState.NotDownloaded && settings.GetDownloadTime(book.Book.Asin) is { } downloaded && downloaded > latest)
			latest = downloaded;
		return book.Book.Purchased > latest ? book.Book.Purchased : latest;
	}

	private void ApplySort()
	{
		IEnumerable<BookItemViewModel> sorted = Sort switch
		{
			SORT_LISTENED => allBooks.OrderByDescending(LastActivity),
			// Finished books, the most recently finished first; then the rest, newest first.
			SORT_FINISHED => allBooks.OrderByDescending(b => b.IsFinished).ThenByDescending(b => b.IsFinished ? LastListened(b) : DateTimeOffset.MinValue).ThenByDescending(b => b.Book.Purchased),
			SORT_TITLE => allBooks.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
			SORT_AUTHOR => allBooks.OrderBy(b => b.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
			// Books part-way through first, the furthest along at the top; then the rest, newest first.
			SORT_IN_PROGRESS => allBooks.OrderByDescending(b => b.Progress is > 0 and < 1).ThenByDescending(b => b.Progress is > 0 and < 1 ? LastListened(b) : DateTimeOffset.MinValue).ThenByDescending(b => b.Book.Purchased),
			_ => allBooks.OrderByDescending(b => b.Book.Purchased)
		};
		allBooks = sorted.ToList();
		ApplyFilter();
	}

	#endregion

	public void RefreshProgress()
	{
		foreach (var item in allBooks)
			RefreshItem(item);
	}

	/// <summary>Downloaded and downloading books, in the library's order, for the Downloads tab.</summary>
	public IReadOnlyList<BookItemViewModel> AllDownloaded() => allBooks.Where(b => b.State != DownloadState.NotDownloaded).ToList();

	public BookItemViewModel? Find(string? asin) => allBooks.FirstOrDefault(b => b.Book.Asin == asin);

	/// <summary>
	/// The listener's next book in this one's series: the lowest numbered after it that they own and have not finished.
	/// Null at the end of a series, or for a book in none. Read from the library, so it works offline.
	/// </summary>
	public BookItemViewModel? NextInSeries(BookItemViewModel item)
	{
		foreach (var membership in item.Book.Series ?? [])
		{
			if (SeriesNumber(membership.Sequence) is not double here)
				continue;
			var next = allBooks
				.Where(b => b != item && b.IsNotFinished)
				.Select(b => (Book: b, At: SeriesNumber(b.Book.Series?.FirstOrDefault(s => s.Id == membership.Id)?.Sequence)))
				.Where(x => x.At > here)
				.OrderBy(x => x.At)
				// Two editions of one book: the one on the phone.
				.ThenByDescending(x => x.Book.IsDownloaded)
				.Select(x => x.Book)
				.FirstOrDefault();
			if (next is not null)
				return next;
		}
		return null;
	}

	/// <summary>The listener's books in this book's first series, in reading order, one edition of each place.</summary>
	public IReadOnlyList<BookItemViewModel> SeriesOf(BookItemViewModel item)
	{
		if (item.Book.Series is not { Count: > 0 } memberships)
			return [];
		var id = memberships[0].Id;
		return allBooks
			.Select(b => (Book: b, At: SeriesNumber(b.Book.Series?.FirstOrDefault(s => s.Id == id)?.Sequence)))
			.Where(x => x.At is not null)
			.GroupBy(x => x.At)
			.OrderBy(g => g.Key)
			.Select(g => g.OrderByDescending(x => x.Book.IsDownloaded).First().Book)
			.ToList();
	}

	/// <summary>A book's place in its series as a number: "2" is 2, "2.5" is 2.5, and an omnibus "1-3" counts as 1.</summary>
	internal static double? SeriesNumber(string? sequence)
	{
		if (string.IsNullOrWhiteSpace(sequence))
			return null;
		var digits = new string(sequence.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
		return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
	}

	/// <summary>A book in the library with this title by this author: another edition of a store listing.</summary>
	public BookItemViewModel? FindByTitle(string title, string authors)
		=> allBooks.FirstOrDefault(b => string.Equals(b.Title, title, StringComparison.CurrentCultureIgnoreCase)
			&& (b.Author.Contains(authors, StringComparison.CurrentCultureIgnoreCase) || authors.Contains(b.Author, StringComparison.CurrentCultureIgnoreCase)));

	/// <summary>Where a downloaded book's audio file is.</summary>
	public string BookPath(string asin) => catalog.BookPath(asin);

	public async Task<LocalBook> ToLocalBookAsync(BookItemViewModel item) => new(
		item.Book.Asin,
		catalog.BookPath(item.Book.Asin),
		item.Title,
		item.Author,
		item.Book.Narrators,
		item.Book.Length,
		await catalog.GetCoverAsync(item.Book));

	public Task<byte[]?> GetCoverBytesAsync(BookItemViewModel item) => catalog.GetCoverAsync(item.Book);

	private void RefreshItem(BookItemViewModel item) => item.Refresh(settings.GetPosition(item.Book.Asin), settings.GetBookSpeed(item.Book.Asin) ?? settings.Speed);

	private void SetBooks(IReadOnlyList<CatalogBook> books)
	{
		// A book listed twice, as in a cache saved before repeats were dropped, is one book.
		books = books.DistinctBy(b => b.Asin).ToList();
		// Keep the existing row for a book, so a download in progress survives a sync.
		var existing = allBooks.ToDictionary(b => b.Book.Asin);
		allBooks = books
			.Select(b => existing.TryGetValue(b.Asin, out var item)
				? item
				: new BookItemViewModel(b, catalog.IsDownloaded(b.Asin) ? DownloadState.Downloaded : DownloadState.NotDownloaded, catalog.GetCoverAsync))
			.ToList();
		var latest = books.ToDictionary(b => b.Asin);
		foreach (var item in allBooks)
		{
			item.Update(latest[item.Book.Asin]);
			RefreshItem(item);
		}
		ApplySort();
		OnPropertyChanged(nameof(IsLibraryEmpty));
	}

	private void ApplyFilter()
	{
		IEnumerable<BookItemViewModel> shown = allBooks;
		if (ShowDownloadedOnly)
			shown = shown.Where(b => b.State != DownloadState.NotDownloaded);
		if (HasActiveFilters)
			shown = shown.Where(PassesFilters);
		if (IsSearching)
		{
			// Every word must appear somewhere in the title, author or narrator.
			var words = SearchText!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			shown = shown.Where(b => words.All(w => b.SearchableText.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
		}
		Books = ReferenceEquals(shown, allBooks) ? allBooks : shown.ToList();
	}
}
