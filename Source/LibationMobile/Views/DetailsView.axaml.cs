using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LibationMobile.Services;
using LibationMobile.ViewModels;
using System;
using System.IO;

namespace LibationMobile.Views;

public partial class DetailsView : UserControl
{
	public DetailsView() => InitializeComponent();

	/// <summary>Start, stop or carry on a transcript, or save it once it is finished.</summary>
	private async void Transcript_Click(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not MainViewModel { Details: { } details } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
			return;
		if (!details.TranscriptDone)
		{
			details.ToggleTranscript();
			return;
		}
		try
		{
			var text = await details.Transcripts.ExportAsync(details.Item.Book);
			var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				Title = "Save transcript",
				SuggestedFileName = BookTranscripts.FileName(details.Title),
				DefaultExtension = "txt",
				FileTypeChoices = [new("Text") { Patterns = ["*.txt"], MimeTypes = ["text/plain"] }]
			});
			if (file is null)
				return;
			await using var stream = await file.OpenWriteAsync();
			await using var writer = new StreamWriter(stream);
			await writer.WriteAsync(text);
			details.Message = $"Transcript saved to {file.Name}.";
		}
		catch (Exception ex)
		{
			details.Message = $"Could not save the transcript: {ex.Message}";
		}
	}
}
