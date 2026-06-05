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

    private static void Invoke(DashboardViewModel viewModel, string methodName, params object?[] args)
    {
        var method = typeof(DashboardViewModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        method.Should().NotBeNull();
        method!.Invoke(viewModel, args);
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
    public void Mixed_missing_estimate_leaves_fallback_fraction_owned_by_muxer()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 2, null });
        viewModel.Progress.Report(Percentage.FromFraction(0.25));
        Dispatcher.UIThread.RunJobs();

        Invoke(
            viewModel,
            "ApplyExportProgress",
            1,
            new ExportProgress(Percentage.FromFraction(0.9), 1, DateTimeOffset.UnixEpoch)
        );

        viewModel.DisplayedProgressFraction.Should().BeApproximately(0.25, 0.0001);
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

        Invoke(
            viewModel,
            "HandleRateLimitChanged",
            null,
            new RateLimitState(false, TimeSpan.Zero)
        );
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeTrue();

        Invoke(
            viewModel,
            "HandleRateLimitChanged",
            null,
            new RateLimitState(false, TimeSpan.Zero)
        );
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeFalse();
    }
}
