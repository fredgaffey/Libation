using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace LibationMobile.Services;

/// <summary>A bookmark (no <paramref name="End"/>) or clip saved on this device.</summary>
public record LocalAnnotation(Guid Id, TimeSpan Start, TimeSpan? End, string? Title, DateTimeOffset Created, string? Note = null);

/// <summary>
/// Bookmarks and clips kept on the device, per book. They work offline and for every book, which Audible's
/// annotation server does not (see <see cref="AudibleAnnotations"/>).
/// </summary>
public class LocalAnnotations
{
	private readonly string path;
	private readonly Lock locker = new();
	private readonly Dictionary<string, List<LocalAnnotation>> books;

	public LocalAnnotations(string path)
	{
		this.path = path;
		books = Load(path);
	}

	public IReadOnlyList<LocalAnnotation> Get(string bookId)
	{
		lock (locker)
			return books.TryGetValue(bookId, out var list) ? list.OrderBy(a => a.Start).ToList() : [];
	}

	public LocalAnnotation Add(string bookId, TimeSpan start, TimeSpan? end = null, string? title = null, string? note = null)
	{
		var annotation = new LocalAnnotation(Guid.NewGuid(), start, end, title, DateTimeOffset.UtcNow, note);
		lock (locker)
		{
			if (!books.TryGetValue(bookId, out var list))
				books[bookId] = list = [];
			list.Add(annotation);
			Save();
		}
		return annotation;
	}

	/// <summary>Give a bookmark or clip a new title and note; empty ones are cleared. Returns the changed one, or null if gone.</summary>
	public LocalAnnotation? Update(string bookId, Guid id, string? title, string? note)
	{
		lock (locker)
		{
			if (!books.TryGetValue(bookId, out var list) || list.FindIndex(a => a.Id == id) is var i && i < 0)
				return null;
			list[i] = list[i] with { Title = title, Note = note };
			Save();
			return list[i];
		}
	}

	public void Remove(string bookId, Guid id)
	{
		lock (locker)
		{
			if (books.TryGetValue(bookId, out var list) && list.RemoveAll(a => a.Id == id) > 0)
				Save();
		}
	}

	private static Dictionary<string, List<LocalAnnotation>> Load(string path)
	{
		try
		{
			if (File.Exists(path))
				return JsonSerializer.Deserialize(File.ReadAllText(path), LocalAnnotationsJsonContext.Default.DictionaryStringListLocalAnnotation) ?? new();
		}
		catch (JsonException)
		{
			// Keep the unreadable file for recovery rather than overwriting the listener's bookmarks.
			File.Move(path, path + ".corrupt", overwrite: true);
		}
		return new();
	}

	private void Save()
	{
		var temp = path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(books, LocalAnnotationsJsonContext.Default.DictionaryStringListLocalAnnotation));
		File.Move(temp, path, overwrite: true);
	}
}

[JsonSerializable(typeof(Dictionary<string, List<LocalAnnotation>>))]
internal partial class LocalAnnotationsJsonContext : JsonSerializerContext
{
}
