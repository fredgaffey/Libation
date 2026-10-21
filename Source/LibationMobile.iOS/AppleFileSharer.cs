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

	public Task ShareAsync(string path, string name)
	{
		// What was shared before has gone by now.
		try
		{
			if (Directory.Exists(Folder))
				Directory.Delete(Folder, recursive: true);
		}
		catch (IOException)
		{
		}
		var named = Path.Combine(Directory.CreateDirectory(Path.Combine(Folder, Guid.NewGuid().ToString("N"))).FullName, name);
		if (link(path, named) != 0)
			File.Copy(path, named);

		var done = new TaskCompletionSource();
		UIApplication.SharedApplication.InvokeOnMainThread(() =>
		{
			var sheet = new UIActivityViewController([NSUrl.FromFilename(named)], null)
			{
				CompletionWithItemsHandler = (_, _, _, _) => done.TrySetResult(),
			};
			if (Top() is not { } top)
			{
				done.TrySetResult();
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

	private static UIViewController? Top()
	{
		var root = UIApplication.SharedApplication.ConnectedScenes.ToArray()
			.OfType<UIWindowScene>()
			.SelectMany(s => s.Windows)
			.FirstOrDefault(w => w.IsKeyWindow)?.RootViewController;
		while (root?.PresentedViewController is { } presented)
			root = presented;
		return root;
	}
}
