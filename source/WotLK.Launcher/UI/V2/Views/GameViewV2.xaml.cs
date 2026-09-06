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
        SizeChanged += (_, _) => ApplyLayout(LayoutMode);
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

        // Layout each native control in DIPs; no root Viewbox or rasterized interface.
        double width = ActualWidth > 0 ? ActualWidth : 1672;
        double height = ActualHeight > 0 ? ActualHeight : 941;
        double scale = Math.Clamp(Math.Min(width / 1672, height / 941), 0.64, 1.25);
        HeroCard.MinHeight = 640;
        HeroCopyPanel.Padding = new Thickness(74 * scale, 214 * scale, 0, 0);
        RealmEyebrow.Height = 26 * scale;
        RealmEyebrow.RenderTransform = new System.Windows.Media.TranslateTransform(0, 5 * scale);
        RealmEyebrowLine.Width = 38 * scale;
        RealmEyebrowLine.Margin = new Thickness(0, 0, 22 * scale, 0);
        ((TextBlock)RealmEyebrow.Children[1]).FontSize = Math.Max(11, 14 * scale);
        // Inter's cap height and baseline differ from the previous title face.
        // Position both live text lines independently, without stretching the glyphs.
        HeroTitleBlock.Height = 212 * scale;
        HeroTitleBlock.Margin = new Thickness(8 * scale, 17 * scale, 0, 0);
        HeroTitle.FontSize = 102 * scale;
        HeroTitleSecond.FontSize = 99 * scale;
        HeroTitleSecond.Margin = new Thickness(1 * scale, 93.4 * scale, 0, 0);
        HeroSubtitle.FontSize = 38 * scale;
        HeroSubtitle.Margin = new Thickness(13 * scale, 0, 0, 0);
        HeroChips.Margin = new Thickness(12 * scale, 35 * scale, 0, 0);
        int chipIndex = 0;
        foreach (Border chip in HeroChips.Children)
        {
            chip.Width = scale >= 0.9 ? new[] { 157, 202, 256 }[chipIndex] * scale : double.NaN;
            chipIndex++;
            chip.Height = Math.Max(34, 43 * scale);
            chip.Padding = new Thickness(13 * scale, 0, 13 * scale, 0);
            chip.Margin = new Thickness(0, 0, 16 * scale, 0);
            if (chip.Child is StackPanel content && content.Children[1] is TextBlock label)
                label.FontSize = Math.Max(11, 15 * scale);
        }
        HeroMotto.Margin = new Thickness(0, 469 * scale, 81 * scale, 0);
        HeroMotto.Visibility = width >= 1400 ? Visibility.Visible : Visibility.Collapsed;
        HeroBottomBar.Margin = new Thickness(59 * scale, 0, 59 * scale, 59 * scale);
        HeroStatusRegion.Width = 575 * scale;
        RealmStatusCard.Height = Math.Max(116, 152 * scale);
        RealmStatusCard.Padding = new Thickness(33 * scale, 22 * scale, 33 * scale, 23 * scale);
        ((Grid)RealmStatusCard.Child).RowDefinitions[0].Height = new GridLength(42 * scale);
        RealmStatusText.FontSize = Math.Max(13, 18 * scale);
        GameServerStatus.Height = Math.Max(22, 28 * scale);
        RealmStatusDot.Width = RealmStatusDot.Height = Math.Max(10, 15 * scale);
        RealmStatusDot.Margin = new Thickness(0, 0, 16 * scale, 0);
        RealmFacts.Margin = new Thickness(5 * scale, 16 * scale, 0, 0);
        TextBlock.SetFontSize(RealmFacts, Math.Max(12, 16 * scale));
        for (int i = 0; i < RealmFacts.Children.Count; i++)
        {
            FrameworkElement child = (FrameworkElement)RealmFacts.Children[i];
            StackPanel fact;
            if (child is Border divider)
            {
                divider.Padding = new Thickness((i == 1 ? 33 : 48) * scale, 0, 0, 0);
                fact = (StackPanel)divider.Child;
            }
            else fact = (StackPanel)child;
            ((TextBlock)fact.Children[1]).FontSize = Math.Max(10, 14 * scale);
        }
        HeroActionRegion.Margin = new Thickness(0, 0, 0, 21 * scale);
        PrimaryActionButton.Width = 322 * scale;
        PrimaryActionButton.Height = Math.Max(62, 91 * scale);
        PrimaryActionButton.FontSize = Math.Max(18, 26 * scale);
        var primaryIcon = (System.Windows.Shapes.Path)((StackPanel)PrimaryActionButton.Content).Children[0];
        primaryIcon.Width = Math.Max(18, 24 * scale);
        primaryIcon.Height = Math.Max(20, 26 * scale);
        primaryIcon.Margin = new Thickness(0, 0, Math.Max(12, 20 * scale), 0);
        PrimaryActionLabelText.MaxWidth = PrimaryActionButton.Width - PrimaryActionButton.Padding.Left
            - PrimaryActionButton.Padding.Right - primaryIcon.Width - primaryIcon.Margin.Right - 2;
        LatestPatchNoteAction.Width = 210 * scale;
        LatestPatchNoteAction.Height = PrimaryActionButton.Height;
        LatestPatchNoteAction.Margin = new Thickness(0, 0, 14 * scale, 0);
        LatestPatchNoteAction.Padding = new Thickness(8 * scale, 0, 8 * scale, 0);
        LatestPatchNoteLabel.FontSize = Math.Max(11, 14 * scale);
        var noteIcon = (System.Windows.Shapes.Path)((StackPanel)LatestPatchNoteAction.Content).Children[0];
        noteIcon.Width = Math.Max(17, 24 * scale);
        noteIcon.Height = Math.Max(20, 27 * scale);
        noteIcon.Margin = new Thickness(0, 0, 19 * scale, 0);
    }
}
