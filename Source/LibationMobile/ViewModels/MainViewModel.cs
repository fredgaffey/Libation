using AudibleApi;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibationMobile.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace LibationMobile.ViewModels;

public enum Page
{
	SignIn,
	Browser,
	Library,
	Podcasts,
	Downloads,
	NowPlaying,
	Settings,
	Details,
	Log
}

/// <summary>A heading in the Downloads tab.</summary>
public record DownloadsHeading(string Text);

/// <summary>Amazon's sign-in page, shown in the app. Completed with the URL Amazon redirects to after sign-in, or null if cancelled.</summary>
public class LoginRequest(string url, CookieCollection? cookies)
{
	private readonly TaskCompletionSource<string?> completion = new();

	public string Url { get; } = url;
	public CookieCollection? Cookies { get; } = cookies;
	public string UserAgent => DeviceRegistrationProfile.Default.UserAgent;
	public Task<string?> Result => completion.Task;

	public void Complete(string? responseUrl) => completion.TrySetResult(responseUrl);
}

/// <summary>App shell: which page is showing, the Audible account, and the book loaded for playback.</summary>
public partial class MainViewModel : ObservableObject, ILoginChoiceEager
{
	private readonly MobileSettings settings;
	private readonly AudibleAccount account;
	private readonly AudibleAnnotations annotations;
	private readonly LocalAnnotations localAnnotations;

