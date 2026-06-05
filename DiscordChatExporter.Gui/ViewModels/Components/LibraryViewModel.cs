using System;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;

namespace DiscordChatExporter.Gui.ViewModels.Components;

// Placeholder shell for the Library home view. Task 5 fleshes this out into the real catalog +
// global search UI; for now it only carries the back-navigation affordance so the Dashboard <->
// Library view switch can be wired and tested.
public partial class LibraryViewModel(LocalizationManager localizationManager) : ViewModelBase
{
    public LocalizationManager LocalizationManager { get; } = localizationManager;

    // Raised when the user wants to return to the Dashboard.
    public event EventHandler? BackRequested;

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);
}
