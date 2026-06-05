using System.Reflection;
using Avalonia.Headless.XUnit;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordChatExporter.Gui.Tests;

// Guards the Continue-export FAB gating, which has regressed TWICE: CanContinueExport must require a
// selected guild + channels exactly like CanExport. If it only checks authentication, the Continue
// FAB floats into the Export FAB's slot whenever the user is merely logged in (no channel chosen).
public sealed class DashboardCommandGatingTests
{
    private static DashboardViewModel CreateViewModel()
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

        var provider = services.BuildServiceProvider(true);
        return provider.GetRequiredService<DashboardViewModel>();
    }

    // _discord is private and only set after a real token auth; fake it so the gate's *other*
    // conditions (guild + channel selection) are what's under test.
    private static void Authenticate(DashboardViewModel vm)
    {
        typeof(DashboardViewModel)
            .GetField("_discord", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new DiscordClient("fake-token"));
        vm.SelectedGuild = new Guild(new Snowflake(1), "Test Guild", "");
    }

    [AvaloniaFact]
    public void Continue_export_is_gated_like_export_when_no_channel_is_selected()
    {
        var vm = CreateViewModel();
        Authenticate(vm);

        // Authenticated with a guild, but NO channels selected — this is the state where the bug
        // shows the Continue FAB floating in the Export FAB's place.
        vm.SelectedChannels.Should().BeEmpty();

        vm.ContinueExportCommand.CanExecute(null)
            .Should()
            .BeFalse("Continue-export must require a selected channel, exactly like Export");
        vm.ExportCommand.CanExecute(null)
            .Should()
            .BeFalse("Export is also disabled with no channel — Continue must match it");
    }
}
