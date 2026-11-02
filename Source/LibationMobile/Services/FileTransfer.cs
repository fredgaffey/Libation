using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LibationMobile.Services;

/// <summary>
/// Fetches a large file in a way that carries on while the app is in the background, where the platform allows.
/// </summary>
public interface IFileTransfer
{
	/// <summary>Download <paramref name="url"/> to <paramref name="path"/>, or pick up a download of it already running.</summary>
	/// <param name="progress">Reports 0 to 1.</param>
	Task DownloadAsync(Uri url, string path, string userAgent, Action<double> progress, CancellationToken token);
}

/// <summary>The platform's downloader received nothing for too long while the app was on screen.</summary>
public sealed class DownloadStalledException() : IOException("The system downloader stalled.");

/// <summary>Asks the platform for time to finish work after the app leaves the screen.</summary>
public interface IBackgroundWork
{
	/// <summary>Keep running until the result is disposed, or until the platform will allow no longer.</summary>
	IDisposable Begin(string name);
}

public static class FileTransfer
{
	/// <summary>Set by the platform head. Null downloads in the app's own process, which stops when it is suspended.</summary>
	public static IFileTransfer? Platform { get; set; }

	/// <summary>Set by the platform head. Null where the app keeps running anyway.</summary>
	public static IBackgroundWork? BackgroundWork { get; set; }

	public static IDisposable BeginBackgroundWork(string name) => BackgroundWork?.Begin(name) ?? NoWork.Instance;

	private sealed class NoWork : IDisposable
	{
		public static readonly NoWork Instance = new();
		public void Dispose() { }
	}
}
