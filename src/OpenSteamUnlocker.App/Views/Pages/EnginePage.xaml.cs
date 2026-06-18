using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OpenSteamUnlocker.App.Views.Pages;

public partial class EnginePage : INavigableView<EngineViewModel>
{
    public EngineViewModel ViewModel { get; }

    public EnginePage(EngineViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
