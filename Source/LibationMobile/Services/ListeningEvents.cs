using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace LibationMobile.Services;

/// <summary>
/// Something that happened while listening, for the listener's own research. Kinds and what Value and Detail hold:
/// <list type="bullet">
/// <item>"play", "pause": Value is how long the pause before a play lasted, in seconds</item>
/// <item>"speed": Value the new speed; Detail who changed it ("you", "plan", "siri", "widget", "profile", "trial")</item>
/// <item>"skip": Value the seconds moved, negative back; a skip back soon after fast listening suggests something was missed</item>
/// <item>"seek": Value the seconds moved; Detail what moved it ("scrubber", "chapter", "bookmark", "history", "sync")</item>
/// <item>"route": the sound moved, as to AirPods; Detail where</item>
/// <item>"chapter": Detail the chapter's title</item>
/// <item>"mode", "profile", "plan": Detail the new one</item>
/// </list>
/// </summary>
/// <param name="Position">Seconds into the book.</param>
/// <param name="Route">Where the sound went: "headphones", "bluetooth", "speaker", "car", "airplay".</param>
/// <param name="Battery">The phone's battery, 0 to 100, when it happened: the drain over an hour of listening follows.</param>
/// <param name="Charging">Whether the phone was charging then, so those stretches can be left out of the drain.</param>
public record ListeningEvent(DateTimeOffset At, string BookId, string Kind, double Position, double Speed, double? Value = null,
	string? Detail = null, string? Route = null, bool Blind = false, int? Battery = null, bool? Charging = null);

/// <summary>
/// The event log: one JSON line per event appended to events.jsonl, so writing never rewrites what came before. A heavy
/// listening day is a few dozen short lines, and costs no measurable battery. Position and speed at any moment follow
/// from the events; the syllable rate at any position can be measured from the book's audio.
/// </summary>
public class ListeningEvents(string dataDirectory)
{
	private readonly string path = Path.Combine(dataDirectory, "events.jsonl");
	private readonly Lock locker = new();
	/// <summary>UTF-8 without the byte-order mark Encoding.UTF8 writes at a new file's start, which made its first line unreadable.</summary>
	private static readonly Encoding NoMark = new UTF8Encoding(false);

	public void Add(ListeningEvent e)
	{
		try
		{
			var line = JsonSerializer.Serialize(e, ListeningEventsJsonContext.Default.ListeningEvent) + "\n";
			lock (locker)
				File.AppendAllText(path, line, NoMark);
		}
		catch (IOException ex)
		{
			Console.WriteLine($"Listening event not saved: {ex.Message}");
		}
	}

	/// <summary>Every event, oldest first. Lines that cannot be read, such as one cut short by the app closing, are skipped.</summary>
	public IReadOnlyList<ListeningEvent> All()
	{
		lock (locker)
		{
			if (!File.Exists(path))
				return [];
			var events = new List<ListeningEvent>();
			foreach (var line in File.ReadLines(path))
			{
				try
				{
					if (JsonSerializer.Deserialize(line.TrimStart('\uFEFF'), ListeningEventsJsonContext.Default.ListeningEvent) is { } e)
						events.Add(e);
				}
				catch (JsonException)
				{
				}
			}
			return events;
		}
	}

	public int Count
	{
		get
		{
			lock (locker)
				return File.Exists(path) ? File.ReadLines(path).Count() : 0;
		}
	}
}

[JsonSerializable(typeof(ListeningEvent))]
internal partial class ListeningEventsJsonContext : JsonSerializerContext
{
}
