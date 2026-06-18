using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OpenSteamUnlocker.App.Views.Pages;

public partial class TrainersPage : INavigableView<TrainersViewModel>
{
    public TrainersViewModel ViewModel { get; }

    public TrainersPage(TrainersViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
