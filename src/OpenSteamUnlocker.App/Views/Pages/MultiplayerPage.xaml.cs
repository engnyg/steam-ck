using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OpenSteamUnlocker.App.Views.Pages;

public partial class MultiplayerPage : INavigableView<MultiplayerViewModel>
{
    public MultiplayerViewModel ViewModel { get; }

    public MultiplayerPage(MultiplayerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