	public LibraryViewModel Library { get; }
	public IReadOnlyList<string> RegionNames { get; } = AudibleAccount.Regions.Select(r => r.DisplayName).ToList();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSignInPage), nameof(IsBrowserPage), nameof(IsLibraryPage), nameof(IsNowPlayingPage), nameof(IsSettingsPage), nameof(IsDetailsPage), nameof(IsLogPage), nameof(ShowMiniPlayer),
		nameof(IsPodcastsPage), nameof(IsDownloadsPage), nameof(IsTabPage))]
	private Page currentPage = Page.Library;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowMiniPlayer))]
	private NowPlayingViewModel? nowPlaying;

	[ObservableProperty]
	private LoginRequest? login;

	[ObservableProperty]
	private int selectedRegionIndex;

	[ObservableProperty]
	private bool isSigningIn;

	[ObservableProperty]
	private string? error;

	public bool IsSignInPage => CurrentPage == Page.SignIn;
	public bool IsBrowserPage => CurrentPage == Page.Browser;
	public bool IsLibraryPage => CurrentPage == Page.Library;
	public bool IsPodcastsPage => CurrentPage == Page.Podcasts;
	public bool IsDownloadsPage => CurrentPage == Page.Downloads;
	/// <summary>One of the three tabs, which show the tab bar and the mini player.</summary>
	public bool IsTabPage => CurrentPage is Page.Library or Page.Podcasts or Page.Downloads;
	public bool IsNowPlayingPage => CurrentPage == Page.NowPlaying;
	public bool IsSettingsPage => CurrentPage == Page.Settings;
	public bool IsDetailsPage => CurrentPage == Page.Details;
	public bool IsLogPage => CurrentPage == Page.Log;

	private readonly ListeningLog listeningLog;
	public ListeningLogViewModel Log { get; }

	/// <summary>Where Back goes from the details and log pages: the player, if they were opened from it.</summary>
	private Page returnPage = Page.Library;

	[RelayCommand]
	private void ShowLog()
	{
		returnPage = CurrentPage == Page.Settings ? Page.Settings : Page.Library;
		Log.Refresh();
		CurrentPage = Page.Log;
	}

	/// <summary>The details page for the book playing, from the player's top bar.</summary>
	[RelayCommand]
	private void ShowPlayingDetails()
	{
		if (NowPlaying is null || Library.Find(NowPlaying.Book.Id) is not { } item)
			return;
		ShowDetails(item);
	}

	/// <summary>The book whose details page is open.</summary>
	[ObservableProperty]
	private BookDetailsViewModel? details;

	/// <summary>Opens a web address outside the app. Set by the view, which knows the window.</summary>
	public Func<Uri, Task>? OpenUri { get; set; }

	private readonly AudibleSeries store;

	[RelayCommand]
	private void ShowDetails(BookItemViewModel item)
	{
		// Moving between books in a series keeps the way back to where the details were first opened.
		if (CurrentPage != Page.Details)
			returnPage = CurrentPage == Page.NowPlaying ? Page.NowPlaying : Page.Library;
		Details = new BookDetailsViewModel(item, store, Library, Transcripts);
		CurrentPage = Page.Details;
	}

	/// <summary>A book in a series: show it if it is in the library, or open its store page if not.</summary>
	[RelayCommand]
	private async Task OpenSeriesBook(SeriesRowViewModel row)
	{
		if (row.Owned is { } owned)
		{
			if (!row.IsCurrent)
				ShowDetails(owned);
		}
		else if (OpenUri is not null)
			await OpenUri(store.StorePage(row.Book.Asin));
	}

	/// <summary>Open the PDF that comes with the book on the details page, where it can be read and saved.</summary>
	[RelayCommand]
	private Task OpenPdf() => Details is { } details ? OpenBookPdf(details.Item) : Task.CompletedTask;

	/// <summary>
	/// A book's companion PDF (maps, tables, figures), downloaded the first time and kept, shown in the app's own
	/// viewer. Where there is no viewer, as on Android for now, Audible's link is opened instead.
	/// </summary>
	[RelayCommand]
	private async Task OpenBookPdf(BookItemViewModel item)
	{
		void Say(string? text)
		{
			if (Details is { } details && details.Item == item)
				details.Message = text;
			else
				Library.Message = text;
		}
		var asin = item.Book.Asin;
		try
		{
			if (DocumentViewer.Platform is not { } viewer)
			{
				if (OpenUri is not null)
					await OpenUri(await store.GetPdfLinkAsync(asin));
				return;
			}
			if (!pdfs.Has(asin))
				Say("Downloading the PDF…");
			var path = await pdfs.EnsureAsync(asin);
			Say(null);
			viewer.Show(path, item.Title);
		}
		catch (Exception ex)
		{
			Say($"The PDF could not be opened: {ex.Message}");
		}
	}

	private readonly BookPdfs pdfs;
	/// <summary>Books transcribed on the device, for the details page.</summary>
	public TranscriptJobs Transcripts { get; }
	public Experiments Experiments { get; }
	public ListeningEvents Events { get; }

	/// <summary>Play the book on the details page, or download it if it is not on the device.</summary>
	[RelayCommand]
	private async Task OpenDetailsBook()
	{
		if (Details is not null)
			await OpenBook(Details.Item);
	}
	public SettingsViewModel Settings { get; }
	public bool ShowMiniPlayer => NowPlaying is not null && IsTabPage;
	public string AccountText => $"Audible {AudibleAccount.DisplayNameOf(settings.RegionName)}";

	public MainViewModel(string dataDirectory)
	{
		settings = new MobileSettings(Path.Combine(dataDirectory, "settings.json"));
		account = new AudibleAccount(Path.Combine(dataDirectory, "audible-identity.json"), settings);
		annotations = new AudibleAnnotations(account, settings);
		store = new AudibleSeries(account);
		pdfs = new BookPdfs(dataDirectory, store);
		Experiments = new Experiments(dataDirectory);
		Events = new ListeningEvents(dataDirectory);
		Settings = new SettingsViewModel(settings, () => NowPlaying?.SettingsChanged(), speed => { if (NowPlaying is { } np) np.Speed = speed; })
		{
			currentSpeed = () => (float)(NowPlaying?.Speed ?? settings.Speed)
		};
		localAnnotations = new LocalAnnotations(Path.Combine(dataDirectory, "annotations.json"));
		listeningLog = new ListeningLog(Path.Combine(dataDirectory, "listening-log.json"));
		// Last night's sleep, if a watch has synced it to Apple Health since: where in each book it began.
		if (settings.UseHealthSleep && ListeningContext.Platform is { } sleepContext)
		{
			var logForSleep = listeningLog;
			_ = Task.Run(async () =>
			{
				await SleepFinder.RefineAsync(logForSleep, sleepContext);
				// Auto-pause's hours follow the listener's usual sleep until they set their own.
				if (!settings.AutoPauseHoursChosen && await SleepFinder.UsualSleepAsync(sleepContext) is { } usual)
				{
					settings.AutoPauseFromMinute = usual.From;
					settings.AutoPauseUntilMinute = usual.Until;
				}
			});
		}
		var catalogForLog = (LibraryViewModel?)null;
		Log = new ListeningLogViewModel(listeningLog, new AudibleStats(account, dataDirectory),
			asin => catalogForLog?.Find(asin));
		Log.Experiments = Experiments;
		Log.Events = Events;
		Log.Profiles = () => settings.Profiles;
		Log.AudibleAppSpeed = () => settings.AudibleAppSpeed;
		Log.SetAudibleAppSpeed = speed => settings.AudibleAppSpeed = (float)speed;
		Log.BookHours = asin => catalogForLog?.Find(asin) is { } found && found.Book.Length > TimeSpan.Zero ? found.Book.Length.TotalHours : null;
		Settings.TimeBreakdown = Log.Breakdown;
		if (ListeningContext.Platform is { } headContext)
			headContext.UseHead = settings.UseHeadMovement;
		Settings.ListeningLog = listeningLog;
		var catalog = new LibraryCatalog(dataDirectory);
		Transcripts = new TranscriptJobs(new BookTranscripts(dataDirectory), catalog.BookPath);
		Library = new LibraryViewModel(catalog, account, new BookDownloader(catalog, settings), settings, annotations, listeningLog, pdfs);
		catalogForLog = Library;
		Podcasts = new PodcastsViewModel(new PodcastLibrary(dataDirectory), settings);
		Podcasts.PlayRequested += row => _ = PlayEpisodeAsync(row);
		Podcasts.DownloadsChanged += RefreshDownloads;
		Podcasts.DownloadsChanged += RefreshPlayableBooks;
		StartVoiced(dataDirectory);
		Library.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName != nameof(LibraryViewModel.Books))
				return;
			if (IsDownloadsPage)
				RefreshDownloads();
			RefreshPlayableBooks();
		};
		// The refresh button refreshes everything Audible holds, including the loaded book's position and bookmarks.
		Library.SyncCommand.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Library.SyncCommand.IsRunning) && !Library.SyncCommand.IsRunning)
				NowPlaying?.RefreshFromAudible();
		};
		SelectedRegionIndex = Math.Max(0, AudibleAccount.Regions.ToList().FindIndex(r => r.Name == "us"));
	}

	public async Task InitializeAsync()
	{
		if (!account.IsSignedIn)
		{
			CurrentPage = Page.SignIn;
			return;
		}

		await Library.LoadAsync();
		SharedDocuments.Handle(OpenSharedDocument);

		// A book asked for on the widget or by Siri, which may be what started the app.
		if (HomeWidget.Platform is { } widget)
		{
			widget.OpenRequested += (id, play) => Dispatcher.UIThread.Post(() => OpenFromWidget(id, play));
			RefreshPlayableBooks();
			if (widget.TakePendingOpen() is { } pending)
			{
				OpenFromWidget(pending.Id, pending.Play);
				return;
			}
		}

#if DEBUG
		// Tests on the real phone while another app plays: no book loaded, so nothing here touches the audio.
		if (Environment.GetEnvironmentVariable("LIBATION_NO_RESTORE") is { Length: > 0 })
			return;
#endif
		// Put the last book back in the mini player, paused, so one tap resumes it.
		if (Library.Find(settings.LastBookId) is { IsDownloaded: true } last)
			await LoadAsync(last, showNowPlaying: false);
		else if (Podcasts.FindDownloaded(settings.LastBookId) is { } lastEpisode)
			await LoadEpisodeAsync(lastEpisode, showNowPlaying: false);
		else if (voiced.Find(settings.LastBookId) is { } lastVoiced)
			await LoadVoicedAsync(lastVoiced, showNowPlaying: false);
	}

	/// <summary>Shows a book in the player, and plays it if asked (Siri's "play Dune"); a recent book on the widget only opens.</summary>
	private async void OpenFromWidget(string bookId, bool play)
	{
		var opened = NowPlaying?.Book.Id == bookId
			|| Library.Find(bookId) is { IsDownloaded: true } item && await LoadAsync(item, showNowPlaying: true)
			|| Podcasts.FindDownloaded(bookId) is { } episode && await LoadEpisodeAsync(episode, showNowPlaying: true);
		if (!opened)
			return;
		CurrentPage = Page.NowPlaying;
		if (play && NowPlaying is { IsPlaying: false } np)
			np.PlayPause();
	}

	/// <summary>
	/// Tell Siri which books can be played, last listened first: downloaded audiobooks and podcast episodes. Kept up to
	/// date as books are downloaded, removed and played.
	/// </summary>
	private void RefreshPlayableBooks()
	{
		if (HomeWidget.Platform is not { } widget)
			return;
		var books = Library.AllDownloaded().Where(b => b.IsDownloaded).Select(b => (Book: new PlayableBook(b.Book.Asin, b.Title, b.Author), At: settings.GetPositionTime(b.Book.Asin)));
		var episodes = Podcasts.DownloadedRows().Select(e => (Book: new PlayableBook(e.Episode.Id, e.Title, e.ShowTitle), At: settings.GetPositionTime(e.Episode.Id)));
		widget.ShowBooks(books.Concat(episodes)
			.OrderByDescending(b => b.Book.Id == settings.LastBookId)
			.ThenByDescending(b => b.At ?? DateTimeOffset.MinValue)
			.Select(b => b.Book)
			.ToList());
	}

	#region Sign in

	[RelayCommand]
	private async Task SignIn()
	{
		Error = null;
		IsSigningIn = true;
		try
		{
			await account.SignInAsync(AudibleAccount.Regions[SelectedRegionIndex].Name, this);
			OnPropertyChanged(nameof(AccountText));
			CurrentPage = Page.Library;
			await Library.LoadAsync();
		}
		catch (OperationCanceledException)
		{
			CurrentPage = Page.SignIn;
		}
		catch (Exception ex)
		{
			CurrentPage = Page.SignIn;
			Error = $"Sign-in did not complete: {ex.Message}";
		}
		finally
		{
			IsSigningIn = false;
			Login = null;
		}
	}

	/// <summary>Called by AudibleApi with Amazon's sign-in page. Shows it in the app and waits for the user to finish.</summary>
	async Task<string?> ILoginChoiceEager.StartAsync(ChoiceIn choiceIn)
	{
		var request = new LoginRequest(choiceIn.LoginUrl, choiceIn.SignInCookies);
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			Login = request;
			CurrentPage = Page.Browser;
		});
		return await request.Result;
	}

	[RelayCommand]
	private void CancelSignIn() => Login?.Complete(null);

	[ObservableProperty]
	private bool showImport;

	/// <summary>An account export pasted from Libation desktop.</summary>
	[ObservableProperty]
	private string? importText;

	[RelayCommand]
	private void ToggleImport() => ShowImport = !ShowImport;

	[RelayCommand]
	private Task ImportAccount() => ImportAccountAsync(ImportText);

	public async Task ImportAccountAsync(string? exportJson)
	{
		if (string.IsNullOrWhiteSpace(exportJson))
		{
			Error = "Paste the account export from Libation on your computer first.";
			return;
		}

		Error = null;
		IsSigningIn = true;
		try
		{
			await account.ImportAsync(exportJson);
			ImportText = null;
			ShowImport = false;
			OnPropertyChanged(nameof(AccountText));
			CurrentPage = Page.Library;
			await Library.LoadAsync();
		}
		catch (InvalidDataException ex)
		{
			Error = ex.Message;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Account import failed: {ex}");
			Error = $"The account could not be imported: {ex.Message}";
		}
		finally
		{
			IsSigningIn = false;
		}
	}

	[RelayCommand]
	private void ShowSettings()
	{
		Settings.OpenPageCommand.Execute("");
		Settings.RefreshTimeSaved();
		CurrentPage = Page.Settings;
	}

	[RelayCommand]
	private void SignOut()
	{
		NowPlaying?.Dispose();
		NowPlaying = null;
		account.SignOut();
		CurrentPage = Page.SignIn;
	}

	#endregion

	#region Library and playback

	/// <summary>Tapping a title: play it if downloaded, otherwise start downloading it.</summary>
	[RelayCommand(AllowConcurrentExecutions = true)]
	private async Task OpenBook(BookItemViewModel item)
	{
		switch (item.State)
		{
			case DownloadState.NotDownloaded:
				// Start it and return, so this command is not busy (and every row disabled) for the whole download.
				_ = Library.DownloadCommand.ExecuteAsync(item);
				return;
			case DownloadState.Downloading:
				return;
		}

		if (NowPlaying?.Book.Id != item.Book.Asin && !await LoadAsync(item, showNowPlaying: true))
			return;
		CurrentPage = Page.NowPlaying;
		if (!NowPlaying!.IsPlaying)
			NowPlaying.PlayPause();
	}

	[RelayCommand]
	private void RemoveDownload(BookItemViewModel item)
	{
		if (NowPlaying?.Book.Id == item.Book.Asin)
		{
			NowPlaying.Dispose();
			NowPlaying = null;
		}
		Library.RemoveDownload(item);
	}

	[RelayCommand]
	private void MarkFinished(BookItemViewModel item)
	{
		// The player would otherwise carry on from where it was and save over this.
		if (NowPlaying?.Book.Id == item.Book.Asin)
		{
			NowPlaying.Dispose();
			NowPlaying = null;
		}
		Library.MarkFinished(item, finished: true);
	}

	[RelayCommand]
	private void MarkNotStarted(BookItemViewModel item)
	{
		if (NowPlaying?.Book.Id == item.Book.Asin)
		{
			NowPlaying.Dispose();
			NowPlaying = null;
		}
		Library.MarkFinished(item, finished: false);
	}

	/// <summary>Open a downloaded book at its first second, whatever was listened to before.</summary>
	[RelayCommand]
	private async Task PlayFromStart(BookItemViewModel item)
	{
		if (item.State != DownloadState.Downloaded)
			return;
		MarkNotStarted(item);
		await OpenBook(item);
	}

	[RelayCommand]
	private void ShowNowPlaying()
	{
		if (NowPlaying is not null)
			CurrentPage = Page.NowPlaying;
	}

	[RelayCommand]
	private void GoToLibrary() => ShowLibrary();

	/// <summary>The tab last shown, which leaving the player or settings goes back to.</summary>
	private Page lastTab = Page.Library;

	partial void OnCurrentPageChanged(Page value)
	{
		if (value is Page.Library or Page.Podcasts or Page.Downloads)
			lastTab = value;
		if (value == Page.Downloads)
			RefreshDownloads();
	}

	[RelayCommand]
	private void ShowTab(string tab)
	{
		CurrentPage = tab switch { "podcasts" => Page.Podcasts, "downloads" => Page.Downloads, _ => Page.Library };
		if (CurrentPage == Page.Library)
			Library.RefreshProgress();
		else if (CurrentPage == Page.Podcasts)
			Podcasts.RefreshProgress();
	}

	/// <summary>Leave Now Playing or the sign-in browser. Playback continues.</summary>
	/// <returns>False if there is nowhere to go back to, so a back press can close the app instead.</returns>
	public bool ShowLibrary()
	{
		switch (CurrentPage)
		{
			case Page.Browser:
				Login?.Complete(null);
				return true;
			case Page.NowPlaying when NowPlaying is { IsChapterListOpen: true }:
				NowPlaying.IsChapterListOpen = false;
				return true;
			case Page.NowPlaying when NowPlaying is { IsClipEditorOpen: true }:
				NowPlaying.CancelClipCommand.Execute(null);
				return true;
			case Page.NowPlaying when NowPlaying is { IsHistoryOpen: true }:
				NowPlaying.IsHistoryOpen = false;
				return true;
			case Page.NowPlaying when NowPlaying is { IsAnnotationListOpen: true }:
				NowPlaying.IsAnnotationListOpen = false;
				return true;
			case Page.NowPlaying:
				CurrentPage = lastTab;
				Library.RefreshProgress();
				Podcasts.RefreshProgress();
				return true;
			case Page.Settings when Settings.Back():
				return true;
			case Page.Settings:
				CurrentPage = lastTab;
				return true;
			case Page.Podcasts when Podcasts.IsShowOpen:
				Podcasts.CloseShowCommand.Execute(null);
				return true;
			case Page.Podcasts or Page.Downloads:
				CurrentPage = Page.Library;
				return true;
			case Page.Details or Page.Log:
				CurrentPage = returnPage;
				returnPage = Page.Library;
				return true;
			default:
				return false;
		}
	}

	/// <summary>The app came back to the front: pick up anything that changed on other devices.</summary>
	public void OnAppResumed()
	{
		if (!account.IsSignedIn)
			return;
		NowPlaying?.RefreshFromAudible();
		_ = Library.RefreshPositionsAsync(force: false);
	}

	/// <summary>The app is leaving the front: save and report where the listener is.</summary>
	public void OnAppBackgrounded() => NowPlaying?.OnAppBackgrounded();

	/// <summary>Keep the playing book's library row in step with playback, not just with the last save.</summary>
	partial void OnNowPlayingChanged(NowPlayingViewModel? oldValue, NowPlayingViewModel? newValue)
	{
		if (newValue is not null)
		{
			newValue.Experiments = Experiments;
			newValue.Events = Events;
			newValue.MoreRequested += what =>
			{
				if (what == "details")
					ShowPlayingDetails();
				else
				{
					ShowSettings();
					if (what == "sleep-settings")
						Settings.OpenPageCommand.Execute("sleep");
					else if (what == "training-settings")
						Settings.OpenPageCommand.Execute("training");
				}
			};
		}
		Library.PlayingBookId = newValue?.Book.Id;
		// The book playing goes to the top of Siri's list.
		RefreshPlayableBooks();
		if (oldValue is not null)
			oldValue.PropertyChanged -= NowPlaying_PropertyChanged;
		if (newValue is not null)
			newValue.PropertyChanged += NowPlaying_PropertyChanged;
	}

	private void NowPlaying_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (sender is not NowPlayingViewModel np || e.PropertyName is not (nameof(NowPlayingViewModel.Position) or nameof(NowPlayingViewModel.Speed)))
			return;
		if (Library.Find(np.Book.Id) is { } book)
			book.Refresh(np.Position, np.Speed);
		else if (Podcasts.FindDownloaded(np.Book.Id) is { } episode)
			Podcasts.RefreshRow(episode);
	}

	#region Podcasts and downloads

	public PodcastsViewModel Podcasts { get; }

	/// <summary>The Downloads tab: audiobooks, then podcast episodes, each under a heading.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNoDownloads))]
	private IReadOnlyList<object> downloadRows = [];

	public bool HasNoDownloads => DownloadRows.Count == 0;

	private void RefreshDownloads()
	{
		var rows = new List<object>();
		// Fresh from the store first: downloading now, then the newest downloads, then by when last listened.
		var books = Library.AllDownloaded()
			.OrderByDescending(b => b.IsDownloading)
			.ThenByDescending(b => settings.GetDownloadTime(b.Book.Asin) ?? DateTimeOffset.MinValue)
			.ThenByDescending(b => settings.GetPositionTime(b.Book.Asin) ?? DateTimeOffset.MinValue)
			.ToList();
		if (books.Count > 0)
		{
			rows.Add(new DownloadsHeading(books.Count == 1 ? "1 audiobook" : $"{books.Count} audiobooks"));
			rows.AddRange(books);
		}
		var voicedBooks = VoicedRows();
		if (voicedBooks.Count > 0)
		{
			rows.Add(new DownloadsHeading(voicedBooks.Count == 1 ? "1 voiced book" : $"{voicedBooks.Count} voiced books"));
			rows.AddRange(voicedBooks);
		}
		var episodes = Podcasts.DownloadedRows();
		if (episodes.Count > 0)
		{
			rows.Add(new DownloadsHeading(episodes.Count == 1 ? "1 podcast episode" : $"{episodes.Count} podcast episodes"));
			rows.AddRange(episodes);
		}
		DownloadRows = rows;
	}

	private async Task PlayEpisodeAsync(EpisodeItemViewModel row)
	{
		if (NowPlaying?.Book.Id != row.Episode.Id && !await LoadEpisodeAsync(row, showNowPlaying: true))
			return;
		CurrentPage = Page.NowPlaying;
		if (!NowPlaying!.IsPlaying)
			NowPlaying.PlayPause();
	}

	[RelayCommand]
	private async Task OpenEpisode(EpisodeItemViewModel row)
	{
		if (row.IsDownloaded)
			await PlayEpisodeAsync(row);
		else
			await Podcasts.PlayCommand.ExecuteAsync(row);
	}

	[RelayCommand]
	private void RemoveEpisodeDownload(EpisodeItemViewModel row)
	{
		if (NowPlaying?.Book.Id == row.Episode.Id)
		{
			NowPlaying.Dispose();
			NowPlaying = null;
		}
		Podcasts.RemoveDownloadCommand.Execute(row);
	}

	/// <summary>Open an episode in the player. No Audible sync: Audible knows nothing of podcasts.</summary>
	private async Task<bool> LoadEpisodeAsync(EpisodeItemViewModel row, bool showNowPlaying)
	{
		Error = null;
		NowPlaying?.Dispose();
		NowPlaying = null;
		try
		{
			NowPlaying = await NowPlayingViewModel.OpenAsync(await Podcasts.ToLocalBookAsync(row), settings, annotations: null, localAnnotations, listeningLog);
			if (showNowPlaying)
				CurrentPage = Page.NowPlaying;
			return true;
		}
		catch (Exception ex)
		{
			Error = $"{row.Title} could not be opened: {ex.Message}";
			return false;
		}
	}

	#endregion

	private async Task<bool> LoadAsync(BookItemViewModel item, bool showNowPlaying)
	{
		Error = null;
		NowPlaying?.Dispose();
		NowPlaying = null;
		try
		{
			NowPlaying = await NowPlayingViewModel.OpenAsync(await Library.ToLocalBookAsync(item), settings, annotations, localAnnotations, listeningLog);
			QueueNextInSeries(NowPlaying, item);
			if (showNowPlaying)
				CurrentPage = Page.NowPlaying;
			return true;
		}
		catch (Exception ex)
		{
			Error = $"{item.Title} could not be opened: {ex.Message}";
			return false;
		}
	}

	#endregion

