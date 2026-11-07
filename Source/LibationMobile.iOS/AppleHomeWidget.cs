using CoreFoundation;
using CoreGraphics;
using Foundation;
using LibationMobile.Services;
using Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using UIKit;

namespace LibationMobile.iOS;

/// <summary>
/// The home-screen widget (Widget/ in this project, built by Widget/build-widget.sh). The app writes what is
/// playing to the team's shared keychain group, which the widget reads; the widget writes its button presses
/// back and posts a Darwin notification. Item names and JSON fields match Widget/Shared/WidgetState.swift.
/// </summary>
public sealed class AppleHomeWidget : IHomeWidget
{
	private const string SERVICE = "io.github.freddygaffey.libation.widget";
	private const string COMMAND_POSTED = "io.github.freddygaffey.libation.widget.command";
	/// <summary>A play or pause older than this was not meant for now: the app was closed and has only just started.
	/// A speed is kept however old: it is what the widget shows.</summary>
	private static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(2);
	private const int COVER_SIZE = 300;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private sealed record State(string BookId, string Title, string? Author, double RemainingSeconds, double Speed, bool IsPlaying, double UpdatedAt,
		double DurationSeconds, string? ChapterTitle, double? ChapterRemainingSeconds, double? ChapterDurationSeconds, double SkipSeconds,
		bool SpeedHidden);
	private sealed record WidgetCommand(string Command, string? BookId, double? Speed, bool? Forward, double At);
	private sealed record RecentBook(string BookId, string Title);
	/// <summary>Books before the current one, for the large widget.</summary>
	private const int RECENT_COUNT = 2;
	private const int RECENT_COVER_SIZE = 120;

	private readonly AppleMediaSession mediaSession;
	private readonly ConcurrentDictionary<string, double> pendingSpeeds = new();
	private State? shown;
	/// <summary>What the widget was last redrawn from, and counts down from by itself.</summary>
	private State? drawn;
	/// <summary>
	/// Speed alone redraws the widget at most this often: following a target rate moves it every few seconds, and
	/// each redraw starts the widget's own process.
	/// </summary>
	private static readonly TimeSpan SpeedRedrawInterval = TimeSpan.FromMinutes(1);
	private string? coverBookId;

	public event Action<string, double>? SpeedRequested;
	public event Action<string, bool>? OpenRequested;
	private (string Id, bool Play)? pendingOpen;

	public AppleHomeWidget(AppleMediaSession mediaSession)
	{
		this.mediaSession = mediaSession;
		CFNotificationCenter.Darwin.AddObserver(COMMAND_POSTED, null, (_, _) => TakeCommand(), CFNotificationSuspensionBehavior.DeliverImmediately);
		// Pressed while the app was suspended or closed: picked up when it comes back.
		UIApplication.Notifications.ObserveDidBecomeActive((_, _) => TakeCommand());
		TakeCommand();
	}

