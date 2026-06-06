using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Gress;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordChatExporter.Gui.Tests;

public sealed class DashboardProgressTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<DialogManager>();
        services.AddSingleton<SnackbarManager>();
        services.AddSingleton<ViewManager>();
        services.AddSingleton<ViewModelManager>();

        services.AddSingleton<SettingsService>();
        services.AddSingleton<UpdateService>();

        services.AddSingleton<LocalizationManager>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<ConversionViewModel>();
        services.AddTransient<ExportSetupViewModel>();
        services.AddTransient<MessageBoxViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(true);
    }

    private static DashboardViewModel CreateViewModel()
    {
        var provider = BuildServices();
        return provider.GetRequiredService<DashboardViewModel>();
    }

    private static void Invoke(
        DashboardViewModel viewModel,
        string methodName,
        params object?[] args
    )
    {
        var method = typeof(DashboardViewModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        method.Should().NotBeNull();
        method!.Invoke(viewModel, args);
    }

    private static T? Invoke<T>(
        DashboardViewModel viewModel,
        string methodName,
        params object?[] args
    )
    {
        var method = typeof(DashboardViewModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        method.Should().NotBeNull();
        return (T?)method!.Invoke(viewModel, args);
    }

    [AvaloniaFact]
    public void Completed_channel_shrinks_overestimated_total_to_actual_messages_read()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 10, 10 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.2), 2, DateTimeOffset.UnixEpoch)
        );

        Invoke(viewModel, "MarkExportProgressCompleted", 0);

        viewModel.DisplayedProgressFraction.Should().BeApproximately(2.0 / 12, 0.0001);
    }

    [AvaloniaFact]
    public void Completed_run_with_overestimated_totals_reports_finished()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 10 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.2), 2, DateTimeOffset.UnixEpoch)
        );

        Invoke(viewModel, "MarkExportProgressCompleted", 0);

        viewModel.DisplayedProgressFraction.Should().Be(1);
    }

    [AvaloniaFact]
    public void Underestimated_count_stays_below_finished_until_channel_completes()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 2 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.5), 3, DateTimeOffset.UnixEpoch)
        );

        viewModel.DisplayedProgressFraction.Should().BeLessThan(1);

        Invoke(viewModel, "MarkExportProgressCompleted", 0);

        viewModel.DisplayedProgressFraction.Should().Be(1);
    }

    [AvaloniaFact]
    public void Overestimated_count_is_corrected_by_modeled_progress_fraction()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 100 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.5), 10, DateTimeOffset.UnixEpoch)
        );

        viewModel.DisplayedProgressFraction.Should().BeApproximately(0.5, 0.0001);
    }

    [AvaloniaFact]
    public void Mixed_missing_estimate_still_uses_count_based_progress()
    {
        var viewModel = CreateViewModel();

        // One channel counted (2), one uncountable (null). The uncounted channel borrows the
        // counted channel's total as a fallback, so the bar stays count-based instead of reverting
        // to the muxer's timestamp fraction (which would read ~0.25 here).
        Invoke(viewModel, "StartExportProgressRun", new long?[] { 2, null });
        viewModel.Progress.Report(Percentage.FromFraction(0.25));
        Dispatcher.UIThread.RunJobs();

        Invoke(
            viewModel,
            "ApplyExportProgress",
            1,
            new ExportProgress(Percentage.FromFraction(0.9), 1, DateTimeOffset.UnixEpoch)
        );

        // read=1 over a modeled total of ~3 (fallback 2 for the uncounted channel, corrected by the
        // 1 message actually read at fraction 0.9) -> ~0.333, NOT the muxer's 0.25.
        viewModel.DisplayedProgressFraction.Should().BeApproximately(1.0 / 3, 0.02);
    }

    [AvaloniaFact]
    public void Completed_empty_unknown_channel_does_not_enable_count_based_progress()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { null, null });
        viewModel.Progress.Report(Percentage.FromFraction(0.25));
        Dispatcher.UIThread.RunJobs();

        Invoke(viewModel, "MarkExportProgressCompleted", 0);
        Invoke(
            viewModel,
            "ApplyExportProgress",
            1,
            new ExportProgress(Percentage.FromFraction(0), 1, DateTimeOffset.UnixEpoch)
        );

        viewModel.DisplayedProgressFraction.Should().BeApproximately(0.25, 0.0001);
    }

    [AvaloniaFact]
    public void Status_text_shows_messages_left_when_total_is_known()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 100 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.1), 10, DateTimeOffset.UnixEpoch)
        );

        // total corrects to 100, read 10 -> 90 left.
        viewModel.MessagesReadText.Should().Contain("90 left");
    }

    [AvaloniaFact]
    public void Status_text_shows_only_read_count_when_total_is_unknown()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { (long?)null });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.5), 5, DateTimeOffset.UnixEpoch)
        );

        viewModel.MessagesReadText.Should().NotBeNull();
        viewModel.MessagesReadText.Should().NotContain("left");
    }

    [AvaloniaFact]
    public void Status_text_hides_messages_left_when_total_is_overrun()
    {
        var viewModel = CreateViewModel();

        var text = Invoke<string>(viewModel, "FormatMessagesRead", 5L, 2L);

        text.Should().Be("5 messages");
    }

    [AvaloniaFact]
    public async Task Background_completion_updates_bound_properties_on_ui_thread()
    {
        var viewModel = CreateViewModel();
        var uiThreadId = Environment.CurrentManagedThreadId;
        var progressChangedThreadIds = new List<int>();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 10 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.2), 2, DateTimeOffset.UnixEpoch)
        );

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DashboardViewModel.DisplayedProgressFraction))
                progressChangedThreadIds.Add(Environment.CurrentManagedThreadId);
        };

        await Task.Run(() => Invoke(viewModel, "MarkExportProgressCompleted", 0));

        viewModel.DisplayedProgressFraction.Should().BeApproximately(0.2, 0.0001);

        Dispatcher.UIThread.RunJobs();

        viewModel.DisplayedProgressFraction.Should().Be(1);
        progressChangedThreadIds.Should().ContainSingle().Which.Should().Be(uiThreadId);
    }

    [AvaloniaFact]
    public void Overlapping_rate_limit_pauses_clear_only_after_all_pauses_resume()
    {
        var viewModel = CreateViewModel();

        Invoke(
            viewModel,
            "HandleRateLimitChanged",
            null,
            new RateLimitState(true, TimeSpan.FromSeconds(5))
        );
        Invoke(
            viewModel,
            "HandleRateLimitChanged",
            null,
            new RateLimitState(true, TimeSpan.FromSeconds(10))
        );
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeTrue();

        Invoke(viewModel, "HandleRateLimitChanged", null, new RateLimitState(false, TimeSpan.Zero));
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeTrue();

        Invoke(viewModel, "HandleRateLimitChanged", null, new RateLimitState(false, TimeSpan.Zero));
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeFalse();
    }
}
