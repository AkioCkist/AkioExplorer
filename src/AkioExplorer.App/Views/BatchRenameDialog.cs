using AkioExplorer.App.Services;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AkioExplorer.App.Views;

/// <summary>
/// Multi-rename (Total Commander / OneCommander style) with a live preview. Every change re-plans the names, so problems
/// (duplicates, invalid characters, bad regex) show next to the item before anything is renamed (error prevention).
/// </summary>
public static class BatchRenameDialog
{
    private static BatchRenameOptions _last = new();

    /// <summary>Returns the plan to apply, or null when cancelled.</summary>
    public static async Task<IReadOnlyList<RenamePlanEntry>?> ShowAsync(
        DialogService dialogs, IReadOnlyList<StorageItem> items, IReadOnlyList<string> siblingNames, string parentName)
    {
        var start = _last;

        var nameMask = new TextBox { Text = start.NameMask, PlaceholderText = "[N]" };
        var extMask = new TextBox { Text = start.ExtensionMask, PlaceholderText = "[E]", Width = 120 };
        var find = new TextBox { Text = start.Find, PlaceholderText = "Find" };
        var replace = new TextBox { Text = start.Replace, PlaceholderText = "Replace with" };
        var regex = new CheckBox { Content = "Regular expression", IsChecked = start.UseRegex };
        var matchCase = new CheckBox { Content = "Match case", IsChecked = start.MatchCase };
        var caseBox = new ComboBox { MinWidth = 160 };
        foreach (var label in new[] { "Keep case", "lowercase", "UPPERCASE", "Title Case", "Sentence case" }) caseBox.Items.Add(label);
        caseBox.SelectedIndex = (int)start.Case;
        var counterStart = Number(start.CounterStart, "Start");
        var counterStep = Number(start.CounterStep, "Step");
        var counterDigits = Number(start.CounterDigits, "Digits", 1, 12);
        AutomationProperties.SetName(nameMask, "Name mask");
        AutomationProperties.SetName(extMask, "Extension mask");
        AutomationProperties.SetName(find, "Find");
        AutomationProperties.SetName(replace, "Replace with");
        AutomationProperties.SetName(caseBox, "Change case");

        // Token buttons insert at the caret of the name mask (recognition over recall).
        var tokens = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (token, tip) in new[]
                 {
                     ("[N]", "Original name (without extension)"), ("[N1-3]", "Characters 1 to 3 of the name"),
                     ("[E]", "Extension"), ("[C]", "Counter"), ("[P]", "Parent folder name"),
                     ("[YMD]", "Date modified, 2026-11-22"), ("[hms]", "Time modified, 14-05-09"),
                 })
        {
            var button = new Button { Content = token, Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
            ToolTipService.SetToolTip(button, tip);
            button.Click += (_, _) =>
            {
                int at = nameMask.SelectionStart;
                nameMask.Text = nameMask.Text.Remove(at, nameMask.SelectionLength).Insert(at, token);
                nameMask.SelectionStart = at + token.Length;
                nameMask.Focus(FocusState.Programmatic);
            };
            tokens.Children.Add(button);
        }

        var preview = new ListView { Height = 240, SelectionMode = ListViewSelectionMode.None, Margin = new Thickness(-12, 0, -12, 0) };
        AutomationProperties.SetName(preview, "Preview");
        var summary = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap };

