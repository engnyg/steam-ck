using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class TrainersViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchQuery = string.Empty;
}
