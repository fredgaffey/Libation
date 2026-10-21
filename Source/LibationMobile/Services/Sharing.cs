using System.IO;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>Hands a file to the platform's share sheet: AirDrop, Save to Files, another app.</summary>
public interface IFileSharer
{
	/// <param name="name">What the file is called where it goes, such as "Diaspora.m4b".</param>
	Task ShareAsync(string path, string name);
}

public static class Sharing
{
	/// <summary>Set by the platform head. Null where there is no share sheet; the view saves through a file picker instead.</summary>
	public static IFileSharer? Platform { get; set; }

	/// <summary>A file name from a book's title.</summary>
	public static string FileName(string title, string extension)
	{
		var name = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
		return (name.Length > 0 ? name : "Book") + extension;
	}
}
