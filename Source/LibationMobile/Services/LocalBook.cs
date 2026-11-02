using System;

namespace LibationMobile.Services;

/// <summary>A book ready to play: downloaded, or streamed.</summary>
/// <param name="Id">The book's ASIN, which keys its saved position.</param>
/// <param name="Chapters">Chapters known apart from the file, as for a voiced book; null to read the file's own.</param>
/// <param name="OpenSource">Where the sound comes from when it is not a file, as for text read aloud as it plays.</param>
public record LocalBook(string Id, string Path, string Title, string? Author, string? Narrator, TimeSpan Duration, byte[]? Cover,
	System.Collections.Generic.IReadOnlyList<Mpeg4Lib.Chapter>? Chapters = null, Func<AudioPlayer.IPcmSource>? OpenSource = null)
{
	/// <summary>Played from Audible over the network, not from a download: <see cref="Path"/> is where a download would go.</summary>
	public bool IsStreamed { get; init; }
}
