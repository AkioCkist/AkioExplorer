using AkioExplorer.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AkioExplorer.App.Views;

/// <summary>Transfer queue (live jobs with pause/resume/cancel) and recent transfers (history with retry).</summary>
public sealed partial class TransfersView : UserControl
{
    public TransfersView()
    {
        Operations = App.Services.GetRequiredService<OperationsViewModel>();
        History = App.Services.GetRequiredService<TransferHistoryViewModel>();
        InitializeComponent();
    }

    public OperationsViewModel Operations { get; }

    public TransferHistoryViewModel History { get; }

    public void ShowQueue() => Tabs.SelectedItem = QueueTab;

    public void ShowHistory() => Tabs.SelectedItem = HistoryTab;

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool queue = sender.SelectedItem == QueueTab;
        QueuePanel.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = queue ? Visibility.Collapsed : Visibility.Visible;
        PauseAllButton.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        ResumeAllButton.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = queue ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRetryRecord(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TransferRecordViewModel record })
        {
            History.Retry(record);
            ShowQueue();
        }
    }

    private void OnRemoveRecord(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TransferRecordViewModel record }) History.Remove(record);
    }
}
