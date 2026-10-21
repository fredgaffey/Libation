using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LibationMobile.Services;
using LibationMobile.ViewModels;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LibationMobile.Views;

public partial class DetailsView : UserControl
{
	public DetailsView() => InitializeComponent();

	private async void ShareAudio_Click(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not MainViewModel { Details: { } details })
			return;
		await ShareAsync(details, details.AudioPath, Sharing.FileName(details.Title, Path.GetExtension(details.AudioPath)), "Audio file", "audio/mp4");
	}

	/// <summary>Start, stop or carry on a transcript, or share it once it is finished.</summary>
	private async void Transcript_Click(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not MainViewModel { Details: { } details })
			return;
		if (!details.TranscriptDone)
		{
			details.ToggleTranscript();
			return;
		}
		try
		{
			var text = await details.Transcripts.ExportAsync(details.Item.Book);
			var path = Path.Combine(Path.GetTempPath(), "transcript.txt");
			await File.WriteAllTextAsync(path, text);
			await ShareAsync(details, path, Sharing.FileName(details.Title, ".txt"), "Text", "text/plain");
		}
		catch (Exception ex)
		{
			details.Message = $"Could not share the transcript: {ex.Message}";
		}
	}

	/// <summary>The share sheet where there is one; otherwise a copy saved where the listener picks.</summary>
	private async Task ShareAsync(BookDetailsViewModel details, string path, string name, string kind, string mime)
	{
		try
		{
			if (Sharing.Platform is { } sharer)
			{
				await sharer.ShareAsync(path, name);
				return;
			}
			if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
				return;
			var extension = Path.GetExtension(name);
			var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				SuggestedFileName = name,
				DefaultExtension = extension.TrimStart('.'),
				FileTypeChoices = [new(kind) { Patterns = ["*" + extension], MimeTypes = [mime] }]
			});
			if (file is null)
				return;
			await using var source = File.OpenRead(path);
			await using var target = await file.OpenWriteAsync();
			await source.CopyToAsync(target);
			details.Message = $"Saved to {file.Name}.";
		}
		catch (Exception ex)
		{
			details.Message = $"Could not share {name}: {ex.Message}";
		}
	}
}
