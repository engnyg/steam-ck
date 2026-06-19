using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OpenSteamUnlocker.App.Views.Pages;

public partial class AboutPage : INavigableView<AboutViewModel>
{
    public AboutViewModel ViewModel { get; }

    public AboutPage(AboutViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
