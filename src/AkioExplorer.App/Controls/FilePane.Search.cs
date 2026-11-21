using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.App.Views;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Google Drive style search: filter chips (Type, Modified, Size, Location), recent searches under the box and the
/// advanced search form. Picking a chip value searches at once; the chips stay visible while any of them is set.
/// </summary>
public sealed partial class FilePane
{
    private bool _searchFocused;
    private bool _chipMenuOpen;

    private void BuildSearchChips()
    {
        TypeChip.Flyout = ChipMenu(menu =>
        {
            var current = ViewModel.SearchFilters.Category;
            foreach (var category in SearchLabels.Categories)
            {
                var item = Radio(menu, "type", SearchLabels.Category(category), category == current,
                    q => q with { Category = category });
                item.Icon = new FontIcon { Glyph = SearchLabels.CategoryGlyph(category) };
                if (category == FileCategory.Any) menu.Items.Add(new MenuFlyoutSeparator());
            }
        });

        PeopleChip.Flyout = ChipMenu(menu =>
        {
            var q = ViewModel.SearchFilters;
            foreach (var owner in new[] { OwnerFilter.Anyone, OwnerFilter.Me, OwnerFilter.NotMe })
            {
                Radio(menu, "owner", SearchLabels.Owner(owner), owner == q.EffectiveOwner,
                    x => x with { Owner = owner, OwnerEmail = string.Empty });
                if (owner == OwnerFilter.Anyone) menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var person = new RadioMenuFlyoutItem
            {
                Text = q.EffectiveOwner == OwnerFilter.Person ? $"{SearchLabels.Owner(OwnerFilter.Person, q.OwnerEmail)}…" : "Specific person…",
                GroupName = "owner",
                IsChecked = q.EffectiveOwner == OwnerFilter.Person,
                Icon = new FontIcon { Glyph = "" },
            };
            person.Click += (_, _) => _ = OpenAdvancedSearchAsync(AdvancedSearchFocus.Owner);
            menu.Items.Add(person);
        });

        ModifiedChip.Flyout = ChipMenu(menu =>
        {
            var q = ViewModel.SearchFilters;
            foreach (var preset in SearchLabels.DatePresets)
            {
                Radio(menu, "date", SearchLabels.Date(preset, q, DateTime.Today), preset == q.Modified,
                    x => x with { Modified = preset, ModifiedFrom = null, ModifiedTo = null });
                if (preset == DateFilter.Any) menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var custom = new RadioMenuFlyoutItem
            {
                Text = q.Modified == DateFilter.Custom ? $"{SearchLabels.Date(DateFilter.Custom, q, DateTime.Today)}…" : "Custom range…",
                GroupName = "date",
                IsChecked = q.Modified == DateFilter.Custom,
            };
            custom.Click += (_, _) => _ = OpenAdvancedSearchAsync(AdvancedSearchFocus.CustomDate);
            menu.Items.Add(custom);
        });

        SizeChip.Flyout = ChipMenu(menu =>
        {
            var q = ViewModel.SearchFilters;
            foreach (var preset in SearchLabels.SizePresets)
            {
                Radio(menu, "size", SearchLabels.Size(preset, q), preset == q.Size,
                    x => x with { Size = preset, MinSize = null, MaxSize = null });
                if (preset == SizeFilter.Any) menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var custom = new RadioMenuFlyoutItem
            {
                Text = q.Size == SizeFilter.Custom ? $"{SearchLabels.Size(SizeFilter.Custom, q)}…" : "Custom size…",
                GroupName = "size",
                IsChecked = q.Size == SizeFilter.Custom,
            };
            custom.Click += (_, _) => _ = OpenAdvancedSearchAsync(AdvancedSearchFocus.CustomSize);
            menu.Items.Add(custom);
        });

        ScopeChip.Flyout = ChipMenu(menu =>
        {
            var scope = ViewModel.SearchFilters.Scope;
            foreach (var (label, option) in ViewModel.SearchScopeOptions)
            {
                Radio(menu, "scope", label, scope == option, q => q with { Scope = option }).Icon = new FontIcon
                {
                    Glyph = option switch { SearchScope.SharedWithMe => "", SearchScope.Everywhere => "", _ => "" },
                };
            }
            if (ViewModel.SupportsStarredSearch || ViewModel.SupportsTrashSearch) menu.Items.Add(new MenuFlyoutSeparator());
            if (ViewModel.SupportsStarredSearch) Toggle(menu, "Starred", "", ViewModel.SearchFilters.Starred, (q, on) => q with { Starred = on });
            if (ViewModel.SupportsTrashSearch) Toggle(menu, "In trash", "", ViewModel.SearchFilters.InTrash, (q, on) => q with { InTrash = on });
        });
    }

    /// <summary>A menu rebuilt each time it opens, so it always reflects the current filters and folder.</summary>
    private MenuFlyout ChipMenu(Action<MenuFlyout> fill)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        menu.Opening += (_, _) =>
        {
            _chipMenuOpen = true;
            menu.Items.Clear();
            if (ViewModel is not null) fill(menu);
        };
        menu.Closed += (_, _) =>
        {
            _chipMenuOpen = false;
            DispatcherQueue.TryEnqueue(RefreshSearchFocus);
        };
        return menu;
    }

    private RadioMenuFlyoutItem Radio(MenuFlyout menu, string group, string text, bool isChecked, Func<SearchQuery, SearchQuery> change)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = isChecked };
        item.Click += async (_, _) => await ViewModel.ApplySearchAsync(change(ViewModel.CurrentQuery));
        menu.Items.Add(item);
        return item;
    }

