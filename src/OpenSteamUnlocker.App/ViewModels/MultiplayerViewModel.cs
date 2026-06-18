using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class MultiplayerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appId = "480";

    [ObservableProperty]
    private string _gameExecutable = string.Empty;
}
