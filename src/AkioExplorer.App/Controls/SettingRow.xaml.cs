using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AkioExplorer.App.Controls;

/// <summary>Windows 11 Settings style row: icon, title, description, and a control on the right.</summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingRow : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(nameof(Action), typeof(object), typeof(SettingRow), new PropertyMetadata(null, OnChanged));

    public SettingRow()
    {
        InitializeComponent();
        Refresh();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width < StackedWidth);
    }

    /// <summary>Narrower rows put the control under the text, so the text keeps a readable width.</summary>
    private const double StackedWidth = 480;

    private bool _stacked;

    private void Arrange(bool stacked)
    {
        if (stacked == _stacked) return;
        _stacked = stacked;
        Grid.SetRow(ActionPresenter, stacked ? 1 : 0);
        Grid.SetColumn(ActionPresenter, stacked ? 1 : 2);
        ActionPresenter.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ActionPresenter.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        Grid.SetRowSpan(Icon, stacked ? 2 : 1);
        Icon.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;
    }

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingRow)d).Refresh();

    private void Refresh()
    {
        if (HeaderText is null) return;
        HeaderText.Text = Header ?? string.Empty;
        DescriptionText.Text = Description ?? string.Empty;
        DescriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        Icon.Glyph = Glyph ?? string.Empty;
        Icon.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        ActionPresenter.Content = Action;
    }
}
