using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace OpenSteamUnlocker.App.ViewModels;

public sealed class TrainerEntry
{
    public string   Name    { get; set; } = string.Empty;
    public string   ExePath { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

public partial class TrainersViewModel : ObservableObject
{
    private static readonly string _dataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenSteamUnlocker", "trainers.json");

    private readonly ObservableCollection<TrainerEntry> _trainers = new();

    // ── Search / filter ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredTrainers))]
    private string _searchQuery = string.Empty;

    public IReadOnlyList<TrainerEntry> FilteredTrainers =>
        string.IsNullOrWhiteSpace(SearchQuery)
            ? (IReadOnlyList<TrainerEntry>)_trainers
            : _trainers.Where(t => t.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                       .ToList();

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool   _showStatus;

    public TrainersViewModel()
    {
        Load();
        _trainers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilteredTrainers));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddTrainerAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title       = "選擇修改器執行檔",
            Filter      = "可執行檔 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        _trainers.Add(new TrainerEntry
        {
            Name    = Path.GetFileNameWithoutExtension(dlg.FileName),
            ExePath = dlg.FileName,
            AddedAt = DateTime.Now,
        });
        await SaveAsync();
    }

    [RelayCommand]
    private async Task RemoveTrainerAsync(TrainerEntry? entry)
    {
        if (entry is null) return;
        _trainers.Remove(entry);
        await SaveAsync();
    }

    [RelayCommand]
    private void LaunchTrainer(TrainerEntry? entry)
    {
        if (entry is null) return;
        if (!File.Exists(entry.ExePath))
        {
            ShowMessage($"找不到修改器：{entry.ExePath}");
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName         = entry.ExePath,
            WorkingDirectory = Path.GetDirectoryName(entry.ExePath),
            UseShellExecute  = true,
        });
    }

    [RelayCommand]
    private void SearchOnFling()
    {
        var q = Uri.EscapeDataString(SearchQuery.Trim());
        Process.Start(new ProcessStartInfo
        {
            FileName        = $"https://flingtrainer.com/?s={q}",
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void SearchOnWemod()
    {
        var q = Uri.EscapeDataString(SearchQuery.Trim());
        Process.Start(new ProcessStartInfo
        {
            FileName        = $"https://www.wemod.com/cheats?q={q}",
            UseShellExecute = true,
        });
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_dataPath)) return;
            var list = JsonSerializer.Deserialize<List<TrainerEntry>>(File.ReadAllText(_dataPath));
            if (list is not null)
                foreach (var e in list) _trainers.Add(e);
        }
        catch { /* corrupt file — start fresh */ }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
        var json = JsonSerializer.Serialize(
            _trainers.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_dataPath, json);
    }

    private void ShowMessage(string msg)
    {
        StatusText  = msg;
        ShowStatus  = true;
    }
}
