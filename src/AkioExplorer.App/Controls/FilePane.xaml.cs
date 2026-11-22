using System.ComponentModel;
using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Transfers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.System;
using Windows.UI.Core;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Reusable file browser pane. Pure view logic: focus, selection sync, keyboard handling, menus.
/// Everything that touches storage goes through <see cref="FilePaneViewModel"/>.
/// </summary>
public sealed partial class FilePane : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(FilePaneViewModel), typeof(FilePane), new PropertyMetadata(null, OnViewModelChanged));

    private bool _syncingSelection;
    private string _typeAhead = string.Empty;
    private DateTime _typeAheadAt;
    private CancellationTokenSource? _suggestCts;
    private readonly DragDropBroker _drag = App.Services.GetRequiredService<DragDropBroker>();
    private readonly ClipboardService _clipboard = App.Services.GetRequiredService<ClipboardService>();
    private readonly ThumbnailService _thumbnails = App.Services.GetRequiredService<ThumbnailService>();
    private FileItemViewModel? _dropTarget;
    private readonly ILogger<FilePane> _log = App.Services.GetRequiredService<ILogger<FilePane>>();
    private string? _externalDragRoot;

    public FilePane()
    {
        InitializeComponent();
        BuildSearchChips();
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);
        SetUpHoldToRename();
        Root.GotFocus += (_, _) => ViewModel?.RequestActivation();
        SizeChanged += (_, e) =>
        {
            FilterColumn.Width = new GridLength(e.NewSize.Width < 700 ? 150 : 240);
            ViewModel?.Columns.Update(e.NewSize.Width);
        };
    }

    public FilePaneViewModel ViewModel
    {
        get => (FilePaneViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private ListViewBase? ActiveList => (ListViewBase?)DetailsList ?? TilesGrid;

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (FilePane)d;
        if (e.OldValue is FilePaneViewModel old)
        {
            old.SelectionRequested -= pane.OnSelectionRequested;
            old.RenameRequested -= pane.OnRenameRequested;
            old.FocusListRequested -= pane.OnFocusListRequested;
            old.AddressEditRequested -= pane.OnAddressEditRequested;
            old.FilterFocusRequested -= pane.OnFilterFocusRequested;
            old.AdvancedSearchRequested -= pane.OnAdvancedSearchRequested;
            old.DialogRequested -= pane.OnDialogRequested;
            old.PropertyChanged -= pane.OnViewModelPropertyChanged;
        }

        if (e.NewValue is FilePaneViewModel vm)
        {
            vm.SelectionRequested += pane.OnSelectionRequested;
            vm.RenameRequested += pane.OnRenameRequested;
            vm.FocusListRequested += pane.OnFocusListRequested;
            vm.AddressEditRequested += pane.OnAddressEditRequested;
            vm.FilterFocusRequested += pane.OnFilterFocusRequested;
            vm.AdvancedSearchRequested += pane.OnAdvancedSearchRequested;
            vm.DialogRequested += pane.OnDialogRequested;
            vm.PropertyChanged += pane.OnViewModelPropertyChanged;
        }

        pane.Bindings.Update();
        pane.SyncViewToggles();
        pane.UpdateSearchChips();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilePaneViewModel.SearchFilters) or nameof(FilePaneViewModel.IsSearchMode)
            or nameof(FilePaneViewModel.FilterText) or nameof(FilePaneViewModel.CanSearch) or nameof(FilePaneViewModel.SearchFolderName))
            UpdateSearchChips();

        if (e.PropertyName == nameof(FilePaneViewModel.ViewMode))
        {
            SyncViewToggles();
            // The other list was just created by x:Load; give it focus if this pane is active.
            DispatcherQueue.TryEnqueue(() => { if (ViewModel.IsActive) FocusList(); });
        }
    }

    private void SyncViewToggles()
    {
        if (ViewModel is null) return;
        DetailsToggle.IsChecked = ViewModel.IsDetailsView;
        GridToggle.IsChecked = ViewModel.IsGridView;
    }

    // ------------------------------------------------------------------ focus & selection

    public void FocusList() => FocusList(attempts: 5);

    private void FocusList(int attempts)
    {
        var list = ActiveList;
        if (list is null) return;

        object? target = list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null);
        if (target is null)
        {
            // An empty ListView isn't a tab stop by default; make it one so keys (Ctrl+V, Backspace…) reach this pane.
            list.IsTabStop = true;
            list.Focus(FocusState.Keyboard);
            return;
        }

        if (list.ContainerFromItem(target) is Control container)
        {
            container.Focus(FocusState.Keyboard);
            return;
        }

        // Rows not realised yet (first layout pass after a navigation): try again after layout.
        if (attempts > 0)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusList(attempts - 1));
        else
            list.Focus(FocusState.Programmatic);
    }

    private void OnFocusListRequested(object? sender, EventArgs e) => FocusList();

    private void OnSelectionRequested(object? sender, IReadOnlyList<FileItemViewModel> items)
    {
        var list = ActiveList;
        if (list is null) return;

        if (items.Count == 0)
        {
            _syncingSelection = true;
            try { list.SelectedItems.Clear(); }
            finally { _syncingSelection = false; }
            ViewModel.UpdateSelection([]);
            return;
        }

        _syncingSelection = true;
        try
        {
            if (items.Count == 1)
            {
                list.SelectedItem = items[0];
            }
            else if (items.Count <= 64)
            {
                list.SelectedItems.Clear();
                foreach (var item in items) list.SelectedItems.Add(item);
            }
            else
            {
                // Big sets (select all, invert, by pattern in a 10k folder): select index ranges, not item by item.
                var index = new Dictionary<FileItemViewModel, int>(ViewModel.Items.Count);
                for (int i = 0; i < ViewModel.Items.Count; i++) index[ViewModel.Items[i]] = i;
                var positions = items.Where(index.ContainsKey).Select(i => index[i]).Order().ToList();
                list.SelectedItems.Clear();
                int start = 0;
                for (int i = 1; i <= positions.Count; i++)
                {
                    if (i < positions.Count && positions[i] == positions[i - 1] + 1) continue;
                    list.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(positions[start], (uint)(i - start)));
                    start = i;
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        ViewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
        list.ScrollIntoView(items[0]);
        if (ViewModel.IsActive) FocusItem(list, items[0], attempts: 3);
    }

    private void FocusItem(ListViewBase list, object item, int attempts)
    {
        if (list.ContainerFromItem(item) is Control container)
        {
            container.Focus(FocusState.Keyboard);
            return;
        }

        // Container not realised yet (just scrolled into view): retry after layout.
        if (attempts > 0)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusItem(list, item, attempts - 1));
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        ViewModel.UpdateSelection(((ListViewBase)sender).SelectedItems.OfType<FileItemViewModel>());
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Lazy icons: only rows that are actually realised on screen request a shell icon (and, in grid view, a
        // picture thumbnail; a recycled tile cancels its pending thumbnail).
        if (args.Item is not FileItemViewModel vm) return;
        if (args.InRecycleQueue)
        {
            if (sender is GridView) _thumbnails.Cancel(vm);
            return;
        }
        vm.EnsureIcon(sender is GridView ? IconSize.Large : IconSize.Small);
        if (sender is GridView) _thumbnails.Request(vm, ViewModel?.Provider);
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel?.RequestActivation();
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed && ViewModel?.Provider is { } provider)
        {
            // Middle-click: folder opens in a background tab (browser convention); on a tab it closes the tab.
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != this; node = VisualTreeHelper.GetParent(node))
            {
                if (node is TabViewItem { DataContext: PaneTab tab })
                {
                    ViewModel.CloseTabCommand.Execute(tab);
                    e.Handled = true;
                    return;
                }
                if (node is FrameworkElement { DataContext: FileItemViewModel { IsContainer: true } folder })
                {
                    ViewModel.OpenInNewTab(new StorageLocation(provider.Id, folder.Path), activate: false);
                    e.Handled = true;
                    return;
                }
            }
        }
        if (props.IsXButton1Pressed)
        {
            ViewModel?.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed)
        {
            ViewModel?.GoForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ list input

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileItemViewModel vm } && !vm.IsRenaming)
        {
            e.Handled = true;
            await ViewModel.OpenAsync(vm);
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // inline rename owns the keyboard

        bool shift = IsKeyDown(VirtualKey.Shift);
        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool alt = IsKeyDown(VirtualKey.Menu);

        switch (e.Key)
        {
            case VirtualKey.Enter when !ctrl && !alt:
                ViewModel.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Back:
                ViewModel.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                if (shift) ViewModel.DeletePermanentlyCommand.Execute(null);
                else ViewModel.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.F2:
                if (ViewModel.SelectedItems.Count > 1) ViewModel.BatchRenameCommand.Execute(null);
                else ViewModel.RenameCommand.Execute(null);
                e.Handled = true;
                break;

            // Total Commander keys: Num + / Num - select / deselect by pattern, Num * inverts.
            case VirtualKey.Add when !ctrl && !alt:
                ViewModel.RequestDialog(PaneDialog.SelectByPattern);
                e.Handled = true;
                break;
            case VirtualKey.Subtract when !ctrl && !alt:
                ViewModel.RequestDialog(PaneDialog.DeselectByPattern);
                e.Handled = true;
                break;
            case VirtualKey.Multiply when !ctrl && !alt:
                ViewModel.InvertSelectionCommand.Execute(null);
                e.Handled = true;
                break;

            case VirtualKey.Escape when ViewModel.FilterText.Length > 0 || ViewModel.IsSearchMode:
                ViewModel.FilterText = string.Empty;
                ViewModel.ExitSearch();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Type-ahead: typing letters jumps to the matching item; repeating a letter cycles through matches.</summary>
    private void OnListCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (args.OriginalSource is TextBox || char.IsControl(args.Character)) return;
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)) return;
        var list = (ListViewBase)sender;
        var items = ViewModel.Items;
        if (items.Count == 0) return;

        var now = DateTime.UtcNow;
        _typeAhead = (now - _typeAheadAt).TotalMilliseconds > 900 ? args.Character.ToString() : _typeAhead + args.Character;
        _typeAheadAt = now;

        bool cycling = _typeAhead.Length > 1 && _typeAhead.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(_typeAhead[0]));
        string needle = cycling ? _typeAhead[..1] : _typeAhead;
        int current = list.SelectedIndex;
        int start = cycling || _typeAhead.Length == 1 ? current + 1 : Math.Max(current, 0);

        for (int i = 0; i < items.Count; i++)
        {
            var candidate = items[(start + i) % items.Count];
            if (candidate.Name.StartsWith(needle, StringComparison.CurrentCultureIgnoreCase))
            {
                OnSelectionRequested(this, [candidate]);
                break;
            }
        }
        args.Handled = true;
    }

    // ------------------------------------------------------------------ inline rename

    private void OnRenameRequested(object? sender, FileItemViewModel vm)
    {
        ActiveList?.ScrollIntoView(vm);
        // Grid tiles are ~100 px wide: rename there in a roomy popup instead (long names stay readable).
        if (ViewModel.IsGridView)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowRenamePopup(vm));
    }

    private void ShowRenamePopup(FileItemViewModel vm)
    {
        if (!vm.IsRenaming || TilesGrid is null) return;
        TilesGrid.UpdateLayout();
        var anchor = TilesGrid.ContainerFromItem(vm) as FrameworkElement ?? TilesGrid;

        var box = new TextBox
        {
            Text = vm.EditName,
            TextWrapping = TextWrapping.Wrap,
            Width = 320,
            MinHeight = 36,
            FontSize = 14,
            IsSpellCheckEnabled = false,
            Header = vm.IsContainer ? "Folder name" : "File name",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "New name");
        var hint = new TextBlock
        {
            Text = "Enter to rename  ·  Esc to cancel",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400,
        };

        // ✓ and ✕ beside the box: confirm/cancel with the mouse too (Enter / Esc still work).
        var confirm = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        ToolTipService.SetToolTip(confirm, "Rename (Enter)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(confirm, "Rename");
        var cancel = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTipService.SetToolTip(cancel, "Cancel (Esc)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cancel, "Cancel rename");

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(confirm, 1);
        Grid.SetColumn(cancel, 2);
        row.Children.Add(box);
        row.Children.Add(confirm);
        row.Children.Add(cancel);
        // The default presenter caps width at 456 px including padding, which clipped the box: size it to the content.
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600.0));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Children = { row, hint } },
            // Starts at the tile's left edge and grows right (flips when there is no room), instead of centring
            // a wide popup over the sidebar.
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            FlyoutPresenterStyle = presenterStyle,
        };

        bool finished = false;
        string? error = null;
        _renamePopupBox = box;
        box.TextChanged += (_, _) =>
        {
            vm.EditName = box.Text;
            // Error prevention: say what's wrong while typing, before Enter.
            error = Core.Logic.FileNameValidator.Validate(box.Text.Trim());
            hint.Text = error ?? "Enter to rename  ·  Esc to cancel";
            hint.Foreground = (Brush)Application.Current.Resources[error is null ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"];
            confirm.IsEnabled = error is null;
        };

        async Task ConfirmAsync()
        {
            if (error is not null || finished) return; // keep the popup open until the name is valid
            finished = true;
            flyout.Hide();
            await ViewModel.CommitRenameAsync(vm);
        }

        void Cancel()
        {
            if (finished) return;
            finished = true;
            flyout.Hide();
            ViewModel.CancelRename(vm);
        }

        confirm.Click += async (_, _) => await ConfirmAsync();
        cancel.Click += (_, _) => Cancel();
        box.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await ConfirmAsync();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Keyboard);
            // Select the name without the extension, like Explorer.
            int dot = vm.IsContainer ? -1 : box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length);
        };
        flyout.Closed += async (_, _) =>
        {
            if (_renamePopupBox == box) _renamePopupBox = null;
            if (finished || !vm.IsRenaming) return;
            // Clicking elsewhere keeps the new name (Explorer behaviour), unless it's invalid.
            if (error is null) await ViewModel.CommitRenameAsync(vm);
            else ViewModel.CancelRename(vm);
        };
        flyout.ShowAt(anchor);
    }

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileItemViewModel vm) return;
        box.Focus(FocusState.Keyboard);
        // Select the name without the extension, like Explorer.
        int dot = vm.IsContainer ? -1 : vm.EditName.LastIndexOf('.');
        box.Select(0, dot > 0 ? dot : vm.EditName.Length);
    }

    private async void OnRenameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItemViewModel vm }) return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.CommitRenameAsync(vm);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelRename(vm);
        }
    }

    private async void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItemViewModel vm } || !vm.IsRenaming) return;
        if (IsRenameProtected)
        {
            // Press-and-hold rename: the list took focus on mouse-up; keep editing instead of committing.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusActiveRenameEditor);
            return;
        }
        await ViewModel.CommitRenameAsync(vm);
    }

    // ------------------------------------------------------------------ context menus

    private void OnListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var list = (ListViewBase)sender;
        var element = e.OriginalSource as FrameworkElement;
        var item = (element as SelectorItem)?.Content as FileItemViewModel ?? element?.DataContext as FileItemViewModel;
        e.Handled = true;

        bool background = item is null;
        if (item is not null)
        {
            if (!list.SelectedItems.Contains(item)) list.SelectedItem = item;
        }
        else
        {
            list.SelectedItems.Clear();
            ViewModel.UpdateSelection([]);
        }

        FlyoutShowOptions? options = e.TryGetPosition(list, out var point) ? new FlyoutShowOptions { Position = point } : null;
        _ = ShowContextMenuAsync(list, element ?? list, options, background);
    }

    private void ConfigureItemMenu()
    {
        var selected = ViewModel.SelectedItems;
        bool single = selected.Count == 1;
        bool singleFolder = single && selected[0].IsContainer;
        bool anyFolder = selected.Any(s => s.IsContainer);
        bool local = ViewModel.Provider is { } p && (p.Capabilities & StorageCapabilities.OpenWithShell) != 0;
        bool dual = ViewModel.ShowActiveIndicator; // true only in dual-pane mode

        // Top level (Hick's law: only what applies to this selection).
        MenuOpenEditor.Visibility = Show(local && ViewModel.OpenInEditorCommand.CanExecute(null));
        MenuOpenWith.Visibility = Show(local && selected.Count > 0 && !selected.Any(s => s.IsDrive));
        MenuRename.Visibility = Show(single);
        MenuProperties.Visibility = Show(local);

        // Open in ▸
        MenuOpenTab.Visibility = Show(anyFolder);
        MenuOpenOther.Visibility = Show(anyFolder);
        MenuTerminal.Visibility = Show(local && singleFolder);
        MenuReveal.Visibility = Show(local);
        MenuReveal.Text = anyFolder ? "File Explorer" : "File Explorer (show file)";

        // Organize ▸
        MenuBatchRename.Visibility = Show(ViewModel.BatchRenameCommand.CanExecute(null));
        MenuBatchRename.Text = single ? "Rename with options…" : $"Rename {selected.Count:N0} items…";
        MenuFolderFromSelection.Visibility = Show(ViewModel.NewFolderFromSelectionCommand.CanExecute(null));
        MenuDuplicate.Visibility = Show(ViewModel.DuplicateCommand.CanExecute(null));
        MenuPinItem.Visibility = Show(singleFolder);
        MenuPinItem.Text = ViewModel.IsSelectionFavorite ? "Unpin from Favorites" : "Pin to Favorites";

        // Archive ▸
        MenuCompress.Visibility = Show(ViewModel.CompressCommand.CanExecute(null));
        MenuExtractHere.Visibility = Show(ViewModel.ExtractHereCommand.CanExecute(null));
        MenuExtractFolder.Visibility = Show(ViewModel.ExtractToFolderCommand.CanExecute(null));

        // Send to ▸ (Windows targets are added when the shell menu arrives)
        MenuCopyOther.Visibility = Show(dual);
        MenuMoveOther.Visibility = Show(dual);
        MenuSendToSeparator.Visibility = Visibility.Collapsed;

        // More tools ▸
        MenuChecksum.Visibility = Show(ViewModel.ChecksumCommand.CanExecute(null));
        MenuFolderSize.Visibility = Show(local && selected.Any(s => s.IsContainer && !s.IsDrive));
        MenuAttrSeparator.Visibility = Show(local);
        MenuHiddenAttr.Visibility = Show(local);
        MenuReadOnlyAttr.Visibility = Show(local);
        MenuHiddenAttr.IsChecked = ViewModel.SelectionHas(StorageItemAttributes.Hidden);
        MenuReadOnlyAttr.IsChecked = ViewModel.SelectionHas(StorageItemAttributes.ReadOnly);

        foreach (var sub in new[] { MenuOpenIn, MenuOrganize, MenuArchive, MenuSendTo }) sub.Visibility = Show(HasVisibleItem(sub));
    }

    /// <summary>A submenu with nothing usable inside is hidden rather than shown empty.</summary>
    private static bool HasVisibleItem(MenuFlyoutSubItem sub) =>
        sub.Items.Any(i => i is not MenuFlyoutSeparator && i.Visibility == Visibility.Visible);

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void ConfigureBackgroundMenu()
    {
        MenuViewDetails.IsChecked = ViewModel.IsDetailsView;
        MenuViewGrid.IsChecked = ViewModel.IsGridView;
        MenuSortName.IsChecked = ViewModel.SortField == SortField.Name;
        MenuSortDate.IsChecked = ViewModel.SortField == SortField.Modified;
        MenuSortType.IsChecked = ViewModel.SortField == SortField.Type;
        MenuSortSize.IsChecked = ViewModel.SortField == SortField.Size;
        MenuSortAsc.IsChecked = !ViewModel.SortDescending;
        MenuSortDesc.IsChecked = ViewModel.SortDescending;
        MenuPinCurrent.Text = ViewModel.IsFavorite ? "Unpin from Favorites" : "Pin to Favorites";

        bool local = ViewModel.Provider is { } p && (p.Capabilities & StorageCapabilities.OpenWithShell) != 0;
        bool hasPath = ViewModel.Location is { Path.Length: > 0 };
        MenuPinCurrent.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        MenuBgExplorer.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        MenuBgProperties.Visibility = local && hasPath ? Visibility.Visible : Visibility.Collapsed;
        MenuNewFile.Visibility = Show(ViewModel.IsLocalFolder);
        MenuBgTerminal.Visibility = Show(ViewModel.IsLocalFolder);
        MenuBgEditor.Visibility = Show(ViewModel.IsLocalFolder);
        MenuBgOpenIn.Visibility = Show(HasVisibleItem(MenuBgOpenIn));
        MenuBgSizes.Visibility = Show(ViewModel.IsLocalFolder);
    }

    private void OnSelectByPattern(object sender, RoutedEventArgs e) => ViewModel.RequestDialog(PaneDialog.SelectByPattern);

    private void OnDeselectByPattern(object sender, RoutedEventArgs e) => ViewModel.RequestDialog(PaneDialog.DeselectByPattern);

    // ------------------------------------------------------------------ pane dialogs

    private async void OnDialogRequested(object? sender, PaneDialog dialog)
    {
        var dialogs = App.Services.GetRequiredService<DialogService>();
        var vm = ViewModel;
        switch (dialog)
        {
            case PaneDialog.BatchRename:
            {
                // List order drives the counter, so number items as the user sees them.
                var chosen = vm.SelectedItems.ToHashSet();
                var items = vm.Items.Where(chosen.Contains).Select(i => i.Item).ToList();
                if (items.Count == 0) return;
                var plan = await Views.BatchRenameDialog.ShowAsync(dialogs, items, vm.SiblingNames, vm.FolderName);
                if (plan is not null) await vm.ApplyBatchRenameAsync(plan);
                break;
            }
            case PaneDialog.SelectByPattern:
            case PaneDialog.DeselectByPattern:
            {
                bool select = dialog == PaneDialog.SelectByPattern;
                var pattern = await Views.ToolDialogs.PatternAsync(dialogs, select);
                if (pattern is not null && vm.SelectByPattern(pattern, select) == 0)
                    vm.ShowMessage($"No items match \"{pattern}\".", isError: false);
                break;
            }
            case PaneDialog.Checksum:
                if (vm.SelectedItems.Count == 1) await Views.ToolDialogs.ChecksumAsync(dialogs, vm.SelectedItems[0].Path);
                break;
        }
        FocusList();
    }

    // ------------------------------------------------------------------ tabs

    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is PaneTab tab && tab != ViewModel.ActiveTab)
        {
            await ViewModel.SwitchToTabAsync(tab);
            FocusList();
        }
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is PaneTab tab) ViewModel.CloseTabCommand.Execute(tab);
    }

    private void OnAddTabClick(TabView sender, object args)
    {
        ViewModel.NewTabCommand.Execute(null);
        FocusList();
    }

    private static PaneTab? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as PaneTab;

    private void OnCloseTabMenu(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) ViewModel.CloseTabCommand.Execute(tab);
    }

    private async void OnCloseOtherTabsMenu(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;
        await ViewModel.SwitchToTabAsync(tab);
        ViewModel.CloseOtherTabsCommand.Execute(null);
    }

    private void OnDuplicateTabMenu(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender)?.Location is { } location) ViewModel.OpenInNewTab(location, activate: true);
    }

    private void OnMenuViewDetails(object sender, RoutedEventArgs e) => ViewModel.ViewMode = FileViewMode.Details;

    private void OnMenuViewGrid(object sender, RoutedEventArgs e) => ViewModel.ViewMode = FileViewMode.Grid;

    private void OnMenuSort(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortField>(tag, out var field))
            ViewModel.SortField = field;
    }

    private void OnMenuSortDirection(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) ViewModel.SortDescending = tag == "Desc";
    }

    private void OnOpenInOtherPane(object sender, RoutedEventArgs e) => ViewModel.RequestOpenInOtherPane();

    private void OnReconnectClick(object sender, RoutedEventArgs e) => ViewModel.RequestReconnect();

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortField>(tag, out var field))
            ViewModel.SortByCommand.Execute(field);
    }

    private void OnDetailsToggleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = FileViewMode.Details;
        SyncViewToggles();
    }

    private void OnGridToggleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = FileViewMode.Grid;
        SyncViewToggles();
    }

    // ------------------------------------------------------------------ drag & drop

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FileItemViewModel>().Where(i => !i.IsDrive).ToList();
        if (items.Count == 0 || ViewModel.Provider is not { } provider || items.Any(i => i.IsRenaming))
        {
            e.Cancel = true;
            return;
        }

        string id = _drag.Begin(provider.Id, items.Select(i => i.Item).ToList());
        _log.LogDebug("Drag start: {Count} items", items.Count);
        e.Data.Properties.Add(DragDropBroker.PayloadProperty, id);
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        // Local files can also be dropped into File Explorer, the desktop or other apps (rendered on demand).
        if ((provider.Capabilities & StorageCapabilities.OpenWithShell) != 0)
        {
            var paths = items.Select(i => i.Path).ToArray();
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => ClipboardService.ProvideStorageItems(request, paths));
        }
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _log.LogDebug("Drag completed: {Op}", args.DropResult);
        _drag.End();
    }

    private IReadOnlyList<StorageItem>? InternalPayload(DataPackageView view, out string providerId)
    {
        providerId = _drag.ProviderId;
        return _drag.Id is { } id && view.Properties.TryGetValue(DragDropBroker.PayloadProperty, out var value) && value as string == id
            ? _drag.Items
            : null;
    }

    private async void OnListDragEnter(object sender, DragEventArgs e)
    {
        _externalDragRoot = null;
        if (InternalPayload(e.DataView, out _) is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        // Learn where external files come from so the default (move on same drive, copy otherwise) matches Explorer.
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0) _externalDragRoot = Path.GetPathRoot(items[0].Path);
        }
        catch
        {
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        // "Into this folder" gets its own big target: in a folder full of subfolders every spot is some subfolder.
        var here = PlanDrop(e, intoCurrentFolder: true);
        bool overBar = UpdateDropBar(here, e);
        var plan = overBar ? here : PlanDrop(e);
        if (plan is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDropTarget(null);
            return;
        }

        e.AcceptedOperation = plan.Value.Kind == OperationKind.Move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"{(plan.Value.Kind == OperationKind.Move ? "Move" : "Copy")} to {plan.Value.FolderName}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        SetDropTarget(plan.Value.Target);
    }

    private void OnListDragLeave(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        // DragLeave also bubbles up from child elements: only hide the bar when the pointer really left the pane.
        var p = e.GetPosition(ContentHost);
        if (p.X < 0 || p.Y < 0 || p.X >= ContentHost.ActualWidth || p.Y >= ContentHost.ActualHeight) HideDropBar();
    }

    /// <summary>Shows the "drop into this folder" bar while a drag is over the pane; returns true when the pointer is on it.</summary>
    private bool UpdateDropBar(DropPlan? here, DragEventArgs e)
    {
        if (here is not { } plan)
        {
            HideDropBar();
            return false;
        }

        DropBarText.Text = $"{(plan.Kind == OperationKind.Move ? "Move" : "Copy")} to {plan.FolderName}";
        DropBarGlyph.Glyph = plan.Kind == OperationKind.Move ? "" : "";
        DropBar.Visibility = Visibility.Visible;

        var p = e.GetPosition(DropBar);
        bool over = DropBar.ActualWidth > 0 && p.X >= 0 && p.Y >= 0 && p.X <= DropBar.ActualWidth && p.Y <= DropBar.ActualHeight;
        DropBar.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[over ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush"];
        return over;
    }

    private void HideDropBar() => DropBar.Visibility = Visibility.Collapsed;

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        var here = PlanDrop(e, intoCurrentFolder: true);
        var plan = DropBar.Visibility == Visibility.Visible && UpdateDropBar(here, e) ? here : PlanDrop(e);
        _log.LogDebug("Drop {Plan}", plan);
        SetDropTarget(null);
        HideDropBar();
        if (plan is not { } p) return;

        ViewModel.RequestActivation();
        var items = InternalPayload(e.DataView, out string providerId);
        if (items is null)
        {
            var deferral = e.GetDeferral();
            try
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                items = await _clipboard.ResolveLocalAsync(storageItems.Select(s => s.Path));
                providerId = LocalStorageProviderId;
            }
            finally
            {
                deferral.Complete();
            }
        }

        if (items.Count > 0) ViewModel.Transfer(p.Kind, providerId, items, p.FolderPath);
    }

    private const string LocalStorageProviderId = "local";

    private readonly record struct DropPlan(OperationKind Kind, string FolderPath, string FolderName, FileItemViewModel? Target);

    /// <summary>Decides target folder and copy/move for the current drag position (Explorer rules).</summary>
    private DropPlan? PlanDrop(DragEventArgs e, bool intoCurrentFolder = false)
    {
        if (ViewModel.Provider is not { } provider || ViewModel.Location is not { } location || ViewModel.IsSearchMode) return null;

        var items = InternalPayload(e.DataView, out string sourceProvider);
        bool external = items is null && e.DataView.Contains(StandardDataFormats.StorageItems);
        if (items is null && !external) return null;
        if (!ViewModel.CanAcceptFrom(items is null ? LocalStorageProviderId : sourceProvider)) return null;

        var target = intoCurrentFolder ? null : ItemUnderPointer(e);
        if (target is { IsContainer: false }) target = null;
        if (target is not null && items?.Any(i => string.Equals(i.Path, target.Path, StringComparison.OrdinalIgnoreCase)) == true)
            return null; // can't drop a folder onto itself

        string folder;
        if (target is not null)
        {
            folder = target.Path;
        }
        else
        {
            if (!ViewModel.CanWriteHere) return null;
            folder = location.Path;
            // Dropping back into the folder the items came from does nothing.
            if (items is not null && sourceProvider == provider.Id &&
                items.All(i => string.Equals(provider.GetParentPath(i.Path), folder, StringComparison.OrdinalIgnoreCase)))
                return null;
        }

        string? sourceRoot = items is not null
            ? (sourceProvider == provider.Id ? Path.GetPathRoot(items[0].Path) : null)
            : _externalDragRoot;
        bool sameVolume = sourceRoot is not null && string.Equals(sourceRoot, Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase);

        var kind = (e.Modifiers & DragDropModifiers.Control) != 0 ? OperationKind.Copy
            : (e.Modifiers & DragDropModifiers.Shift) != 0 ? OperationKind.Move
            : sameVolume ? OperationKind.Move : OperationKind.Copy;

        return new DropPlan(kind, folder, target?.Name ?? provider.GetDisplayName(folder), target);
    }

    /// <summary>Row/tile under the drag position (hit-tested, since the list itself isn't the drop target).</summary>
    private FileItemViewModel? ItemUnderPointer(DragEventArgs e)
    {
        if (ActiveList is not { } list) return null;
        var point = e.GetPosition(null);
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(point, list))
        {
            if (element is SelectorItem { Content: FileItemViewModel vm }) return vm;
        }
        return null;
    }

    private void SetDropTarget(FileItemViewModel? target)
    {
        if (_dropTarget == target) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = target;
        if (target is not null) target.IsDropTarget = true;
    }

    // ------------------------------------------------------------------ address bar

    private void OnAddressEditRequested(object? sender, EventArgs e) => EnterAddressEdit();

    private void OnAddressTapped(object sender, TappedRoutedEventArgs e)
    {
        // Clicks on a breadcrumb segment navigate; clicks on empty space switch to typing mode (like Explorer).
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != AddressHost; node = VisualTreeHelper.GetParent(node))
        {
            if (node is BreadcrumbBarItem || node is AutoSuggestBox) return;
        }
        EnterAddressEdit();
    }

    private void EnterAddressEdit()
    {
        if (ViewModel.Location is null) return;
        Crumbs.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = ViewModel.Location.Path.Length == 0 ? "This PC" : ViewModel.Location.Path;
        AddressBox.UpdateLayout(); // just became visible: template must exist before it can take focus
        FocusTextOf(AddressBox);
    }

    private void ExitAddressEdit()
    {
        _suggestCts?.Cancel();
        AddressBox.IsSuggestionListOpen = false;
        AddressBox.Visibility = Visibility.Collapsed;
        Crumbs.Visibility = Visibility.Visible;
    }

    private async void OnAddressSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string text = args.ChosenSuggestion as string ?? args.QueryText;
        ExitAddressEdit();
        await ViewModel.NavigateToTypedPathAsync(text);
        FocusList();
    }

    private async void OnAddressTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        string text = sender.Text;
        try
        {
            await Task.Delay(120, cts.Token); // debounce keystrokes
            var suggestions = await ViewModel.GetPathSuggestionsAsync(text, cts.Token);
            if (!cts.IsCancellationRequested) sender.ItemsSource = suggestions;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ExitAddressEdit();
            FocusList();
        }
    }

    private void OnAddressLostFocus(object sender, RoutedEventArgs e)
    {
        if (!AddressBox.IsSuggestionListOpen) ExitAddressEdit();
    }

    private async void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbSegment segment && ViewModel.Provider is { } provider)
            await ViewModel.NavigateAsync(new StorageLocation(provider.Id, segment.Path));
    }

    // ------------------------------------------------------------------ filter / search

    private void OnFilterFocusRequested(object? sender, EventArgs e)
    {
        FocusTextOf(FilterBox);
    }

    private async void OnFilterSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        sender.IsSuggestionListOpen = false;
        if (args.ChosenSuggestion is string recent && recent != ViewModel.FilterText) ViewModel.FilterText = recent;
        await ViewModel.StartSearchAsync();
    }

    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (FilterBox.IsSuggestionListOpen)
                {
                    FilterBox.IsSuggestionListOpen = false;
                }
                else if (ViewModel.FilterText.Length > 0 || ViewModel.IsSearchMode || ViewModel.HasSearchFilters)
                {
                    ViewModel.ClearSearch();
                }
                else
                {
                    FocusList();
                }
                break;
            case VirtualKey.Down when !FilterBox.IsSuggestionListOpen:
                // Keep hands on the keyboard: arrow down moves from the search box into the results.
                e.Handled = true;
                if (ViewModel.Items.Count > 0 && ActiveList?.SelectedItem is null)
                    OnSelectionRequested(this, [ViewModel.Items[0]]);
                else
                    FocusList();
                break;
        }
    }

    private static void FocusTextOf(AutoSuggestBox host)
    {
        if (FindDescendant<TextBox>(host) is { } box)
        {
            box.Focus(FocusState.Keyboard);
            box.SelectAll();
        }
        else
        {
            host.Focus(FocusState.Keyboard);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
