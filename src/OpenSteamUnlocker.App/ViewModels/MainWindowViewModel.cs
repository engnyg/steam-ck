using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _applicationTitle = "OpenSteam Unlocker";
}
