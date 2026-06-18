using System.Windows;
using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace OpenSteamUnlocker.App.Views;

public partial class MainWindow : INavigationWindow
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        SetPageService(pageProvider);
        navigationService.SetNavigationControl(RootNavigation);
    }

    public INavigationView GetNavigation() => RootNavigation;

    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider)
        => RootNavigation.SetPageProviderService(navigationViewPageProvider);

    public void ShowWindow() => Show();

    public void CloseWindow() => Close();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Land on the home page by default.
        Navigate(typeof(Pages.HomePage));
    }

    INavigationView INavigationWindow.GetNavigation() => RootNavigation;

    void INavigationWindow.SetServiceProvider(IServiceProvider serviceProvider) { }
}