	public void Show(WidgetInfo info)
	{
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
		var state = new State(info.BookId, info.Title, info.Author, info.Remaining.TotalSeconds, info.Speed, info.IsPlaying, now,
			info.Duration.TotalSeconds, info.ChapterTitle, info.ChapterRemaining?.TotalSeconds, info.ChapterDuration?.TotalSeconds, info.SkipSeconds,
			info.SpeedHidden);
		try
		{
			if (shown is { } previousBook && previousBook.BookId != info.BookId)
				AddRecent(previousBook);
			Write("state", JsonSerializer.SerializeToUtf8Bytes(state, Json));
			if (coverBookId != info.BookId)
			{
				coverBookId = info.BookId;
				if (SmallCover(info.Cover) is NSData cover)
					Write("cover", cover);
				else
					Console.WriteLine($"Widget: no cover for {info.BookId} ({info.Cover?.Length ?? 0} bytes given)");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Widget state not saved: {ex.Message}");
			return;
		}

		// The widget counts down by itself while playing; it only needs redrawing when that would go wrong.
		shown = state;
		var previous = drawn;
		if (previous is null || previous.BookId != state.BookId || previous.IsPlaying != state.IsPlaying || previous.ChapterTitle != state.ChapterTitle
			|| previous.SpeedHidden != state.SpeedHidden
			|| Math.Abs(previous.Speed - state.Speed) > 0.001 && now - previous.UpdatedAt >= SpeedRedrawInterval.TotalSeconds
			|| Math.Abs(Predicted(previous, now) - state.RemainingSeconds) > 60)
		{
			drawn = state;
			Reload();
		}
	}

	private static double Predicted(State state, double at)
		=> state.IsPlaying ? state.RemainingSeconds - (at - state.UpdatedAt) * state.Speed : state.RemainingSeconds;

	/// <summary>The book being replaced goes to the front of the recent list, with a small copy of its cover.</summary>
	private static void AddRecent(State book)
	{
		var recent = Read("recent") is byte[] saved ? JsonSerializer.Deserialize<List<RecentBook>>(saved, Json) ?? [] : [];
		recent.RemoveAll(r => r.BookId == book.BookId);
		recent.Insert(0, new RecentBook(book.BookId, book.Title));
		if (Read("cover") is byte[] cover && UIImage.LoadFromData(NSData.FromArray(cover)) is UIImage image)
			Write("cover-" + book.BookId, Resized(image, RECENT_COVER_SIZE));
		foreach (var dropped in recent.Skip(RECENT_COUNT + 1))
			Remove("cover-" + dropped.BookId);
		// One spare, in case the current book is among them when it comes back round.
		Write("recent", JsonSerializer.SerializeToUtf8Bytes(recent.Take(RECENT_COUNT + 1).ToList(), Json));
	}

	public (string Id, bool Play)? TakePendingOpen()
	{
		var open = pendingOpen;
		pendingOpen = null;
		return open;
	}

	public void OpenLink(Uri link)
	{
		Console.WriteLine($"Widget link: {link}");
		switch (link.Host)
		{
			case "play" when Uri.UnescapeDataString(link.AbsolutePath.Trim('/')) is { Length: > 0 } playId:
				Open(playId, play: true);
				break;
			case "play":
				mediaSession.RequestPlay();
				break;
			case "open" when Uri.UnescapeDataString(link.AbsolutePath.Trim('/')) is { Length: > 0 } bookId:
				Open(bookId, play: false);
				break;
		}
	}

	private void Open(string bookId, bool play)
	{
		if (OpenRequested is null)
			pendingOpen = (bookId, play);
		else
			OpenRequested.Invoke(bookId, play);
	}

	private sealed record ListedBook(string Id, string Title, string? Author);

	/// <summary>For Siri: "list my books", "play number 3", "play Dune". Read by Widget/Intents/SiriIntents.swift.</summary>
	public void ShowBooks(IReadOnlyList<PlayableBook> books)
	{
		try
		{
			var listed = books.Take(100).Select(b => new ListedBook(b.Id, b.Title, b.Author)).ToList();
			Write("books", JsonSerializer.SerializeToUtf8Bytes(listed, Json));
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Book list for Siri not saved: {ex.Message}");
		}
	}

	public double? TakePendingSpeed(string bookId) => pendingSpeeds.TryRemove(bookId, out var speed) ? speed : null;

	private void TakeCommand()
	{
		WidgetCommand? command;
		try
		{
			var data = Read("command");
			if (data is null)
				return;
			Remove("command");
			command = JsonSerializer.Deserialize<WidgetCommand>(data.AsSpan(), Json);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Widget command not read: {ex.Message}");
			return;
		}
		if (command is null)
			return;
		var stale = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds((long)(command.At * 1000)) > CommandLifetime;

		switch (command.Command)
		{
			case "play" when !stale:
				mediaSession.RequestPlay();
				break;
			case "pause" when !stale:
				mediaSession.RequestPause();
				break;
			case "skip" when !stale:
				mediaSession.RequestSkip(command.Forward == true);
				break;
			case "open" when !stale && command.BookId is string openId:
				Open(openId, play: false);
				break;
			case "speed" when command.BookId is string bookId && command.Speed is double speed:
				// The widget has already drawn this speed; redrawing it again would block its buttons a second time.
				if (drawn?.BookId == bookId)
					drawn = drawn with { Speed = speed };
				if (SpeedRequested is null)
					pendingSpeeds[bookId] = speed;
				else
					SpeedRequested.Invoke(bookId, speed);
				break;
		}
	}

	private static NSData? SmallCover(byte[]? cover)
	{
		if (cover is null || UIImage.LoadFromData(NSData.FromArray(cover)) is not UIImage image)
			return null;
		return Resized(image, COVER_SIZE);
	}

	private static NSData Resized(UIImage image, int size)
	{
		var renderer = new UIGraphicsImageRenderer(new CGSize(size, size), new UIGraphicsImageRendererFormat { Scale = 1 });
		var small = renderer.CreateImage(_ => image.Draw(new CGRect(0, 0, size, size)));
		return small.AsJPEG(0.8f)!;
	}

	// Saved without naming a keychain group, so in the first of the app's keychain-access-groups: the shared one.
	private static SecRecord Query(string account) => new(SecKind.GenericPassword) { Service = SERVICE, Account = account };

	private static byte[]? Read(string account)
		=> SecKeyChain.QueryAsData(Query(account), false, out var status) is NSData data && status == SecStatusCode.Success ? data.ToArray() : null;

	private static void Write(string account, byte[] value) => Write(account, NSData.FromArray(value));

	private static void Write(string account, NSData value)
	{
		// Replaced whole: an update cannot carry the item's class, which every SecRecord has.
		SecKeyChain.Remove(Query(account));
		var item = Query(account);
		item.ValueData = value;
		// The widget is drawn while the phone is locked.
		item.Accessible = SecAccessible.AfterFirstUnlock;
		var status = SecKeyChain.Add(item);
		if (status != SecStatusCode.Success)
			throw new InvalidOperationException($"keychain {status} writing {account}");
	}

	/// <summary>For the widget test action: what is saved, as the widget will read it.</summary>
	public override string ToString()
	{
		// Kept beside the app's files so a test can look at the image itself.
		if (Read("cover") is byte[] cover)
			System.IO.File.WriteAllBytes(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "widget-cover-test.jpg"), cover);
		return Describe();
	}

	private static string Describe()
		=> $"state: {(Read("state") is byte[] state ? System.Text.Encoding.UTF8.GetString(state) : "none")}; cover: {Read("cover")?.Length.ToString() ?? "none"} bytes";

	private static void Remove(string account) => SecKeyChain.Remove(Query(account));

	[DllImport("@rpath/LibationWidgetBridge.framework/LibationWidgetBridge", EntryPoint = "libation_widget_reload")]
	private static extern void ReloadWidgets();

	private static void Reload()
	{
		try
		{
			ReloadWidgets();
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			// Built without the widget (Widget/build-widget.sh not run): nothing to redraw.
		}
	}
}
