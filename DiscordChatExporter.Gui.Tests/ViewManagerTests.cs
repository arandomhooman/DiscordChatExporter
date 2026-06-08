using System;
using System.Threading.Tasks;
using DiscordChatExporter.Gui.Framework;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class ViewManagerTests
{
    private sealed class CountingViewModel : ViewModelBase
    {
        public int InitializeCount { get; private set; }

        public override Task InitializeAsync()
        {
            InitializeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingViewModel : ViewModelBase
    {
        public override Task InitializeAsync() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Initialize_view_model_once_does_not_run_initialize_twice()
    {
        var manager = new ViewManager();
        var viewModel = new CountingViewModel();

        await manager.InitializeViewModelOnceAsync(viewModel);
        await manager.InitializeViewModelOnceAsync(viewModel);

        viewModel.InitializeCount.Should().Be(1);
    }

    [Fact]
    public async Task Initialize_view_model_once_contains_initialize_failures()
    {
        var manager = new ViewManager();
        var viewModel = new ThrowingViewModel();

        var act = async () => await manager.InitializeViewModelOnceAsync(viewModel);

        await act.Should().NotThrowAsync();
    }
}
