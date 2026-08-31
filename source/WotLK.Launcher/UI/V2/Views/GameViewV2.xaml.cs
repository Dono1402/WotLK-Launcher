using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class GameViewV2 : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(GameUiState),
        typeof(GameViewV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(AdaptiveLayoutMode),
        typeof(GameViewV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, LayoutModeChanged));

    public static readonly DependencyProperty DashboardStateProperty = DependencyProperty.Register(
        nameof(DashboardState),
        typeof(DashboardUiState),
        typeof(GameViewV2),
        new PropertyMetadata(null));

    public GameViewV2()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyLayout(LayoutMode);
    }

    public GameUiState? State
    {
        get => (GameUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public AdaptiveLayoutMode LayoutMode
    {
        get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public DashboardUiState? DashboardState
    {
        get => (DashboardUiState?)GetValue(DashboardStateProperty);
        set => SetValue(DashboardStateProperty, value);
    }

    private static void LayoutModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((GameViewV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void ApplyLayout(AdaptiveLayoutMode mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 22, 34, 30),
            AdaptiveLayoutMode.Compact => new Thickness(26, 18, 26, 26),
            _ => new Thickness(20, 16, 20, 24)
        };
        HeroCard.Height = mode switch
        {
            AdaptiveLayoutMode.Wide => 296,
            AdaptiveLayoutMode.Compact => 280,
            _ => 276
        };
        HeroCopyPanel.Padding = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(44, 30, 44, 30),
            AdaptiveLayoutMode.Compact => new Thickness(38, 28, 38, 28),
            _ => new Thickness(32, 26, 32, 26)
        };
        HeroTitle.FontSize = mode switch
        {
            AdaptiveLayoutMode.Wide => 38,
            AdaptiveLayoutMode.Compact => 36,
            _ => 34
        };
        HeroArtwork.Width = mode switch
        {
            AdaptiveLayoutMode.Wide => 940,
            AdaptiveLayoutMode.Compact => 860,
            _ => 820
        };
        HeroArtwork.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(0, 0, 56, 0),
            AdaptiveLayoutMode.Compact => new Thickness(0, 0, 32, 0),
            _ => new Thickness(0, 0, 20, 0)
        };

        NewsColumn.Width = new GridLength(1, GridUnitType.Star);
        CardGapColumn.Width = new GridLength(20);
        InstallColumn.Width = new GridLength(mode == AdaptiveLayoutMode.Wide ? 360 : 340);
        Grid.SetRow(NewsCard, 0);
        Grid.SetColumn(NewsCard, 0);
        Grid.SetRow(InstallCard, 0);
        Grid.SetColumn(InstallCard, 2);
    }
}
