using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private int _downloadTimeoutSeconds = 30;
}
