using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class SettingsViewV2 : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(SettingsUiState),
        typeof(SettingsViewV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(AdaptiveLayoutMode),
        typeof(SettingsViewV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, LayoutModeChanged));

    public SettingsViewV2()
    {
        InitializeComponent();
        Loaded += SettingsViewV2_Loaded;
        Unloaded += SettingsViewV2_Unloaded;
    }

    public SettingsUiState? State
    {
        get => (SettingsUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public AdaptiveLayoutMode LayoutMode
    {
        get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    internal ScrollViewer ScrollHost => SettingsScrollViewer;

    private static void LayoutModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((SettingsViewV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void SettingsViewV2_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout(LayoutMode);
    }

    private void SettingsViewV2_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsScrollViewer.ScrollToTop();
    }

    private void ApplyLayout(AdaptiveLayoutMode mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        ContentFrame.MaxWidth = mode switch
        {
            AdaptiveLayoutMode.Wide => 1220,
            AdaptiveLayoutMode.Compact => 1120,
            _ => 920
        };
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 28, 34, 38),
            AdaptiveLayoutMode.Compact => new Thickness(28, 24, 28, 34),
            _ => new Thickness(22, 22, 22, 32)
        };
        PageTitle.FontSize = mode switch
        {
            AdaptiveLayoutMode.Wide => 32,
            AdaptiveLayoutMode.Compact => 30,
            _ => 28
        };

        bool stacked = mode == AdaptiveLayoutMode.Stacked;
        PrimaryColumn.Width = new GridLength(stacked ? 1 : 3, GridUnitType.Star);
        ColumnGap.Width = new GridLength(stacked ? 0 : 20);
        SecondaryColumn.Width = new GridLength(stacked ? 0 : 2, GridUnitType.Star);
        Grid.SetRow(SecondarySettingsColumn, stacked ? 1 : 0);
        Grid.SetColumn(SecondarySettingsColumn, stacked ? 0 : 2);
        SecondarySettingsColumn.Margin = stacked
            ? new Thickness(0, 16, 0, 0)
            : new Thickness(0);
    }
}
