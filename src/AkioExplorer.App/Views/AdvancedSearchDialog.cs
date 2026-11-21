using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AkioExplorer.App.Views;

/// <summary>Which field to open the advanced search with (the chips' "Custom…" entries jump straight to it).</summary>
public enum AdvancedSearchFocus
{
    Name,
    CustomDate,
    CustomSize,
    Owner,
}

/// <summary>Every search option in one form, like Google Drive's "Advanced search". Only options this location supports are shown.</summary>
public static class AdvancedSearchDialog
{
    private const double Mb = 1024 * 1024;

    /// <summary>Returns the query to run, or null when cancelled.</summary>
    public static async Task<SearchQuery?> ShowAsync(DialogService dialogs, FilePaneViewModel pane, AdvancedSearchFocus focus = AdvancedSearchFocus.Name)
    {
        var start = pane.CurrentQuery;
        if (focus == AdvancedSearchFocus.CustomDate) start = start with { Modified = DateFilter.Custom };
        if (focus == AdvancedSearchFocus.CustomSize) start = start with { Size = SizeFilter.Custom };
        if (focus == AdvancedSearchFocus.Owner) start = start with { Owner = OwnerFilter.Person };
        var today = DateTime.Today;

        // ---------------------------------------------------------------- fields
        var type = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var category in SearchLabels.Categories) type.Items.Add(SearchLabels.Category(category));

        var name = new TextBox { PlaceholderText = "Part of the name, or a pattern like *.psd" };
        var words = new TextBox { PlaceholderText = "Words found in the file" };

