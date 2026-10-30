using Foundation;
using LibationMobile.Services;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UIKit;

namespace LibationMobile.iOS;

/// <summary>
/// iOS's share sheet. The file goes under the name given, through a hard link in the temporary folder, so a book of
/// hundreds of megabytes is not copied first.
/// </summary>
public sealed class AppleFileSharer : IFileSharer
{
	private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Shared");

	[DllImport("libc", SetLastError = true)]
	private static extern int link(string existing, string created);

	/// <summary>
	/// The file under its new name, in a folder of its own. Earlier shares are cleared only once a day old: AirDrop can
	/// still be sending a large book after the sheet has closed, and deleting it under AirDrop cut the transfer off.
	/// </summary>
	private static string Named(string path, string name)
	{
		try
		{
			if (Directory.Exists(Folder))
				foreach (var old in Directory.EnumerateDirectories(Folder))
					if (DateTime.UtcNow - Directory.GetCreationTimeUtc(old) > TimeSpan.FromDays(1))
						Directory.Delete(old, recursive: true);
		}
		catch (IOException)
		{
		}
		var named = Path.Combine(Directory.CreateDirectory(Path.Combine(Folder, Guid.NewGuid().ToString("N"))).FullName, name);
		if (link(path, named) != 0)
			File.Copy(path, named);
		return named;
	}

	/// <summary>
	/// Files' own "save to" picker: iCloud Drive, OneDrive, a USB drive, or on the phone. Unlike AirDrop it needs no
	/// other Apple device, so a book can reach a Windows laptop through OneDrive.
	/// </summary>
	public Task SaveAsync(string path, string name)
	{
		var named = Named(path, name);
		var done = new TaskCompletionSource();
		UIApplication.SharedApplication.InvokeOnMainThread(() =>
		{
			if (Top() is not { } top)
			{
				done.TrySetException(new InvalidOperationException("there is no window to show it from"));
				return;
			}
			var picker = new UIDocumentPickerViewController([NSUrl.FromFilename(named)], asCopy: true);
			picker.DidPickDocumentAtUrls += (_, _) => done.TrySetResult();
			picker.WasCancelled += (_, _) => done.TrySetResult();
			top.PresentViewController(picker, true, null);
		});
		return done.Task;
	}

	public Task ShareAsync(string path, string name)
	{
		var named = Named(path, name);
		var done = new TaskCompletionSource();
		UIApplication.SharedApplication.InvokeOnMainThread(() =>
		{
			var sheet = new UIActivityViewController([NSUrl.FromFilename(named)], null)
			{
				CompletionWithItemsHandler = (_, _, _, _) => done.TrySetResult(),
			};
			if (Top() is not { } top)
			{
				done.TrySetException(new InvalidOperationException("there is no window to show it from"));
				return;
			}
			// An iPad shows it as a popover, which needs somewhere to point.
			if (sheet.PopoverPresentationController is { } popover)
			{
				popover.SourceView = top.View;
				popover.SourceRect = new CoreGraphics.CGRect(top.View!.Bounds.Width / 2, top.View.Bounds.Height / 2, 0, 0);
			}
			top.PresentViewController(sheet, true, null);
		});
		return done.Task;
	}

	/// <summary>
	/// The view controller on top, to present from. The key window first; failing that any visible one, as the app's own
	/// window is not always key (just after launch, or with the keyboard or a system sheet up), and finding none meant
	/// the share sheet silently never appeared.
	/// </summary>
	private static UIViewController? Top()
	{
		var windows = UIApplication.SharedApplication.ConnectedScenes.ToArray()
			.OfType<UIWindowScene>()
			.OrderBy(s => s.ActivationState == UISceneActivationState.ForegroundActive ? 0 : 1)
			.SelectMany(s => s.Windows)
			.ToList();
		var root = (windows.FirstOrDefault(w => w.IsKeyWindow) ?? windows.FirstOrDefault(w => !w.Hidden && w.RootViewController is not null))?.RootViewController;
		while (root?.PresentedViewController is { } presented)
			root = presented;
		return root;
	}
}
