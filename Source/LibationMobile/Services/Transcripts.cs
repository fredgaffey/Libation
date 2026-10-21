using Mpeg4Lib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>A stretch of speech, with where it is in the book, in seconds.</summary>
public record TranscriptLine(double Start, double End, string Text);

/// <summary>Speech to text on the device.</summary>
public interface ITranscriber
{
	/// <summary>This device can transcribe.</summary>
	bool IsAvailable { get; }

	/// <summary>Download the speech model for <paramref name="language"/> unless it is on the device.</summary>
	/// <param name="progress">Reports 0 to 1.</param>
	Task EnsureModelAsync(string language, Action<double> progress, CancellationToken token);

	/// <summary>Transcribe the audio file from <paramref name="from"/> seconds to its end.</summary>
	/// <param name="line">Each finished stretch of speech, in order.</param>
	/// <param name="progress">How far into the file it has got, in seconds.</param>
	Task TranscribeAsync(string path, double from, string language, Action<TranscriptLine> line, Action<double> progress, CancellationToken token);
}

/// <summary>Lets a long transcription carry on while the app is in the background, where the platform allows.</summary>
public interface ITranscriptionWork
{
	/// <summary>Ask to keep running. <paramref name="cancel"/> is called if the listener or the system stops it.</summary>
	/// <returns>Takes progress (0 to 1) as it is made; disposed, with success or not, when the work ends.</returns>
	ITranscriptionWorkHandle Begin(string title, Action cancel);
}

public interface ITranscriptionWorkHandle : IDisposable
{
	void Report(double fraction, string subtitle);
	void Finish(bool success);
}

public static class Transcriber
{
	/// <summary>Set by the platform head. Null where there is no speech-to-text.</summary>
	public static ITranscriber? Platform { get; set; }

	/// <summary>Set by the platform head. Null: the work runs only while the app does.</summary>
	public static ITranscriptionWork? Work { get; set; }
}

/// <summary>
/// Books' transcripts, kept in Transcripts/ by ASIN as tab-separated lines (start, end, text) added as they are heard,
/// so one stopped part way picks up where it left off. Exported as plain text with chapter headings and times.
/// </summary>
public class BookTranscripts(string dataDirectory)
{
	/// <summary>A pause at least this long, in seconds, starts a new paragraph.</summary>
	private const double PARAGRAPH_PAUSE = 1.2;
	/// <summary>Paragraphs are broken after this many seconds of speech even without a pause.</summary>
	private const double PARAGRAPH_LENGTH = 90;

	private readonly string directory = Path.Combine(dataDirectory, "Transcripts");

	private string LinesPath(string asin) => Path.Combine(directory, asin + ".tsv");
	private string DonePath(string asin) => Path.Combine(directory, asin + ".done");

	public bool IsDone(string asin) => File.Exists(DonePath(asin));

	public bool HasAny(string asin) => File.Exists(LinesPath(asin));

	/// <summary>Where a transcript stopped part way: the end of its last line, in seconds.</summary>
	public double ResumePoint(string asin) => Read(asin).LastOrDefault()?.End ?? 0;

	public void Delete(string asin)
	{
		File.Delete(LinesPath(asin));
		File.Delete(DonePath(asin));
	}

	/// <summary>Opens the transcript to add lines to, as they are heard.</summary>
	public StreamWriter Append(string asin)
	{
		Directory.CreateDirectory(directory);
		File.Delete(DonePath(asin));
		return new StreamWriter(LinesPath(asin), append: true, Encoding.UTF8) { AutoFlush = true };
	}

	public static void Write(StreamWriter writer, TranscriptLine line)
	{
		var text = line.Text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
		writer.WriteLine(FormattableString.Invariant($"{line.Start:0.00}\t{line.End:0.00}\t{text}"));
	}

	public void MarkDone(string asin) => File.WriteAllText(DonePath(asin), DateTimeOffset.Now.ToString("O"));

	public IReadOnlyList<TranscriptLine> Read(string asin)
	{
		if (!File.Exists(LinesPath(asin)))
			return [];
		var lines = new List<TranscriptLine>();
		foreach (var row in File.ReadLines(LinesPath(asin)))
		{
			var parts = row.Split('\t', 3);
			// A line cut short when the app was stopped mid-write.
			if (parts.Length == 3
				&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
				&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
				lines.Add(new TranscriptLine(start, end, parts[2]));
		}
		return lines;
	}

	public static string FileName(string title)
	{
		var name = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
		return (name.Length > 0 ? name : "Transcript") + ".txt";
	}

	/// <summary>The transcript as text: the book's title, then its chapters' headings and paragraphs, each with its time.</summary>
	public string ToText(string asin, string title, string? author, string? narrator, IReadOnlyList<Chapter> chapters)
	{
		var text = new StringBuilder();
		text.AppendLine(title);
		if (!string.IsNullOrWhiteSpace(author))
			text.AppendLine($"by {author}");
		if (!string.IsNullOrWhiteSpace(narrator))
			text.AppendLine($"Read by {narrator}");
		text.AppendLine();

		var chapter = 0;
		var paragraph = new StringBuilder();
		double paragraphStart = 0, lastEnd = 0;

		void EndParagraph()
		{
			if (paragraph.Length == 0)
				return;
			text.Append('[').Append(Time(paragraphStart)).Append("] ").AppendLine(paragraph.ToString()).AppendLine();
			paragraph.Clear();
		}

		foreach (var line in Read(asin))
		{
			var headed = false;
			while (chapter < chapters.Count && chapters[chapter].StartOffset.TotalSeconds <= line.Start + 0.5)
			{
				EndParagraph();
				text.AppendLine(chapters[chapter].Title.Trim()).AppendLine();
				chapter++;
				headed = true;
			}
			if (!headed && paragraph.Length > 0 && (line.Start - lastEnd >= PARAGRAPH_PAUSE || line.Start - paragraphStart >= PARAGRAPH_LENGTH))
				EndParagraph();
			if (paragraph.Length == 0)
				paragraphStart = line.Start;
			else
				paragraph.Append(' ');
			paragraph.Append(line.Text);
			lastEnd = line.End;
		}
		EndParagraph();
		return text.ToString();
	}

	private static string Time(double seconds)
	{
		var t = TimeSpan.FromSeconds(seconds);
		return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
	}
}
