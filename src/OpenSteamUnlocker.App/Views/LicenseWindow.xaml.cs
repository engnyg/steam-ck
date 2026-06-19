using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Controls;

namespace OpenSteamUnlocker.App.Views;

public partial class LicenseWindow : FluentWindow
{
    public LicenseViewModel ViewModel { get; }
    private bool _accepted;

    public LicenseWindow(LicenseViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        ViewModel.LicenseAccepted += () =>
        {
            _accepted = true;
            App.GetService<MainWindow>().Show();
            Close();
        };
    }

    private void OnPurchaseLinkClick(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_accepted)
            Application.Current.Shutdown();
    }
}
