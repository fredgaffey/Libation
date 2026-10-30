using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace LibationMobile.Services;

/// <summary>One stretch of listening, from pressing play to pausing.</summary>
/// <param name="From">Where in the book it started.</param>
/// <param name="To">Where in the book it stopped.</param>
/// <param name="BookSeconds">How much of the book was heard, at normal speed.</param>
/// <param name="SpentSeconds">How long the listening took.</param>
/// <param name="Marks">Where in the book the listener was every few minutes, to find the place again after dozing off.</param>
/// <param name="Syllables">Syllables heard, measured from the audio (AudioFilePlayer.SourceSyllablesPerSecond). Null before this was recorded.</param>
/// <param name="AsleepAt">When the listener probably fell asleep during it; null if not thought to have.</param>
/// <param name="AsleepPosition">Where in the book that was: the place to go back to.</param>
/// <param name="AsleepSource">How it was found: "auto-pause" (no touch and no movement for a while), or "health" (Apple Health's sleep, as from a watch).</param>
public record ListeningSession(
	string BookId,
	string Title,
	DateTimeOffset Started,
	DateTimeOffset Ended,
	TimeSpan From,
	TimeSpan To,
	double BookSeconds,
	double SpentSeconds,
	float Speed,
	IReadOnlyList<ListeningMark>? Marks = null,
	double? Syllables = null,
	DateTimeOffset? AsleepAt = null,
	TimeSpan? AsleepPosition = null,
	string? AsleepSource = null)
{
	/// <summary>Book time over real time: the speed across the whole session, which <see cref="Speed"/>, the speed at its end, may not be.</summary>
	[JsonIgnore]
	public double AverageSpeed => SpentSeconds > 0 && BookSeconds > 0 ? BookSeconds / SpentSeconds : Speed;

	/// <summary>Where in the book the listener was in the session, from the marks taken every few minutes.</summary>
	public TimeSpan PositionAt(DateTimeOffset at)
	{
		var points = new List<ListeningMark> { new(Started, From) };
		points.AddRange(Marks ?? []);
		points.Add(new(Ended, To));
		if (at <= Started)
			return From;
		for (var i = 1; i < points.Count; i++)
		{
			var (a, b) = (points[i - 1], points[i]);
			if (at > b.At)
				continue;
			var span = (b.At - a.At).TotalSeconds;
			var share = span > 0 ? (at - a.At).TotalSeconds / span : 0;
			return a.Position + (b.Position - a.Position) * share;
		}
		return To;
	}
}

/// <summary>Where in the book the listener was at a moment in a session.</summary>
public record ListeningMark(DateTimeOffset At, TimeSpan Position);

/// <summary>
/// Listening done somewhere else, as in the Audible app, seen when its place was picked up here: how far the book moved,
/// and between when. How long it took is not known, only that it was heard by <paramref name="At"/>.
/// </summary>
/// <param name="Since">When the book was last listened to here; the listening elsewhere came after.</param>
/// <param name="At">When the place was saved elsewhere.</param>
public record ElsewhereListening(string BookId, string Title, DateTimeOffset Since, DateTimeOffset At, TimeSpan From, TimeSpan To);

/// <summary>Every listening session on this device, newest last, saved as JSON in the app's data folder.</summary>
public class ListeningLog
{
	private readonly string path;
	private readonly string elsewherePath;
	private readonly Lock locker = new();
	private readonly List<ListeningSession> sessions;
	private readonly List<ElsewhereListening> elsewhere;

	public ListeningLog(string path)
	{
		this.path = path;
		elsewherePath = Path.Combine(Path.GetDirectoryName(path) ?? "", "listening-elsewhere.json");
		sessions = Load(path, ListeningLogJsonContext.Default.ListListeningSession);
		elsewhere = Load(elsewherePath, ListeningLogJsonContext.Default.ListElsewhereListening);
	}