        var owner = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var o in SearchLabels.Owners) owner.Items.Add(SearchLabels.Owner(o));
        var ownerEmail = new TextBox { PlaceholderText = "Owner's name or email address", InputScope = EmailScope() };
        AutomationProperties.SetName(ownerEmail, "Owner email");
        var ownerCell = new StackPanel { Spacing = 8 };
        ownerCell.Children.Add(owner);
        ownerCell.Children.Add(ownerEmail);

        var scopeOptions = pane.SearchScopeOptions;
        var scope = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in scopeOptions) scope.Items.Add(option.Label);
        var inTrash = new CheckBox { Content = "In trash" };
        var starred = new CheckBox { Content = "Starred" };
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        if (pane.SupportsTrashSearch) flags.Children.Add(inTrash);
        if (pane.SupportsStarredSearch) flags.Children.Add(starred);
        var locationCell = new StackPanel { Spacing = 8 };
        if (scopeOptions.Count > 1) locationCell.Children.Add(scope);
        if (flags.Children.Count > 0) locationCell.Children.Add(flags);

        var sharedWith = new TextBox { PlaceholderText = "Enter a name or email address", InputScope = EmailScope() };

        var date = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var preset in SearchLabels.DatePresets) date.Items.Add(SearchLabels.Date(preset, start, today));
        date.Items.Add("Custom range");
        var from = new CalendarDatePicker { PlaceholderText = "From", HorizontalAlignment = HorizontalAlignment.Stretch };
        var to = new CalendarDatePicker { PlaceholderText = "To", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(from, "Modified from");
        AutomationProperties.SetName(to, "Modified to");
        var dateRange = Pair(from, to);

        var size = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var preset in SearchLabels.SizePresets) size.Items.Add(SearchLabels.Size(preset, start));
        size.Items.Add("Custom size");
        var minSize = new NumberBox { PlaceholderText = "At least (MB)", Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var maxSize = new NumberBox { PlaceholderText = "At most (MB)", Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        AutomationProperties.SetName(minSize, "Minimum size in megabytes");
        AutomationProperties.SetName(maxSize, "Maximum size in megabytes");
        var sizeRange = Pair(minSize, maxSize);

        void Load(SearchQuery q)
        {
            type.SelectedIndex = Array.IndexOf(SearchLabels.Categories, q.Category);
            owner.SelectedIndex = Array.IndexOf(SearchLabels.Owners, q.Owner);
            ownerEmail.Text = q.OwnerEmail;
            sharedWith.Text = q.SharedWith;
            inTrash.IsChecked = q.InTrash;
            starred.IsChecked = q.Starred;
            name.Text = q.Name;
            words.Text = q.Content;
            int scopeIndex = scopeOptions.ToList().FindIndex(o => o.Scope == q.Scope);
            scope.SelectedIndex = Math.Max(0, scopeIndex);
            date.SelectedIndex = q.Modified == DateFilter.Custom ? SearchLabels.DatePresets.Length : Array.IndexOf(SearchLabels.DatePresets, q.Modified);
            from.Date = q.ModifiedFrom is { } f ? new DateTimeOffset(f) : null;
            to.Date = q.ModifiedTo is { } t ? new DateTimeOffset(t) : null;
            size.SelectedIndex = q.Size == SizeFilter.Custom ? SearchLabels.SizePresets.Length : Array.IndexOf(SearchLabels.SizePresets, q.Size);
            minSize.Value = q.MinSize is { } min ? Math.Round(min / Mb, 2) : double.NaN;
            maxSize.Value = q.MaxSize is { } max ? Math.Round(max / Mb, 2) : double.NaN;
        }

        void SyncCustomRows()
        {
            dateRange.Visibility = date.SelectedIndex == SearchLabels.DatePresets.Length ? Visibility.Visible : Visibility.Collapsed;
            sizeRange.Visibility = size.SelectedIndex == SearchLabels.SizePresets.Length ? Visibility.Visible : Visibility.Collapsed;
            ownerEmail.Visibility = owner.SelectedIndex == Array.IndexOf(SearchLabels.Owners, OwnerFilter.Person) ? Visibility.Visible : Visibility.Collapsed;
        }

        owner.SelectionChanged += (_, _) =>
        {
            SyncCustomRows();
            if (ownerEmail.Visibility == Visibility.Visible) ownerEmail.Focus(FocusState.Programmatic);
        };

        date.SelectionChanged += (_, _) => SyncCustomRows();
        size.SelectionChanged += (_, _) => SyncCustomRows();

        // ---------------------------------------------------------------- layout
        var form = new Grid { ColumnSpacing = 16, RowSpacing = 12, MinWidth = 460 };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Row(string label, FrameworkElement control, string? help = null)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            Grid.SetRow(text, row);
            form.Children.Add(text);
            AutomationProperties.SetName(control, label);

            FrameworkElement cell = control;
            if (help is not null)
            {
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(control);
                stack.Children.Add(new TextBlock
                {
                    Text = help,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                cell = stack;
            }
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, 1);
            form.Children.Add(cell);
        }

        void Extra(FrameworkElement control)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }

        // Same order as Google Drive's form; rows the current location can't answer are left out.
        Row("Type", type);
        if (pane.SupportsSharingSearch) Row("Owner", ownerCell);
        if (pane.SupportsContentSearch) Row("Has the words", words, "Searches inside documents, PDFs and other text files.");
        Row("Item name", name);
        if (locationCell.Children.Count > 0) Row("Location", locationCell);
        Row("Date modified", date);
        Extra(dateRange);
        Row("Size", size, "Size filters match files only.");
        Extra(sizeRange);
        if (pane.SupportsSharingSearch) Row("Shared with", sharedWith, "Items you shared with this person (they can view or edit).");

        Load(start);
        SyncCustomRows();

        var dialog = new ContentDialog
        {
            Title = "Advanced search",
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Search",
            SecondaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Reset clears the form but keeps the dialog open (like Drive).
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            Load(SearchQuery.Empty);
            SyncCustomRows();
            name.Focus(FocusState.Programmatic);
        };

        dialog.Opened += (_, _) =>
        {
            Control target = focus switch
            {
                AdvancedSearchFocus.CustomDate => from,
                AdvancedSearchFocus.CustomSize => minSize,
                AdvancedSearchFocus.Owner => ownerEmail,
                _ => name,
            };
            target.Focus(FocusState.Programmatic);
        };

        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;

        var query = new SearchQuery
        {
            Name = name.Text.Trim(),
            Content = pane.SupportsContentSearch ? words.Text.Trim() : string.Empty,
            Category = type.SelectedIndex >= 0 ? SearchLabels.Categories[type.SelectedIndex] : FileCategory.Any,
            Scope = scope.SelectedIndex > 0 && scope.SelectedIndex < scopeOptions.Count ? scopeOptions[scope.SelectedIndex].Scope : SearchScope.CurrentFolder,
            Owner = pane.SupportsSharingSearch && owner.SelectedIndex >= 0 ? SearchLabels.Owners[owner.SelectedIndex] : OwnerFilter.Anyone,
            OwnerEmail = ownerEmail.Text.Trim(),
            SharedWith = pane.SupportsSharingSearch ? sharedWith.Text.Trim() : string.Empty,
            InTrash = pane.SupportsTrashSearch && inTrash.IsChecked == true,
            Starred = pane.SupportsStarredSearch && starred.IsChecked == true,
        };
        if (query.Owner != OwnerFilter.Person) query = query with { OwnerEmail = string.Empty };

        if (date.SelectedIndex == SearchLabels.DatePresets.Length)
        {
            DateTime? f = from.Date?.Date, t = to.Date?.Date;
            if (f is { } a && t is { } b && a > b) (f, t) = (t, f); // forgive a reversed range
            query = f is null && t is null ? query : query with { Modified = DateFilter.Custom, ModifiedFrom = f, ModifiedTo = t };
        }
        else if (date.SelectedIndex > 0)
        {
            query = query with { Modified = SearchLabels.DatePresets[date.SelectedIndex] };
        }

        if (size.SelectedIndex == SearchLabels.SizePresets.Length)
        {
            long? min = double.IsNaN(minSize.Value) ? null : (long)(minSize.Value * Mb);
            long? max = double.IsNaN(maxSize.Value) ? null : (long)(maxSize.Value * Mb);
            if (min is { } a && max is { } b && a > b) (min, max) = (max, min);
            query = min is null && max is null ? query : query with { Size = SizeFilter.Custom, MinSize = min, MaxSize = max };
        }
        else if (size.SelectedIndex > 0)
        {
            query = query with { Size = SearchLabels.SizePresets[size.SelectedIndex] };
        }

        return query;
    }

    private static InputScope EmailScope()
    {
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.EmailNameOrAddress));
        return scope;
    }

    private static Grid Pair(FrameworkElement first, FrameworkElement second)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(second, 1);
        grid.Children.Add(first);
        grid.Children.Add(second);
        return grid;
    }
}