    private void Toggle(MenuFlyout menu, string text, string glyph, bool isChecked, Func<SearchQuery, bool, SearchQuery> change)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) => await ViewModel.ApplySearchAsync(change(ViewModel.CurrentQuery, item.IsChecked));
        menu.Items.Add(item);
    }

    /// <summary>Chip labels, highlight of the set ones, and whether the chip row shows at all.</summary>
    private void UpdateSearchChips()
    {
        if (ViewModel is not { } vm) return;
        var q = vm.SearchFilters;

        SetChip(TypeChip, SearchLabels.TypeChip(q), q.Category != FileCategory.Any);
        SetChip(ModifiedChip, SearchLabels.ModifiedChip(q, DateTime.Today), q.Modified != DateFilter.Any);
        SetChip(SizeChip, SearchLabels.SizeChip(q), q.Size != SizeFilter.Any);
        SetChip(PeopleChip, SearchLabels.PeopleChip(q), q.EffectiveOwner != OwnerFilter.Anyone || q.SharedWithText.Length > 0);
        PeopleChip.Visibility = vm.SupportsSharingSearch ? Visibility.Visible : Visibility.Collapsed;

        // Location chip: where to look, plus Starred / In trash where the service has them.
        var options = vm.SearchScopeOptions;
        var current = options.FirstOrDefault(o => o.Scope == q.Scope);
        string where = q.Scope == SearchScope.CurrentFolder
            ? (options.Count > 0 && options[0].Label.StartsWith("Anywhere", StringComparison.Ordinal) ? "Location" : "This folder")
            : current.Label ?? "Location";
        var extras = new List<string>();
        if (q.Starred) extras.Add("Starred");
        if (q.InTrash) extras.Add("In trash");
        if (extras.Count > 0) where = q.Scope == SearchScope.CurrentFolder ? string.Join(", ", extras) : $"{where}, {string.Join(", ", extras)}";
        SetChip(ScopeChip, where, q.Scope != SearchScope.CurrentFolder || q.Starred || q.InTrash);
        ScopeChip.Visibility = options.Count > 1 || vm.SupportsStarredSearch || vm.SupportsTrashSearch || q.Scope != SearchScope.CurrentFolder
            ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.Visibility = q.HasFilters ? Visibility.Visible : Visibility.Collapsed;

        bool show = vm.CanSearch &&
            (vm.IsSearchMode || q.HasFilters || vm.FilterText.Length > 0 || _searchFocused || _chipMenuOpen);
        SearchChipsHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetChip(DropDownButton chip, string text, bool active)
    {
        chip.Content = text;
        if (active)
        {
            chip.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            chip.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
        else
        {
            chip.ClearValue(Control.ForegroundProperty);
            chip.ClearValue(Control.BorderBrushProperty);
        }
    }

    // ------------------------------------------------------------------ focus: the chip row follows the search box

    private void OnFilterGotFocus(object sender, RoutedEventArgs e)
    {
        _searchFocused = true;
        UpdateSearchChips();
        if (ViewModel.FilterText.Length == 0) ShowRecentSearches();
    }

    private void OnChipsGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        _searchFocused = true;
    }

    private void OnSearchAreaLostFocus(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(RefreshSearchFocus);

    /// <summary>Focus may have moved into a chip or its menu (a popup outside this tree): only hide when it truly left.</summary>
    private void RefreshSearchFocus()
    {
        if (XamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        _searchFocused = _chipMenuOpen || IsWithin(focused, FilterBox) || IsWithin(focused, SearchChips);
        UpdateSearchChips();
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (ReferenceEquals(e, ancestor)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ recent searches

    private void ShowRecentSearches()
    {
        var recent = ViewModel.RecentSearches(ViewModel.FilterText);
        FilterBox.ItemsSource = recent;
        FilterBox.IsSuggestionListOpen = recent.Count > 0;
    }

    private void OnFilterTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ShowRecentSearches();
    }

    private void OnRecentSearchChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        // UpdateTextOnSelect puts the term in the box; QuerySubmitted (Enter / click) runs the search.
    }

    // ------------------------------------------------------------------ advanced search & clear

    private void OnAdvancedSearchClick(object sender, RoutedEventArgs e) => _ = OpenAdvancedSearchAsync(AdvancedSearchFocus.Name);

    private void OnAdvancedSearchRequested(object? sender, EventArgs e) => _ = OpenAdvancedSearchAsync(AdvancedSearchFocus.Name);

    private async Task OpenAdvancedSearchAsync(AdvancedSearchFocus focus)
    {
        if (ViewModel is not { CanSearch: true } vm) return;
        FilterBox.IsSuggestionListOpen = false;
        var dialogs = App.Services.GetRequiredService<DialogService>();
        if (await AdvancedSearchDialog.ShowAsync(dialogs, vm, focus) is { } query)
        {
            await vm.ApplySearchAsync(query);
            FocusList();
        }
    }

    /// <summary>Drops every filter but keeps the typed name (still searched if there is one).</summary>
    private async void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        FocusTextOf(FilterBox);
        await ViewModel.ApplySearchAsync(new SearchQuery { Name = ViewModel.FilterText });
    }
}
