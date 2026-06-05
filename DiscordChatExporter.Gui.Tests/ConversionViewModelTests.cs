using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class ConversionViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceGuiConversion_" + Guid.NewGuid().ToString("N")
    );

    public ConversionViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string WriteJson(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $$"""
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": ""
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": null
              },
              "dateRange": {
                "after": null,
                "before": null
              },
              "exportedAt": "1970-01-01T00:00:00.0000000+00:00",
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "{{content}}",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1
            }
            """
        );
        return path;
    }

    private string WriteInvalidJson(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{");
        return path;
    }

    private ConversionViewModel CreateViewModel() =>
        new(new DialogManager(), new LocalizationManager(new SettingsService()))
        {
            OutputFolderPath = _dir,
            IsHtmlDarkSelected = false,
        };

    [Fact]
    public async Task Convert_records_failed_source_and_continues_with_remaining_sources()
    {
        var badJson = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(badJson, "{", TestContext.Current.CancellationToken);
        var goodJson = WriteJson("good.json", "hello from json");
        var viewModel = CreateViewModel();
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(badJson);
        viewModel.SourceFilePaths.Add(goodJson);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().Contain(r => r.SourceFilePath == badJson && !r.IsSuccess);
        viewModel.Results.Should().Contain(r => r.SourceFilePath == goodJson && r.IsSuccess);
        (
            await File.ReadAllTextAsync(
                Path.Combine(_dir, "good.csv"),
                TestContext.Current.CancellationToken
            )
        ).Should()
            .Contain("hello from json");
    }

    [Fact]
    public async Task Convert_uses_initial_sources_formats_and_output_folder()
    {
        var outputDir = Path.Combine(_dir, "output");
        var changedOutputDir = Path.Combine(_dir, "changed-output");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(changedOutputDir);

        var firstJson = WriteInvalidJson(Path.Combine("sources", "first.json"));
        var secondJson = WriteInvalidJson(Path.Combine("sources", "second.json"));
        var lateJson = WriteInvalidJson(Path.Combine("sources", "late.json"));

        var viewModel = CreateViewModel();
        viewModel.OutputFolderPath = outputDir;
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(firstJson);
        viewModel.SourceFilePaths.Add(secondJson);

        var changedLiveState = false;
        viewModel.Results.CollectionChanged += (_, _) =>
        {
            if (changedLiveState)
                return;

            changedLiveState = true;
            viewModel.OutputFolderPath = changedOutputDir;
            viewModel.IsCsvSelected = false;
            viewModel.IsTxtSelected = true;
            viewModel.SourceFilePaths.Add(lateJson);
        };

        var act = () => viewModel.ConvertCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => r.Format == ExportFormat.Csv);
        viewModel.Results
            .Should()
            .OnlyContain(r => Path.GetDirectoryName(r.OutputFilePath) == outputDir);
        viewModel.Results.Should().NotContain(r => r.SourceFilePath == lateJson);
    }

    [Fact]
    public async Task Convert_reports_conflicts_for_sources_that_would_write_same_output_path()
    {
        var firstJson = WriteJson(Path.Combine("first", "chat.json"), "from first json");
        var secondJson = WriteJson(Path.Combine("second", "chat.json"), "from second json");
        var viewModel = CreateViewModel();
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(firstJson);
        viewModel.SourceFilePaths.Add(secondJson);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => !r.IsSuccess);
        viewModel.Results.Should().OnlyContain(r => r.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        viewModel.Results
            .Select(r => r.OutputFilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Should()
            .ContainSingle();
        File.Exists(Path.Combine(_dir, "chat.csv")).Should().BeFalse();
    }

    [Fact]
    public async Task Convert_writes_distinct_outputs_for_html_dark_and_light()
    {
        var json = WriteJson("chat.json", "hello html");
        var viewModel = CreateViewModel();
        viewModel.IsHtmlDarkSelected = true;
        viewModel.IsHtmlLightSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        var htmlResults = viewModel.Results.Where(r => r.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight);
        htmlResults.Select(r => r.OutputFilePath).Should().OnlyHaveUniqueItems();
        htmlResults.Should().HaveCount(2);
        foreach (var result in htmlResults)
            File.Exists(result.OutputFilePath).Should().BeTrue();
    }

    [Fact]
    public void Commands_that_mutate_conversion_state_are_disabled_while_busy()
    {
        var viewModel = CreateViewModel();
        viewModel.SourceFilePaths.Add(WriteJson("chat.json", "hello"));
        viewModel.IsCsvSelected = true;

        viewModel.IsBusy = true;

        viewModel.PickFilesCommand.CanExecute(null).Should().BeFalse();
        viewModel.PickOutputFolderCommand.CanExecute(null).Should().BeFalse();
        viewModel.ConvertCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Pick_output_folder_keeps_existing_path_when_picker_is_canceled()
    {
        const string ExistingOutputPath = "C:\\exports";
        var viewModel = new ConversionViewModel(
            new DialogManager(),
            new LocalizationManager(new SettingsService()),
            promptDirectoryPathAsync: _ => Task.FromResult<string?>(null),
            promptMultipleFilePathsAsync: _ =>
                Task.FromResult<IReadOnlyList<string>>([WriteJson("chat.json", "hello")])
        )
        {
            OutputFolderPath = ExistingOutputPath,
            IsHtmlDarkSelected = false,
        };

        await viewModel.PickOutputFolderCommand.ExecuteAsync(null);

        viewModel.OutputFolderPath.Should().Be(ExistingOutputPath);
    }
}
