using System.Windows.Input;
using OpenSteamUnlocker.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace OpenSteamUnlocker.App.Views.Pages;

public partial class SearchPage : INavigableView<SearchViewModel>
{
    public SearchViewModel ViewModel { get; }

    public SearchPage(SearchViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SearchCommand.CanExecute(null))
            ViewModel.SearchCommand.Execute(null);
    }
}
