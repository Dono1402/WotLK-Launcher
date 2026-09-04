using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public event EventHandler? PatchNoteRequested;

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

    internal IInputElement PrimaryActionFocusTarget => PrimaryActionButton;

    internal IInputElement PatchNoteActionFocusTarget => LatestPatchNoteAction;

    internal bool FocusPrimaryAction()
    {
        return PrimaryActionButton.Focus();
    }

    private void LatestPatchNoteAction_Click(object sender, RoutedEventArgs e)
    {
        if (DashboardState?.Current.CanOpenLatestPatchNote == true)
        {
            PatchNoteRequested?.Invoke(this, EventArgs.Empty);
        }
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
        HeroCard.MinHeight = mode switch
        {
            AdaptiveLayoutMode.Wide => 520,
            AdaptiveLayoutMode.Compact => 500,
            _ => 480
        };
        HeroCopyPanel.Padding = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(48, 44, 48, 0),
            AdaptiveLayoutMode.Compact => new Thickness(40, 36, 40, 0),
            _ => new Thickness(32, 30, 32, 0)
        };
        HeroTitle.FontSize = mode switch
        {
            AdaptiveLayoutMode.Wide => 48,
            AdaptiveLayoutMode.Compact => 44,
            _ => 40
        };
        HeroBottomBar.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(48, 28, 48, 44),
            AdaptiveLayoutMode.Compact => new Thickness(40, 24, 40, 36),
            _ => new Thickness(32, 22, 32, 30)
        };
        LatestPatchNoteAction.Width = mode == AdaptiveLayoutMode.Stacked ? 158 : 176;
        PrimaryActionButton.Width = mode == AdaptiveLayoutMode.Stacked ? 174 : 190;
    }
}
