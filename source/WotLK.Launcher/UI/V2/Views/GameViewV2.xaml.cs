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

        bool stacked = mode == AdaptiveLayoutMode.Stacked;
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 26, 34, 34),
            AdaptiveLayoutMode.Compact => new Thickness(26, 22, 26, 32),
            _ => new Thickness(20, 18, 20, 30)
        };
        HeroCard.Height = stacked ? 284 : 300;
        HeroCopyPanel.Padding = stacked
            ? new Thickness(32, 26, 32, 26)
            : new Thickness(44, 32, 44, 32);
        HeroTitle.FontSize = stacked ? 34 : mode == AdaptiveLayoutMode.Compact ? 36 : 38;

        if (stacked)
        {
            NewsColumn.Width = new GridLength(1, GridUnitType.Star);
            CardGapColumn.Width = new GridLength(0);
            InstallColumn.Width = new GridLength(0);
            FirstCardRow.Height = GridLength.Auto;
            CardGapRow.Height = new GridLength(20);
            SecondCardRow.Height = GridLength.Auto;
            Grid.SetRow(NewsCard, 0);
            Grid.SetColumn(NewsCard, 0);
            Grid.SetRow(InstallCard, 2);
            Grid.SetColumn(InstallCard, 0);
        }
        else
        {
            NewsColumn.Width = new GridLength(2, GridUnitType.Star);
            CardGapColumn.Width = new GridLength(24);
            InstallColumn.Width = new GridLength(1, GridUnitType.Star);
            FirstCardRow.Height = GridLength.Auto;
            CardGapRow.Height = new GridLength(0);
            SecondCardRow.Height = new GridLength(0);
            Grid.SetRow(NewsCard, 0);
            Grid.SetColumn(NewsCard, 0);
            Grid.SetRow(InstallCard, 0);
            Grid.SetColumn(InstallCard, 2);
        }
    }
}
