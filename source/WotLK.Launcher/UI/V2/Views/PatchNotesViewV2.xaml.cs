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

    internal ScrollViewer ScrollHost
    {
        get
        {
            PatchNotesList.ApplyTemplate();
            return (ScrollViewer)PatchNotesList.Template.FindName("PART_ScrollViewer", PatchNotesList);
        }
    }

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

        ContentFrame.MaxWidth = AtlasPageLayout.MaxWidth;
        ContentFrame.Margin = AtlasPageLayout.Margin;
        PageTitle.FontSize = AtlasPageLayout.TitleSize;
        PageDescription.FontSize = AtlasPageLayout.SubtitleSize;
    }
}
