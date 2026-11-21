using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AkioExplorer.App.Services;

/// <summary>
/// Conflict dialog. Shows both files side by side (size, date, which is newer) so the choice is informed,
/// defaults to the non-destructive "Keep both", Esc = Skip, and offers "apply to all remaining".
/// </summary>
public sealed class ConflictResolver : IConflictResolver
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DialogService _dialogs;

    public ConflictResolver(DispatcherQueue dispatcher, DialogService dialogs)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
    }

    public Task<ConflictDecision> ResolveAsync(FileOperation operation, ConflictInfo conflict, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return;
                tcs.TrySetResult(await ShowAsync(operation, conflict));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private async Task<ConflictDecision> ShowAsync(FileOperation operation, ConflictInfo conflict)
    {
        var applyAll = new CheckBox
        {
            Content = $"Do this for the remaining {conflict.RemainingItems:N0} conflicts",
            Visibility = conflict.RemainingItems > 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var body = new StackPanel { Spacing = 12, MaxWidth = 460 };
        body.Children.Add(new TextBlock
        {
            Text = $"\"{conflict.DestinationFolderName}\" already has a file named \"{conflict.Source.Name}\".",
            TextWrapping = TextWrapping.Wrap,
        });

        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bool sourceNewer = (conflict.Source.Modified ?? default) > (conflict.Existing.Modified ?? default);
        AddRow(grid, 0, operation.Kind == OperationKind.Copy ? "Copying" : "Moving", conflict.Source, sourceNewer);
        AddRow(grid, 1, "Existing", conflict.Existing, !sourceNewer && conflict.Source.Modified != conflict.Existing.Modified);
        body.Children.Add(grid);
        body.Children.Add(applyAll);

        var dialog = new ContentDialog
        {
            Title = "Replace or keep both?",
            Content = body,
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Keep both",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Secondary,
        };

        var result = await _dialogs.ShowAsync(dialog);
        var resolution = result switch
        {
            ContentDialogResult.Primary => ConflictResolution.Replace,
            ContentDialogResult.Secondary => ConflictResolution.KeepBoth,
            _ => ConflictResolution.Skip,
        };
        return new ConflictDecision(resolution, applyAll.IsChecked == true);
    }

    private static void AddRow(Grid grid, int row, string label, StorageItem item, bool newer)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        string size = item.Size is { } s ? Formatting.FormatSize(s) : "—";
        string date = item.Modified?.LocalDateTime.ToString("g") ?? "—";
        var detail = new TextBlock { Text = $"{size}  ·  {date}{(newer ? "  ·  newer" : string.Empty)}", TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(header, row);
        Grid.SetRow(detail, row);
        Grid.SetColumn(detail, 1);
        grid.Children.Add(header);
        grid.Children.Add(detail);
    }
}
