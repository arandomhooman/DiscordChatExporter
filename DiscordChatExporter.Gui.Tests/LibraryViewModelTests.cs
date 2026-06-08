using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;

namespace DiscordChatExporter.Gui.Tests;

public sealed class LibraryViewModelTests
{
    [AvaloniaFact]
    public async Task Blocking_search_work_runs_off_the_ui_thread()
    {
        Dispatcher.UIThread.CheckAccess().Should().BeTrue();
        var ranOnUiThread = true;

        await LibraryViewModel.RunSearchOffUiThreadAsync(() =>
        {
            ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            return ValueTask.FromResult<IReadOnlyList<SqliteSearchHit>>([]);
        });

        ranOnUiThread.Should().BeFalse();
    }
}
