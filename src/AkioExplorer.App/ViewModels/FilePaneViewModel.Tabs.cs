using System.Collections.ObjectModel;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AkioExplorer.Infrastructure.Local;

namespace AkioExplorer.App.ViewModels;

/// <summary>One tab of a pane: its folder and its own back/forward history.</summary>
public sealed partial class PaneTab : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;

    public StorageLocation? Location { get; set; }

    internal Stack<StorageLocation> Back { get; } = new();

    internal Stack<StorageLocation> Forward { get; } = new();

    /// <summary>Item to select again when the tab comes back to the front.</summary>
    internal string? FocusPath { get; set; }
}

/// <summary>
/// Tabs (OneCommander / browser style). The pane keeps one listing; switching a tab re-navigates to that tab's folder,
/// which the listing cache makes instant. Each tab owns its back/forward stacks.
/// </summary>
public sealed partial class FilePaneViewModel
{
    private const int MaxClosedTabs = 20;
    private readonly Stack<PaneTab> _closedTabs = new();

    public ObservableCollection<PaneTab> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTabIndex))]
    private PaneTab _activeTab = null!;

    /// <summary>The tab strip shows once there is something to switch between.</summary>
    public bool HasTabs => Tabs.Count > 1;

    public int ActiveTabIndex => Tabs.IndexOf(ActiveTab);

    private Stack<StorageLocation> _back => ActiveTab.Back;

    private Stack<StorageLocation> _forward => ActiveTab.Forward;

    private void InitializeTabs()
    {
        var first = new PaneTab();
        Tabs.Add(first);
        _activeTab = first;
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(ActiveTabIndex));
        };
    }

    private void UpdateActiveTab(IStorageProvider provider, StorageLocation target)
    {
        ActiveTab.Location = target;
        ActiveTab.Title = provider.GetDisplayName(target.Path);
    }

    /// <summary>Ctrl+T: a new tab at the current folder (like duplicating a browser tab, but with fresh history).</summary>
    [RelayCommand]
    private void NewTab()
    {
        if (Location is null) return;
        OpenInNewTab(Location, activate: true);
    }

    /// <summary>Opens <paramref name="location"/> in a new tab right after the current one.</summary>
    public void OpenInNewTab(StorageLocation location, bool activate)
    {
        var tab = new PaneTab
        {
            Location = location,
            Title = _registry.Find(location.ProviderId)?.GetDisplayName(location.Path) ?? location.Path,
        };
        Tabs.Insert(ActiveTabIndex + 1, tab);
        if (activate) _ = SwitchToTabAsync(tab);
    }

    /// <summary>
    /// A folder (or file) handed over by Windows: Win+E, a double-clicked folder, "AkioExplorer ." in a terminal.
    /// New-tab mode reuses a tab already showing that folder (OneCommander's "existing tab with the same path").
    /// </summary>
    public async Task OpenExternalAsync(string path, bool newTab)
    {
        string? focus = null;
        string folder = path;
        if (File.Exists(path))
        {
            focus = path;
            folder = System.IO.Path.GetDirectoryName(path) ?? path;
        }
        var location = new StorageLocation(LocalStorageProvider.ProviderId, folder);

        if (!newTab)
        {
            await NavigateAsync(location, NavigationKind.New, focusPath: focus);
            return;
        }

        static string Norm(string p) => p.Length > 3 ? p.TrimEnd('\\', '/') : p;
        var existing = Tabs.FirstOrDefault(t => t.Location is { ProviderId: LocalStorageProvider.ProviderId } l
                                                && string.Equals(Norm(l.Path), Norm(folder), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing == ActiveTab)
            {
                if (focus is not null) await NavigateAsync(location, NavigationKind.Restore, focusPath: focus);
                return;
            }
            existing.FocusPath = focus ?? existing.FocusPath;
            await SwitchToTabAsync(existing);
            return;
        }

        var tab = new PaneTab
        {
            Location = location,
            Title = _registry.Find(location.ProviderId)?.GetDisplayName(location.Path) ?? location.Path,
            FocusPath = focus,
        };
        Tabs.Insert(ActiveTabIndex + 1, tab);
        await SwitchToTabAsync(tab);
    }

    /// <summary>Opens the selected folder (or the current one) in a new tab.</summary>
    [RelayCommand]
    private void OpenSelectionInNewTab()
    {
        if (Provider is null) return;
        var folders = _selected.Where(s => s.IsContainer).Take(10).ToList();
        if (folders.Count == 0)
        {
            NewTab();
            return;
        }
        for (int i = folders.Count - 1; i >= 0; i--)
            OpenInNewTab(new StorageLocation(Provider.Id, folders[i].Path), activate: false);
        _ = SwitchToTabAsync(Tabs[ActiveTabIndex + 1]);
    }

    public async Task SwitchToTabAsync(PaneTab tab)
    {
        if (tab == ActiveTab || !Tabs.Contains(tab)) return;
        ActiveTab.FocusPath = _selected.FirstOrDefault()?.Path;
        ActiveTab = tab;
        UpdateCommandStates();
        if (tab.Location is { } location) await NavigateAsync(location, NavigationKind.Restore, focusPath: tab.FocusPath);
    }

    [RelayCommand]
    private void CloseTab(PaneTab? tab)
    {
        tab ??= ActiveTab;
        if (Tabs.Count <= 1 || !Tabs.Contains(tab)) return;

        int index = Tabs.IndexOf(tab);
        if (tab == ActiveTab) tab.FocusPath = _selected.FirstOrDefault()?.Path;
        _closedTabs.Push(tab);
        while (_closedTabs.Count > MaxClosedTabs) TrimClosedTabs();

        if (tab == ActiveTab)
        {
            var next = index + 1 < Tabs.Count ? Tabs[index + 1] : Tabs[index - 1];
            _ = SwitchToTabAsync(next);
        }
        Tabs.Remove(tab);
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    private void TrimClosedTabs()
    {
        var keep = _closedTabs.Take(MaxClosedTabs).Reverse().ToList();
        _closedTabs.Clear();
        foreach (var t in keep) _closedTabs.Push(t);
    }

    private bool CanReopenClosedTab() => _closedTabs.Count > 0;

    /// <summary>Ctrl+Shift+T: brings back the last closed tab with its history.</summary>
    [RelayCommand(CanExecute = nameof(CanReopenClosedTab))]
    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        var tab = _closedTabs.Pop();
        Tabs.Insert(ActiveTabIndex + 1, tab);
        _ = SwitchToTabAsync(tab);
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NextTab() => CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int step)
    {
        if (Tabs.Count < 2) return;
        int index = (ActiveTabIndex + step + Tabs.Count) % Tabs.Count;
        _ = SwitchToTabAsync(Tabs[index]);
    }

    /// <summary>Closes every tab except the current one.</summary>
    [RelayCommand]
    private void CloseOtherTabs()
    {
        foreach (var tab in Tabs.Where(t => t != ActiveTab).ToList())
        {
            _closedTabs.Push(tab);
            Tabs.Remove(tab);
        }
        while (_closedTabs.Count > MaxClosedTabs) TrimClosedTabs();
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    private List<TabState> CaptureTabs() =>
        Tabs.Where(t => t.Location is not null)
            .Select(t => new TabState { ProviderId = t.Location!.ProviderId, Path = t.Location.Path })
            .ToList();

    /// <summary>Recreates saved tabs; the active one is navigated by the shell (it is the pane's saved location).</summary>
    private void RestoreTabs(PaneState state)
    {
        if (state.Tabs.Count < 2) return;
        Tabs.Clear();
        foreach (var saved in state.Tabs)
        {
            var location = new StorageLocation(saved.ProviderId, saved.Path);
            Tabs.Add(new PaneTab
            {
                Location = location,
                Title = _registry.Find(saved.ProviderId)?.GetDisplayName(saved.Path) ?? (saved.Path.Length == 0 ? "This PC" : Path.GetFileName(saved.Path.TrimEnd('\\'))),
            });
        }
        ActiveTab = Tabs[Math.Clamp(state.ActiveTab, 0, Tabs.Count - 1)];
    }
}