#if DEBUG
	/// <summary>Debug builds only: act on a launch setting, so emulator and simulator runs can be driven without taps.</summary>
	/// <param name="action">"signin", or "play:ASIN[:speed]".</param>
	internal async Task RunTestActionAsync(string action)
	{
		var parts = action.Split(':');
		switch (parts[0])
		{
			case "signin":
				await SignInCommand.ExecuteAsync(null);
				break;
			case "benchspeed":
				// How long this device takes to speed up 30s of 44.1 kHz stereo at 10x, which must be well under 3s to
				// play in real time. Noise in bursts, so the nonlinear analysis has something like speech to work on.
				var input = new float[4096];
				var random = new Random(1);
				var output = new float[8192];
				foreach (var amount in new[] { 1f, 0f, -1f })
				{
					// -1 stands for the original method.
					AudioBackend.UseNonlinear = amount >= 0;
					AudioBackend.Nonlinearity = Math.Max(0, amount);
					using var stretcher = AudioBackend.CreateStretcher(44100, 2);
					stretcher.Speed = 10f;
					var produced = 0L;
					var watch = System.Diagnostics.Stopwatch.StartNew();
					for (var frames = 0; frames < 30 * 44100; frames += input.Length / 2)
					{
						var loud = frames / 4410 % 3 != 0;
						for (var i = 0; i < input.Length; i++)
							input[i] = loud ? (float)(Math.Sin((frames + i / 2) * 0.02) * 0.4 + (random.NextDouble() - 0.5) * 0.2) : 0f;
						stretcher.Write(input);
						int read;
						while ((read = stretcher.Read(output)) > 0)
							produced += read / 2;
					}
					Console.WriteLine($"LIBATION_TEST benchspeed: {(amount < 0 ? "original" : $"speedy, nonlinearity {amount}")}: 30s at 10x took {watch.ElapsedMilliseconds} ms (budget 3000 ms), produced {produced / 44100.0:0.00}s");
				}
				AudioBackend.Nonlinearity = settings.Nonlinearity;
				AudioBackend.UseNonlinear = settings.UseNonlinearSpeed;
				break;
			case "widget" when parts.Length > 1 && Library.Find(parts[1]) is { } widgetBook && HomeWidget.Platform is { } widget:
				// Writes a book to the home-screen widget twice (the second replaces the first) and logs what it holds.
				var widgetCover = await Library.GetCoverBytesAsync(widgetBook);
				widget.Show(new WidgetInfo(widgetBook.Book.Asin, widgetBook.Title, widgetBook.Author, widgetCover, widgetBook.Book.Length, 2, false, widgetBook.Book.Length, "Chapter 1", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)));
				widget.Show(new WidgetInfo(widgetBook.Book.Asin, widgetBook.Title, widgetBook.Author, widgetCover, widgetBook.Book.Length, 3, false, widgetBook.Book.Length, "Chapter 1", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)));
				Console.WriteLine($"LIBATION_TEST widget: given cover {widgetCover?.Length ?? 0} bytes; saved {widget}");
				break;
			case "open" when parts.Length > 1 && Library.Find(parts[1]) is { IsDownloaded: true } openBook:
				// Shows the player without playing: playback reports a position to Audible, which a look at the screen must not.
				await LoadAsync(openBook, showNowPlaying: true);
				// "open:ID:speed" also opens a sheet: speed, profile, mode, sleep or more.
				if (parts.Length > 2 && NowPlaying is { } openPlayer)
					openPlayer.OpenSheetCommand.Execute(parts[2]);
				break;
			case "dedupevoiced":
				Console.WriteLine($"LIBATION_TEST dedupevoiced: {voiced.RemoveDuplicates()} removed; left {string.Join(", ", voiced.Books.Select(b => $"{b.Title} ({b.VoiceName})"))}");
				RefreshDownloads();
				break;
			case "audiblestats":
				// Read only: Audible's own record of listening by day, the last fortnight.
				{
					var stats = await new AudibleStats(account, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).FetchAsync();
					foreach (var (day, ms) in stats.DailyMs.OrderByDescending(d => d.Key).Take(14))
						Console.WriteLine($"LIBATION_TEST audiblestats: {day} {ms / 60000:0} min");
				}
				break;
			case "getvoices":
				// Download the neural voices, then list every voice.
				if (BookVoice.Platform is { } installer)
				{
					var lastReport = -1;
					await installer.InstallNeuralVoicesAsync(new Progress<double>(p =>
					{
						if ((int)(p * 10) != lastReport)
							Console.WriteLine($"LIBATION_TEST getvoices: {p:P0}");
						lastReport = (int)(p * 10);
					}), default);
					Console.WriteLine($"LIBATION_TEST getvoices: {string.Join(", ", installer.Voices().Select(v => v.Label))}");
				}
				break;
			case "voicesheet":
				// Downloads, with the voice sheet open: "voicesheet:ADDRESS" also fetches a document into it.
				CurrentPage = Page.Downloads;
				Voice.OpenCommand.Execute(null);
				if (parts.Length > 1)
				{
					Voice.Url = action["voicesheet:".Length..];
					await Voice.FetchUrlCommand.ExecuteAsync(null);
				}
				break;
			case "tab" when parts.Length > 1:
				ShowTabCommand.Execute(parts[1]);
				break;
			case "voicebench":
				// How fast the newest voiced book is read aloud, without playing it: seconds of audio made a second.
				if (BookVoice.Platform is { } benchVoice)
				{
					var benchBook = voiced.Books.FirstOrDefault();
					// "voicebench:SECONDS:VOICE", the voice as "kokoro:af_heart", "espeak:en-us" or Apple's identifier.
					var seconds = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 60;
					var benchVoiceId = parts.Length > 2 ? string.Join(':', parts[2..]) : benchBook?.VoiceId ?? "espeak:en-us";
					// The newest voiced book's text, or one put in Documents/Kokoro for testing.
					var benchText = File.ReadAllText(benchBook is not null
						? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voiced", benchBook.Id, "text.txt")
						: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Kokoro", "text.txt"));
					_ = Task.Run(async () =>
					{
						using var source = benchVoice.Open(benchText, benchVoiceId, BookVoice.SECONDS_PER_CHARACTER);
						Console.WriteLine($"LIBATION_TEST voicebench: {benchVoiceId}, {source.SampleRate} Hz");
						var buffer = new float[4096];
						long frames = 0;
						var waits = 0;
						var watch = System.Diagnostics.Stopwatch.StartNew();
						while (watch.Elapsed.TotalSeconds < seconds)
						{
							var read = source.Read(buffer);
							frames += read;
							if (read < buffer.Length && source.IsWaiting)
							{
								waits++;
								await Task.Delay(20);
							}
							if (watch.ElapsedMilliseconds / 10000 != (watch.ElapsedMilliseconds - 20) / 10000)
								Console.WriteLine($"LIBATION_TEST voicebench: {watch.Elapsed.TotalSeconds:0}s, {frames / (double)source.SampleRate:0}s of audio ({frames / (double)source.SampleRate / watch.Elapsed.TotalSeconds:0.0}x), at {source.TimeAt(frames)}");
						}
						Console.WriteLine($"LIBATION_TEST voicebench: done, {frames / (double)source.SampleRate:0}s of audio in {watch.Elapsed.TotalSeconds:0}s = {frames / (double)source.SampleRate / watch.Elapsed.TotalSeconds:0.0}x real time, {waits} waits, timeline {source.TimeAt(frames)}");
					});
				}
				break;
			case "openvoiced":
				// The newest voiced book, in the player, without playing.
				if (voiced.Books.FirstOrDefault() is { } newestVoiced && await LoadVoicedAsync(newestVoiced, showNowPlaying: true))
					Console.WriteLine($"LIBATION_TEST openvoiced: {newestVoiced.Title}, {NowPlaying!.Duration}, {newestVoiced.Chapters.Count} chapters");
				break;
			case "addvoice" when parts.Length > 2:
				// "addvoice:VOICE:ADDRESS": add a document as a voiced book in that voice, without opening it.
				{
					var addVoice = parts[1] + (parts[1] is "kokoro" or "espeak" ? ":" + parts[2] : "");
					var target = action[("addvoice:" + addVoice + ":").Length..];
					var path = target.StartsWith("http") ? await VoicedLibrary.DownloadAsync(new Uri(target), Path.GetTempPath(), default) : target;
					var content = DocumentText.Read(path);
					var choice = BookVoice.Platform!.Voices().First(v => v.Id == addVoice);
					var added = voiced.Add(content, target, content.Title ?? "Untitled", content.Author, choice);
					BookVoice.Platform.VoiceAhead(voiced.TextPath(added), choice.Id);
					Console.WriteLine($"LIBATION_TEST addvoice: '{added.Title}' in {choice.Label}, {added.Chapters.Count} chapters");
					RefreshDownloads();
				}
				break;
			case "voice" when parts.Length > 1:
				// "voice:/path/to/file.pdf" or "voice:https://…": add it with the first voice and open it, without playing.
				{
					var target = action["voice:".Length..];
					var path = target.StartsWith("http") ? await VoicedLibrary.DownloadAsync(new Uri(target), Path.GetTempPath(), default) : target;
					var content = DocumentText.Read(path);
					Console.WriteLine($"LIBATION_TEST voice: '{content.Title}' by {content.Author ?? "?"}, {content.Chapters.Count} chapters, {content.Words} words");
					var voices = BookVoice.Platform?.Voices() ?? [];
					Console.WriteLine($"LIBATION_TEST voice: {voices.Count} voices: {string.Join(", ", voices.Select(v => v.Label))}");
					var book = voiced.Add(content, target, content.Title ?? "Test", content.Author, voices[0]);
					if (await LoadVoicedAsync(book, showNowPlaying: true))
						Console.WriteLine($"LIBATION_TEST voice: opened, {NowPlaying!.Duration}");
				}
				break;
			case "checkin" when parts.Length > 2 && Library.Find(parts[1]) is { IsDownloaded: true } checkInBook:
				// "checkin:ID:start" or "checkin:ID:end": shows that question's card, without playing.
				await LoadAsync(checkInBook, showNowPlaying: true);
				NowPlaying?.ShowCheckInForTest(parts[2]);
				break;
			case "download" when parts.Length > 1 && Library.Find(parts[1]) is { IsNotDownloaded: true } downloadBook:
				// Starts a download and logs its progress, to check it carries on with the app in the background.
				downloadBook.PropertyChanged += (_, e) =>
				{
					if (e.PropertyName is nameof(BookItemViewModel.State) or nameof(BookItemViewModel.StatusText))
						Console.WriteLine($"LIBATION_TEST download {DateTime.Now:HH:mm:ss}: {downloadBook.State} {downloadBook.StatusText}");
				};
				_ = Library.DownloadCommand.ExecuteAsync(downloadBook);
				break;
			// "podcasts:term" searches and opens the first podcast; "podcastdl:term" also follows it, downloads its
			// newest episode and shows Downloads. Neither plays anything.
			case "podcasts" or "podcastdl" when parts.Length > 1:
				ShowTab("podcasts");
				Podcasts.SearchText = parts[1];
				await Podcasts.SearchCommand.ExecuteAsync(null);
				if (Podcasts.SearchResults.FirstOrDefault() is not { } found)
					break;
				await Podcasts.OpenCommand.ExecuteAsync(found);
				if (parts[0] == "podcasts")
					break;
				if (!found.IsSubscribed)
					await Podcasts.ToggleSubscriptionCommand.ExecuteAsync(found);
				if (Podcasts.Episodes.FirstOrDefault() is { } newest)
					await Podcasts.DownloadCommand.ExecuteAsync(newest);
				Podcasts.CloseShowCommand.Execute(null);
				Podcasts.SearchText = null;
				ShowTab("downloads");
				break;
			case "openepisode" when Podcasts.DownloadedRows().FirstOrDefault() is { } downloadedEpisode:
				await LoadEpisodeAsync(downloadedEpisode, showNowPlaying: true);
				break;
			case "clip" when parts.Length > 1 && Library.Find(parts[1]) is { IsDownloaded: true } clipBook:
				await LoadAsync(clipBook, showNowPlaying: true);
				NowPlaying?.AddClipCommand.Execute(null);
				break;
			case "pdf" when parts.Length > 1 && Library.Find(parts[1]) is { } pdfBook:
				await OpenBookPdf(pdfBook);
				break;
			case "blind" when parts.Length > 1 && Library.Find(parts[1]) is { IsDownloaded: true } blindBook:
				await LoadAsync(blindBook, showNowPlaying: true);
				NowPlaying?.SetListeningModeCommand.Execute("blind");
				break;
			case "log":
				ShowLog();
				break;
			case "settings":
				ShowSettings();
				if (parts.Length > 1)
					Settings.OpenPageCommand.Execute(parts[1]);
				break;
			// transcribe[:ASIN[:fresh]]: transcribe a downloaded book (the shortest, if none is named), without playing it.
			case "transcribe":
				{
					var downloaded = Library.AllDownloaded().Where(b => b.IsDownloaded).OrderBy(b => b.Book.Length).ToList();
					Console.WriteLine($"LIBATION_TEST transcribe: supported={TranscriptJobs.IsSupported}; downloaded: {string.Join(", ", downloaded.Select(b => $"{b.Book.Asin} {b.Title} ({b.Book.Length:h\\:mm})"))}");
					if ((parts.Length > 1 && parts[1].Length > 0 ? Library.Find(parts[1]) : downloaded.FirstOrDefault()) is not { IsDownloaded: true } target)
						break;
					if (parts.Length > 2 && parts[2] == "fresh")
						Transcripts.Transcripts.Delete(target.Book.Asin);
					var watch = System.Diagnostics.Stopwatch.StartNew();
					var startedAt = Transcripts.Done(target.Book);
					Transcripts.Start(target.Book);
					while (Transcripts.Asin is not null)
					{
						await Task.Delay(5000);
						var heard = (Transcripts.Fraction - startedAt) * target.Book.Length.TotalSeconds;
						Console.WriteLine($"LIBATION_TEST transcribe: {Transcripts.Fraction:P1} {Transcripts.Status} after {watch.Elapsed.TotalSeconds:0}s, {heard / watch.Elapsed.TotalSeconds:0.0}x real time");
					}
					var text = await Transcripts.ExportAsync(target.Book);
					Console.WriteLine($"LIBATION_TEST transcribe: done={Transcripts.Transcripts.IsDone(target.Book.Asin)} status={Transcripts.Status ?? "none"}, {text.Length} chars in {watch.Elapsed.TotalSeconds:0}s");
					Console.WriteLine(text[..Math.Min(text.Length, 1500)]);
				}
				break;
			case "details" when parts.Length > 1 && Library.Find(parts[1]) is { } detailsBook:
				ShowDetails(detailsBook);
				for (var i = 0; i < 60 && Details!.IsLoading; i++)
					await Task.Delay(500);
				Console.WriteLine($"LIBATION_TEST details: summary={Details!.Summary?.Length ?? 0} chars, message={Details.Message ?? "none"}");
				foreach (var section in Details.Series)
				{
					Console.WriteLine($"LIBATION_TEST details: series '{section.Name}', {section.CountText}");
					foreach (var row in section.Rows)
						Console.WriteLine($"LIBATION_TEST details:   {row.SequenceText} | {row.Title} | {row.StatusText}");
				}
				break;
			case "annotest" when parts.Length > 1 && Library.Find(parts[1]) is { IsDownloaded: true } book:
				await RunAnnotationTestAsync(book);
				break;
			case "play" when parts.Length > 1 && Library.Find(parts[1]) is { } item:
				await OpenBookCommand.ExecuteAsync(item);
				if (parts.Length > 2 && NowPlaying is not null && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var speed))
					NowPlaying.Speed = speed;
				break;
		}
	}

	/// <summary>
	/// End-to-end check of playback, of bookmarks and clips, and of reading and writing the Audible position,
	/// on one book. Leaves the book's Audible position as it found it and deletes what it created.
	/// </summary>
	private async Task RunAnnotationTestAsync(BookItemViewModel book)
	{
		static void Log(string message) => Console.WriteLine("LIBATION_TEST annotest: " + message);
		var asin = book.Book.Asin;
		var originalSpeed = settings.Speed;
		try
		{
			var before = await annotations.GetPositionAsync(asin);
			Log($"before: audible position={before?.Position.ToString() ?? "none"} updated={before?.Updated:u}");

			if (!await LoadAsync(book, showNowPlaying: true))
			{
				Log($"open failed: {Error}");
				return;
			}
			var np = NowPlaying!;
			Log($"opened: duration={np.Duration} chapters={np.Chapters.Count} startedAt={np.Position}");

			// Playback through this platform's audio backend, silent, sampled once a second.
			np.Volume = 0;
			np.Speed = 3;
			var testStart = TimeSpan.FromMinutes(10);
			np.Seek(testStart);
			np.PlayPause();
			for (var i = 1; i <= 5; i++)
			{
				await Task.Delay(1000);
				Log($"  t+{i}s at 3x: advanced {(np.Position - testStart).TotalSeconds:F1}s of book");
			}
			np.PlayPause();

			np.AddBookmarkCommand.Execute(null);
			Log($"add bookmark: status='{np.StatusMessage}' rows={np.Annotations.Count}");
			np.AddClipCommand.Execute(null);
			np.ClipEditor!.Title = "Test clip";
			np.ClipEditor.Note = "Made by the annotation test.";
			np.ClipEditor.SetLengthCommand.Execute("15");
			Log($"clip editor: {np.ClipEditor.StartText} to {np.ClipEditor.EndText}, {np.ClipEditor.RangeText}");
			np.SaveClipCommand.Execute(null);
			Log($"save clip: status='{np.StatusMessage}' rows={np.Annotations.Count}");
			foreach (var row in np.Annotations)
				Log($"  row: {row.Title} | {row.DetailText}");
			foreach (var row in np.Annotations.Where(r => r.Start >= testStart - TimeSpan.FromMinutes(1) && r.Start <= testStart + TimeSpan.FromMinutes(1)).ToList())
				await np.DeleteAnnotationCommand.ExecuteAsync(row);
			Log($"after delete: rows={np.Annotations.Count}");

			// Writing the position: send a marker, read it back, then put the original back.
			var marker = testStart + TimeSpan.FromSeconds(7);
			await annotations.SetPositionAsync(asin, marker);
			var during = await annotations.GetPositionAsync(asin);
			Log($"wrote {marker}, audible now says {during?.Position.ToString() ?? "none"}");
			if (before is not null)
			{
				await annotations.SetPositionAsync(asin, before.Position);
				np.Seek(before.Position);
			}
			var after = await annotations.GetPositionAsync(asin);
			Log($"after: audible position={after?.Position.ToString() ?? "none"}");
			np.Volume = 1;
			np.Speed = originalSpeed;
			Log("done");
		}
		catch (Exception ex)
		{
			Log($"FAILED: {ex}");
		}
	}
#endif
}