	public IReadOnlyList<ListeningSession> Sessions
	{
		get { lock (locker) return sessions.ToList(); }
	}

	/// <summary>Listening in other apps, kept apart from <see cref="Sessions"/>, which are this device's own and are timed.</summary>
	public IReadOnlyList<ElsewhereListening> Elsewhere
	{
		get { lock (locker) return elsewhere.ToList(); }
	}

	public void AddElsewhere(ElsewhereListening listening)
	{
		lock (locker)
		{
			elsewhere.Add(listening);
			Save(elsewherePath, elsewhere, ListeningLogJsonContext.Default.ListElsewhereListening);
		}
	}

	/// <summary>Take back the latest for a book, as when the jump to the other device's place is undone.</summary>
	public void RemoveLastElsewhere(string bookId)
	{
		lock (locker)
		{
			var last = elsewhere.FindLastIndex(e => e.BookId == bookId);
			if (last < 0)
				return;
			elsewhere.RemoveAt(last);
			Save(elsewherePath, elsewhere, ListeningLogJsonContext.Default.ListElsewhereListening);
		}
	}

	private static void Save<T>(string file, List<T> items, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> info)
	{
		var temp = file + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(items, info));
		File.Move(temp, file, overwrite: true);
	}

	/// <summary>Add a session, or replace the one saved earlier for the same book and start, while it was still going.</summary>
	public void Add(ListeningSession session)
	{
		lock (locker)
		{
			var earlier = sessions.FindLastIndex(s => s.BookId == session.BookId && s.Started == session.Started);
			if (earlier >= 0)
				sessions[earlier] = session;
			else
				sessions.Add(session);
			Save(path, sessions, ListeningLogJsonContext.Default.ListListeningSession);
		}
	}

	/// <summary>
	/// Sessions with pauses of under <paramref name="gap"/> between them, in the same book, as one: before short pauses
	/// carried a session on, every pause began a new one, and a sitting was logged as a string of two-minute pieces.
	/// </summary>
	public static IReadOnlyList<ListeningSession> Joined(IEnumerable<ListeningSession> sessions, TimeSpan gap)
	{
		var joined = new List<ListeningSession>();
		foreach (var s in sessions.OrderBy(s => s.Started))
		{
			if (joined.Count > 0 && joined[^1] is var last && last.BookId == s.BookId && s.Started - last.Ended < gap && s.Started >= last.Started)
			{
				var marks = (last.Marks ?? []).Append(new ListeningMark(last.Ended, last.To)).Concat(s.Marks ?? []).ToList();
				joined[^1] = last with
				{
					Ended = s.Ended,
					To = s.To,
					BookSeconds = last.BookSeconds + s.BookSeconds,
					SpentSeconds = last.SpentSeconds + s.SpentSeconds,
					Speed = s.Speed,
					Marks = marks,
					Syllables = last.Syllables is null && s.Syllables is null ? null : (last.Syllables ?? 0) + (s.Syllables ?? 0),
					AsleepAt = last.AsleepAt ?? s.AsleepAt,
					AsleepPosition = last.AsleepPosition ?? s.AsleepPosition,
					AsleepSource = last.AsleepSource ?? s.AsleepSource,
				};
			}
			else
				joined.Add(s);
		}
		return joined;
	}

	public void Clear()
	{
		lock (locker)
		{
			sessions.Clear();
			elsewhere.Clear();
			File.Delete(path);
			File.Delete(elsewherePath);
		}
	}

	private static List<T> Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> info)
	{
		try
		{
			if (File.Exists(path))
				return JsonSerializer.Deserialize(File.ReadAllText(path), info) ?? [];
		}
		catch (JsonException)
		{
			// A damaged log is started again rather than stopping the app.
		}
		return [];
	}
}

[JsonSerializable(typeof(List<ListeningSession>))]
[JsonSerializable(typeof(List<ElsewhereListening>))]
internal partial class ListeningLogJsonContext : JsonSerializerContext
{
}
