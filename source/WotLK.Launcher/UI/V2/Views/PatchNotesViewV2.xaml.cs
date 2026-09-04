using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class PatchNotesViewV2 : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(DashboardUiState),
        typeof(PatchNotesViewV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(AdaptiveLayoutMode),
        typeof(PatchNotesViewV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, LayoutModeChanged));

    public PatchNotesViewV2()
    {
        InitializeComponent();
        Loaded += PatchNotesViewV2_Loaded;
    }

    public DashboardUiState? State
    {
        get => (DashboardUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public AdaptiveLayoutMode LayoutMode
    {
        get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    internal ScrollViewer ScrollHost => PatchNotesScrollViewer;

    internal ItemsControl ListHost => PatchNotesList;

    private static void LayoutModeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((PatchNotesViewV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void PatchNotesViewV2_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout(LayoutMode);
    }

    private void ApplyLayout(AdaptiveLayoutMode mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        ContentFrame.MaxWidth = mode switch
        {
            AdaptiveLayoutMode.Wide => 1180,
            AdaptiveLayoutMode.Compact => 1100,
            _ => 1036
        };
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 28, 34, 42),
            AdaptiveLayoutMode.Compact => new Thickness(28, 24, 28, 38),
            _ => new Thickness(22, 20, 22, 34)
        };
        PageTitle.FontSize = mode == AdaptiveLayoutMode.Stacked ? 28 : 30;
    }
}
