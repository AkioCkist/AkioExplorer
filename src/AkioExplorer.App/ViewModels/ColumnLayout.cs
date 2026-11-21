using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace AkioExplorer.App.ViewModels;

/// <summary>
/// Details-view column widths shared by every row of one pane. Narrow panes drop the least important
/// columns first so the Name column always stays readable (dual-pane on a laptop screen).
/// </summary>
public sealed partial class ColumnLayout : ObservableObject
{
    private static readonly GridLength Hidden = new(0);

    [ObservableProperty] private GridLength _dateWidth = new(150);
    [ObservableProperty] private GridLength _typeWidth = new(150);
    [ObservableProperty] private GridLength _sizeWidth = new(110);

    public void Update(double paneWidth)
    {
        if (paneWidth >= 820)
        {
            Set(new GridLength(150), new GridLength(150), new GridLength(110));
        }
        else if (paneWidth >= 640)
        {
            Set(new GridLength(140), new GridLength(120), new GridLength(96));
        }
        else if (paneWidth >= 500)
        {
            Set(new GridLength(140), Hidden, new GridLength(90));
        }
        else
        {
            Set(Hidden, Hidden, new GridLength(84));
        }
    }

    private void Set(GridLength date, GridLength type, GridLength size)
    {
        if (DateWidth != date) DateWidth = date;
        if (TypeWidth != type) TypeWidth = type;
        if (SizeWidth != size) SizeWidth = size;
    }
}
