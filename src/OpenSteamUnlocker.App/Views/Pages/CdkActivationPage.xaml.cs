using System.Windows.Input;
using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OpenSteamUnlocker.App.Views.Pages;

public partial class CdkActivationPage : INavigableView<CdkActivationViewModel>
{
    public CdkActivationViewModel ViewModel { get; }

    public CdkActivationPage(CdkActivationViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    // Allow pressing Enter in the key field to trigger activation.
    private void KeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.ActivateCommand.CanExecute(null))
            ViewModel.ActivateCommand.Execute(null);
    }
}
