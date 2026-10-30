using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LibationMobile.ViewModels;
using System;
using System.IO;

namespace LibationMobile.Views;

public partial class ListeningLogView : UserControl
{
	public ListeningLogView()
	{
		InitializeComponent();
#if DEBUG
		// Simulator runs can look further down the page: LIBATION_TEST_SCROLL=900 scrolls that far once it is shown.
		if (double.TryParse(Environment.GetEnvironmentVariable("LIBATION_TEST_SCROLL"), out var offset))
			Scroller.LayoutUpdated += (_, _) =>
			{
				if (Scroller.Extent.Height > offset && Scroller.Offset.Y == 0)
					Scroller.Offset = new Avalonia.Vector(0, offset);
			};
#endif
	}

	private async void CopyResearch_Click(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
			return;
		await clipboard.SetTextAsync(vm.Log.ResearchJson());
		vm.Log.AudibleStatus = "Research data copied as JSON. Paste it into an AI or a spreadsheet tool.";
	}

	private async void SaveResearch_Click(object? sender, RoutedEventArgs e)
	{
		if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
			return;
		try
		{
			var json = vm.Log.ResearchJson();
			var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				Title = "Save research data",
				SuggestedFileName = ListeningLogViewModel.ResearchFileName,
				DefaultExtension = "json",
				FileTypeChoices = [new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }]
			});
			if (file is null)
				return;
			await using var stream = await file.OpenWriteAsync();
			await using var writer = new StreamWriter(stream);
			await writer.WriteAsync(json);
			vm.Log.AudibleStatus = $"Research data saved to {file.Name}.";
		}
		catch (Exception ex)
		{
			vm.Log.AudibleStatus = $"Could not save the research data: {ex.Message}";
		}
	}
}
