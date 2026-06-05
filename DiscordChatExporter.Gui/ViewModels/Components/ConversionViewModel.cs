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
    LocalizationManager localizationManager,
    Func<FilePickerFileType[], Task<IReadOnlyList<string>>>? promptMultipleFilePathsAsync = null,
    Func<string, Task<string?>>? promptDirectoryPathAsync = null
) : ViewModelBase
{
    private readonly Func<
        FilePickerFileType[],
        Task<IReadOnlyList<string>>
    > _promptMultipleFilePathsAsync =
        promptMultipleFilePathsAsync
        ?? (fileTypes => dialogManager.PromptMultipleFilePathsAsync(fileTypes));
    private readonly Func<string, Task<string?>> _promptDirectoryPathAsync =
        promptDirectoryPathAsync
        ?? (defaultDirPath => dialogManager.PromptDirectoryPathAsync(defaultDirPath));

    private const string ConvertedMessage = "Converted";
    private const string OutputPathConflictMessage = "Output path conflict";

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
    [NotifyCanExecuteChangedFor(nameof(PickFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickOutputFolderCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditConversionState))]
    public partial bool IsBusy { get; set; }

    public bool CanEditConversionState => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEditConversionState))]
    private async Task PickFilesAsync()
    {
        if (IsBusy)
            return;

        var paths = await _promptMultipleFilePathsAsync([
            new FilePickerFileType("JSON exports") { Patterns = ["*.json"] },
        ]);

        if (IsBusy)
            return;

        foreach (
            var path in paths.Where(p =>
                !SourceFilePaths.Contains(p, StringComparer.OrdinalIgnoreCase)
            )
        )
            SourceFilePaths.Add(path);

        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditConversionState))]
    private async Task PickOutputFolderAsync()
    {
        if (IsBusy)
            return;

        var outputFolderPath = await _promptDirectoryPathAsync(OutputFolderPath ?? "");
        if (IsBusy || outputFolderPath is null)
            return;

        OutputFolderPath = outputFolderPath;
    }

    private bool CanConvert() =>
        !IsBusy
        && SourceFilePaths.Count > 0
        && !string.IsNullOrWhiteSpace(OutputFolderPath)
        && GetTargetFormats().Any();

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        if (IsBusy)
            return;

        var outputFolderPath = OutputFolderPath;
        var sourceFilePaths = SourceFilePaths.ToArray();
        var targetFormats = GetTargetFormats().ToArray();

        if (
            string.IsNullOrWhiteSpace(outputFolderPath)
            || sourceFilePaths.Length <= 0
            || targetFormats.Length <= 0
        )
            return;

        IsBusy = true;
        Results.Clear();
        try
        {
            var conversionJobs = CreateConversionJobs(
                sourceFilePaths,
                targetFormats,
                outputFolderPath
            );
            var conflictingOutputPaths = conversionJobs
                .GroupBy(j => j.OutputFilePath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceJobs in conversionJobs.GroupBy(j => j.SourceIndex))
            {
                var sourcePath = sourceJobs.First().SourceFilePath;
                var conflictJobs = sourceJobs
                    .Where(j => conflictingOutputPaths.Contains(j.OutputFilePath))
                    .ToArray();
                var remainingJobs = sourceJobs
                    .Where(j => !conflictingOutputPaths.Contains(j.OutputFilePath))
                    .ToArray();

                foreach (var job in conflictJobs)
                {
                    Results.Add(
                        new ConversionResultRow(
                            job.SourceFilePath,
                            job.OutputFilePath,
                            job.Format,
                            false,
                            false,
                            OutputPathConflictMessage
                        )
                    );
                }

                if (remainingJobs.Length <= 0)
                    continue;

                bool hasConversionData;
                try
                {
                    hasConversionData =
                        (await JsonExportReader.ParseAsync(sourcePath)).ConversionData is not null;
                }
                catch (Exception ex)
                {
                    foreach (var job in remainingJobs)
                    {
                        Results.Add(
                            new ConversionResultRow(
                                job.SourceFilePath,
                                job.OutputFilePath,
                                job.Format,
                                false,
                                false,
                                ex.Message
                            )
                        );
                    }

                    continue;
                }

                foreach (var job in remainingJobs)
                {
                    try
                    {
                        await ExportConverter.ConvertAsync(
                            job.SourceFilePath,
                            job.OutputFilePath,
                            job.Format
                        );
                        Results.Add(
                            new ConversionResultRow(
                                job.SourceFilePath,
                                job.OutputFilePath,
                                job.Format,
                                hasConversionData,
                                true,
                                ConvertedMessage
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        Results.Add(
                            new ConversionResultRow(
                                job.SourceFilePath,
                                job.OutputFilePath,
                                job.Format,
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

    private static IReadOnlyList<ConversionJob> CreateConversionJobs(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<ExportFormat> targetFormats,
        string outputFolderPath
    ) =>
        sourceFilePaths
            .SelectMany(
                (sourcePath, sourceIndex) =>
                    targetFormats.Select(format => new ConversionJob(
                        sourceIndex,
                        sourcePath,
                        GetOutputPath(outputFolderPath, sourcePath, format),
                        format
                    ))
            )
            .ToArray();

    private static string GetOutputPath(
        string outputFolderPath,
        string sourcePath,
        ExportFormat format
    )
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        var formatSuffix = format switch
        {
            ExportFormat.HtmlDark => ".dark",
            ExportFormat.HtmlLight => ".light",
            _ => "",
        };

        return Path.Combine(
            outputFolderPath,
            fileNameWithoutExtension + formatSuffix + "." + format.GetFileExtension()
        );
    }

    private sealed record ConversionJob(
        int SourceIndex,
        string SourceFilePath,
        string OutputFilePath,
        ExportFormat Format
    );
}

public sealed record ConversionResultRow(
    string SourceFilePath,
    string OutputFilePath,
    ExportFormat Format,
    bool HasConversionData,
    bool IsSuccess,
    string Message
);
