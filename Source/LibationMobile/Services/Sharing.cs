using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>Hands a file to the platform's share sheet: AirDrop, Save to Files, another app.</summary>
public interface IFileSharer
{
	/// <param name="name">What the file is called where it goes, such as "Diaspora.m4b".</param>
	Task ShareAsync(string path, string name);

	/// <summary>Save a copy through the system's file picker, to a cloud drive, a USB drive or the device.</summary>
	Task SaveAsync(string path, string name);
}

public static class Sharing
{
	/// <summary>Set by the platform head. Null where there is no share sheet; the view saves through a file picker instead.</summary>
	public static IFileSharer? Platform { get; set; }

	/// <summary>A file name from a book's title.</summary>
	/// <summary>Characters Windows, and so OneDrive and most USB drives, will not take in a name, whatever this device allows.</summary>
	private static readonly char[] Unportable = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

	/// <summary>
	/// A file name from a book's title that any computer will take: "Hitchhiker's Guide to the Galaxy: Book 1" becomes
	/// "Hitchhiker's Guide to the Galaxy - Book 1", and very long titles are shortened.
	/// </summary>
	public static string FileName(string title, string extension)
	{
		var name = string.Concat(title.Replace(": ", " - ").Split(Unportable.Concat(Path.GetInvalidFileNameChars()).ToArray()))
			.Where(c => !char.IsControl(c)).Aggregate(new System.Text.StringBuilder(), (b, c) => b.Append(c)).ToString().Trim().TrimEnd('.');
		if (name.Length > 100)
			name = name[..100].TrimEnd();
		return (name.Length > 0 ? name : "Book") + extension;
	}
}
