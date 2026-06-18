using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly ISteamLocator _steamLocator;

    [ObservableProperty]
    private string _steamPath = "偵測中…";

    [ObservableProperty]
    private bool _steamInstalled;

    public ObservableCollection<string> LibraryFolders { get; } = new();

    public HomeViewModel(ISteamLocator steamLocator)
    {
        _steamLocator = steamLocator;
        RefreshSteam();
    }

    [RelayCommand]
    private void RefreshSteam()
    {
        _steamLocator.Refresh();
        SteamInstalled = _steamLocator.IsInstalled;
        SteamPath = _steamLocator.SteamPath ?? "未偵測到 Steam 安裝";

        LibraryFolders.Clear();
        foreach (var folder in _steamLocator.GetLibraryFolders())
            LibraryFolders.Add(folder);
    }
}