        var form = new StackPanel { Spacing = 10, Width = 620 };
        form.Children.Add(Labeled("Name", Columns(nameMask, Labeled("Extension", extMask))));
        form.Children.Add(tokens);
        form.Children.Add(Labeled("Find and replace", Columns(find, replace)));
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        flags.Children.Add(regex);
        flags.Children.Add(matchCase);
        form.Children.Add(flags);
        var counter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        counter.Children.Add(Labeled("Case", caseBox));
        counter.Children.Add(Labeled("Counter start", counterStart));
        counter.Children.Add(Labeled("Step", counterStep));
        counter.Children.Add(Labeled("Digits", counterDigits));
        form.Children.Add(counter);
        form.Children.Add(new TextBlock { Text = "Preview", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 6, 0, 0) });
        form.Children.Add(preview);
        form.Children.Add(summary);

        var dialog = new ContentDialog
        {
            Title = items.Count == 1 ? $"Rename \"{items[0].Name}\"" : $"Rename {items.Count:N0} items",
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Rename",
            SecondaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;

        IReadOnlyList<RenamePlanEntry> plan = [];

        BatchRenameOptions Read() => new()
        {
            NameMask = nameMask.Text,
            ExtensionMask = extMask.Text,
            Find = find.Text,
            Replace = replace.Text,
            UseRegex = regex.IsChecked == true,
            MatchCase = matchCase.IsChecked == true,
            Case = (RenameCase)Math.Max(0, caseBox.SelectedIndex),
            CounterStart = Int(counterStart, 1),
            CounterStep = Int(counterStep, 1),
            CounterDigits = Math.Clamp(Int(counterDigits, 1), 1, 12),
        };

        void Update()
        {
            plan = BatchRenamer.Plan(items, Read(), siblingNames, parentName);
            preview.Items.Clear();
            foreach (var entry in plan.Take(500)) preview.Items.Add(PreviewRow(entry));
            int changes = plan.Count(p => p.IsChange);
            int errors = plan.Count(p => p.Error is not null);
            summary.Text = errors > 0
                ? $"{errors:N0} {(errors == 1 ? "name has" : "names have")} a problem – fix them to continue."
                : changes == 0 ? "Nothing changes yet." : $"{changes:N0} of {plan.Count:N0} items will be renamed. Ctrl+Z undoes it.";
            summary.Foreground = (Brush)Application.Current.Resources[errors > 0 ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
            dialog.IsPrimaryButtonEnabled = errors == 0 && changes > 0;
            dialog.PrimaryButtonText = changes > 0 ? $"Rename {changes:N0}" : "Rename";
        }

        foreach (var box in new[] { nameMask, extMask, find, replace }) box.TextChanged += (_, _) => Update();
        regex.Click += (_, _) => Update();
        matchCase.Click += (_, _) => Update();
        caseBox.SelectionChanged += (_, _) => Update();
        foreach (var n in new[] { counterStart, counterStep, counterDigits }) n.ValueChanged += (_, _) => Update();

        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var d = new BatchRenameOptions();
            nameMask.Text = d.NameMask;
            extMask.Text = d.ExtensionMask;
            find.Text = replace.Text = string.Empty;
            regex.IsChecked = matchCase.IsChecked = false;
            caseBox.SelectedIndex = 0;
            counterStart.Value = counterStep.Value = counterDigits.Value = 1;
            Update();
        };
        dialog.Opened += (_, _) =>
        {
            nameMask.Focus(FocusState.Programmatic);
            nameMask.SelectAll();
        };

        Update();
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        _last = Read();
        return plan;
    }

    private static FrameworkElement PreviewRow(RenamePlanEntry entry)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var old = new TextBlock { Text = entry.Item.Name, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = secondary };
        var arrow = new FontIcon { Glyph = "", FontSize = 12, Foreground = secondary };
        var stack = new StackPanel();
        var name = new TextBlock
        {
            Text = entry.NewName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = entry.IsChange ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        stack.Children.Add(name);
        if (entry.Error is { } error)
        {
            var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            name.Foreground = critical;
            stack.Children.Add(new TextBlock { Text = error, Foreground = critical, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        }
        ToolTipService.SetToolTip(grid, $"{entry.Item.Name} → {entry.NewName}");
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(stack, 2);
        grid.Children.Add(old);
        grid.Children.Add(arrow);
        grid.Children.Add(stack);
        return grid;
    }

    private static NumberBox Number(int value, string name, int min = int.MinValue, int max = int.MaxValue)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            Width = 96,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static int Int(NumberBox box, int fallback) => double.IsNaN(box.Value) ? fallback : (int)Math.Round(box.Value);

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        stack.Children.Add(control);
        return stack;
    }

    private static Grid Columns(FrameworkElement first, FrameworkElement second)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = second is TextBox ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetColumn(second, 1);
        grid.Children.Add(first);
        grid.Children.Add(second);
        return grid;
    }
}
