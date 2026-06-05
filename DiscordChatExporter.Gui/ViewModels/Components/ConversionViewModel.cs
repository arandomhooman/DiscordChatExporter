using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public sealed partial class ConversionViewModel(
    DialogManager dialogManager,
    LocalizationManager localizationManager
) : ViewModelBase
{
    public LocalizationManager LocalizationManager { get; } = localizationManager;

    public event EventHandler? BackRequested;

    public ObservableCollection<string> SourceFilePaths { get; } = [];

    public ObservableCollection<ConversionResultRow> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial string? OutputFolderPath { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsHtmlDarkSelected { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsHtmlLightSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsCsvSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsTxtSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsSqliteSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task PickFilesAsync()
    {
        var paths = await dialogManager.PromptMultipleFilePathsAsync([
            new FilePickerFileType("JSON exports") { Patterns = ["*.json"] },
        ]);

        foreach (
            var path in paths.Where(p =>
                !SourceFilePaths.Contains(p, StringComparer.OrdinalIgnoreCase)
            )
        )
            SourceFilePaths.Add(path);

        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PickOutputFolderAsync()
    {
        OutputFolderPath = await dialogManager.PromptDirectoryPathAsync(OutputFolderPath ?? "");
    }

    private bool CanConvert() =>
        !IsBusy
        && SourceFilePaths.Count > 0
        && !string.IsNullOrWhiteSpace(OutputFolderPath)
        && GetTargetFormats().Any();

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputFolderPath))
            return;

        IsBusy = true;
        Results.Clear();
        try
        {
            foreach (var sourcePath in SourceFilePaths)
            {
                var hasConversionData =
                    (await JsonExportReader.ParseAsync(sourcePath)).ConversionData is not null;
                foreach (var format in GetTargetFormats())
                {
                    var outputPath = Path.Combine(
                        OutputFolderPath,
                        Path.GetFileNameWithoutExtension(sourcePath)
                            + "."
                            + format.GetFileExtension()
                    );

                    try
                    {
                        await ExportConverter.ConvertAsync(sourcePath, outputPath, format);
                        Results.Add(
                            new ConversionResultRow(
                                sourcePath,
                                outputPath,
                                format,
                                hasConversionData,
                                true,
                                "Converted"
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        Results.Add(
                            new ConversionResultRow(
                                sourcePath,
                                outputPath,
                                format,
                                hasConversionData,
                                false,
                                ex.Message
                            )
                        );
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    private IEnumerable<ExportFormat> GetTargetFormats()
    {
        if (IsHtmlDarkSelected)
            yield return ExportFormat.HtmlDark;
        if (IsHtmlLightSelected)
            yield return ExportFormat.HtmlLight;
        if (IsCsvSelected)
            yield return ExportFormat.Csv;
        if (IsTxtSelected)
            yield return ExportFormat.PlainText;
        if (IsSqliteSelected)
            yield return ExportFormat.Db;
    }
}

public sealed record ConversionResultRow(
    string SourceFilePath,
    string OutputFilePath,
    ExportFormat Format,
    bool HasConversionData,
    bool IsSuccess,
    string Message
);
