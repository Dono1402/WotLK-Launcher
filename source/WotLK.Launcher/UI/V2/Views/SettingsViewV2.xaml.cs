using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class SettingsViewV2 : UserControl
{
    private SettingsUiState? _subscribedState;
    private bool _isApplyingState;
    private bool _initialCategoryApplied;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(SettingsUiState),
        typeof(SettingsViewV2),
        new PropertyMetadata(null, StateChanged));

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

    internal SettingsCategory SelectedCategory { get; private set; } = SettingsCategory.General;

    private static void StateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        SettingsViewV2 view = (SettingsViewV2)dependencyObject;
        view.ReplaceStateSubscription(
            args.OldValue as SettingsUiState,
            args.NewValue as SettingsUiState);
        view._initialCategoryApplied = false;
        view.ApplyState(applyInitialCategory: true);
    }

    private static void LayoutModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((SettingsViewV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void SettingsViewV2_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToState(State);
        ApplyLayout(LayoutMode);
        ApplyState(applyInitialCategory: !_initialCategoryApplied);
    }

    private void SettingsViewV2_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromState(_subscribedState);
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
            AdaptiveLayoutMode.Wide => 1280,
            AdaptiveLayoutMode.Compact => 1160,
            _ => 1040
        };
        SettingsActionContent.MaxWidth = ContentFrame.MaxWidth;
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 26, 34, 38),
            AdaptiveLayoutMode.Compact => new Thickness(28, 24, 28, 34),
            _ => new Thickness(22, 20, 22, 30)
        };
        PageTitle.FontSize = mode switch
        {
            AdaptiveLayoutMode.Wide => 30,
            AdaptiveLayoutMode.Compact => 29,
            _ => 28
        };
        NavigationColumn.Width = new GridLength(mode switch
        {
            AdaptiveLayoutMode.Wide => 224,
            AdaptiveLayoutMode.Compact => 212,
            _ => 176
        });
        NavigationGap.Width = new GridLength(mode switch
        {
            AdaptiveLayoutMode.Wide => 24,
            AdaptiveLayoutMode.Compact => 20,
            _ => 16
        });
        SettingsActionBar.Padding = mode == AdaptiveLayoutMode.Stacked
            ? new Thickness(22, 11, 22, 11)
            : new Thickness(34, 12, 34, 12);
    }

    internal void SelectCategory(SettingsCategory category)
    {
        if (!IsInitialized)
        {
            SelectedCategory = category;
            return;
        }

        SelectedCategory = category;
        SetCategoryState(GeneralCategoryButton, GeneralPanel, category == SettingsCategory.General);
        SetCategoryState(GameCategoryButton, GamePanel, category == SettingsCategory.Game);
        SetCategoryState(UpdatesCategoryButton, UpdatesPanel, category == SettingsCategory.Updates);
        SetCategoryState(NotificationsCategoryButton, NotificationsPanel, category == SettingsCategory.Notifications);
        SetCategoryState(AppearanceCategoryButton, AppearancePanel, category == SettingsCategory.Appearance);
        SetCategoryState(DiagnosticCategoryButton, DiagnosticPanel, category == SettingsCategory.Diagnostic);
        SettingsScrollViewer.ScrollToTop();
    }

    private void ApplyState(bool applyInitialCategory = false)
    {
        if (!IsInitialized || State is null)
        {
            return;
        }

        _isApplyingState = true;
        try
        {
            if (applyInitialCategory && !_initialCategoryApplied)
            {
                SelectCategory(State.Current.InitialCategory);
                _initialCategoryApplied = true;
            }

            ApplyRuntimeNotice(State.Current.RuntimeNoticeMessage);
            ApplySavePreviewState(State.Current.SavePreviewState);
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ApplySavePreviewState(SettingsSavePreviewState state)
    {
        if (State?.Current.IsRuntimeConnected == true
            || state == SettingsSavePreviewState.None)
        {
            SettingsActionBar.Visibility = Visibility.Collapsed;
            return;
        }

        SettingsActionBar.Visibility = Visibility.Visible;
        SettingsActionProgress.Visibility = Visibility.Collapsed;
        bool runtimeConnected = State?.Current.IsRuntimeConnected == true;
        SettingsActionButtons.Visibility = runtimeConnected
            ? Visibility.Collapsed
            : Visibility.Visible;
        CancelSettingsChangesButton.IsEnabled = true;
        SaveSettingsChangesButton.IsEnabled = true;
        SaveSettingsChangesButton.Content = "Enregistrer";

        string surfaceKey;
        string borderKey;
        string accentKey;
        string iconKey;
        switch (state)
        {
            case SettingsSavePreviewState.Saving:
                SettingsActionStatusText.Text = "Enregistrement…";
                SettingsActionDetailText.Text = runtimeConnected
                    ? State?.Current.SaveStatusDetail ?? "Enregistrement immédiat des préférences locales."
                    : "Les préférences fictives sont en cours d’application.";
                SettingsActionProgress.Visibility = Visibility.Visible;
                CancelSettingsChangesButton.IsEnabled = false;
                SaveSettingsChangesButton.IsEnabled = false;
                SaveSettingsChangesButton.Content = "Enregistrement…";
                surfaceKey = "AtlasV2.Brush.CyanSurface";
                borderKey = "AtlasV2.Brush.CyanBorder";
                accentKey = "AtlasV2.Brush.Cyan";
                iconKey = "AtlasV2.Icon.Refresh";
                break;
            case SettingsSavePreviewState.Saved:
                SettingsActionStatusText.Text = "Enregistré";
                SettingsActionDetailText.Text = runtimeConnected
                    ? State?.Current.SaveStatusDetail ?? "Préférence enregistrée sur cet ordinateur."
                    : "Les préférences fictives ont été prises en compte.";
                SettingsActionButtons.Visibility = Visibility.Collapsed;
                surfaceKey = "AtlasV2.Brush.SuccessSurface";
                borderKey = "AtlasV2.Brush.SuccessBorder";
                accentKey = "AtlasV2.Brush.Success";
                iconKey = "AtlasV2.Icon.Check";
                break;
            case SettingsSavePreviewState.Error:
                SettingsActionStatusText.Text = runtimeConnected
                    ? State?.Current.SaveStatusTitle ?? "Erreur d’enregistrement"
                    : "Erreur d’enregistrement";
                SettingsActionDetailText.Text = runtimeConnected
                    ? State?.Current.SaveStatusDetail ?? "La préférence n’a pas pu être enregistrée."
                    : "Les modifications fictives n’ont pas pu être enregistrées.";
                surfaceKey = "AtlasV2.Brush.DangerSurface";
                borderKey = "AtlasV2.Brush.DangerBorder";
                accentKey = "AtlasV2.Brush.Danger";
                iconKey = "AtlasV2.Icon.AlertCircle";
                break;
            default:
                SettingsActionStatusText.Text = "Modifications non enregistrées";
                SettingsActionDetailText.Text = "Les changements de cette catégorie n’ont pas encore été appliqués.";
                surfaceKey = "AtlasV2.Brush.GoldSurface";
                borderKey = "AtlasV2.Brush.GoldBorder";
                accentKey = "AtlasV2.Brush.Gold";
                iconKey = "AtlasV2.Icon.AlertCircle";
                break;
        }

        Brush surface = (Brush)FindResource(surfaceKey);
        Brush border = (Brush)FindResource(borderKey);
        Brush accent = (Brush)FindResource(accentKey);
        SettingsActionBar.Background = surface;
        SettingsActionBar.BorderBrush = border;
        SettingsActionIconBadge.Background = surface;
        SettingsActionIconBadge.BorderBrush = border;
        SettingsActionIcon.Stroke = accent;
        SettingsActionIcon.Data = (Geometry)FindResource(iconKey);
    }

    private void ApplyRuntimeNotice(string? message)
    {
        bool show = State?.Current.IsRuntimeConnected == true
            && !string.IsNullOrWhiteSpace(message);
        RuntimeNotice.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RuntimeNoticeText.Text = show ? message : string.Empty;
    }

    private static void SetCategoryState(Button button, FrameworkElement panel, bool selected)
    {
        button.Tag = selected ? "Active" : null;
        panel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GeneralCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCategory(SettingsCategory.General);
    }

    private void GameCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCategory(SettingsCategory.Game);
    }

    private void UpdatesCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCategory(SettingsCategory.Updates);
    }

    private void NotificationsCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCategory(SettingsCategory.Notifications);
    }

    private void AppearanceCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCategory(SettingsCategory.Appearance);
    }

    private void DiagnosticCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCategory(SettingsCategory.Diagnostic);
    }

    private void GameLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState
            || State is null
            || GameLanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string locale = LauncherSettings.NormalizeGameLocale(item.Tag?.ToString());
        if (!State.TryChangeGameLocale(locale))
        {
            GameLanguageComboBox.SelectedValue = State.Current.Game.GameLocale;
        }
    }

    private void CloseAfterLaunchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingState || State is null)
        {
            return;
        }

        bool requested = CloseAfterLaunchToggle.IsChecked == true;
        if (!State.TryChangeCloseAfterLaunch(requested))
        {
            CloseAfterLaunchToggle.IsChecked =
                State.Current.General.CloseLauncherAfterGameStart;
        }
    }

    private void InstantQuestTextToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingState || State is null)
        {
            return;
        }

        bool requested = InstantQuestTextToggle.IsChecked == true;
        if (!State.TryChangeInstantQuestText(requested))
        {
            InstantQuestTextToggle.IsChecked = State.Current.Game.InstantQuestText;
        }
    }

    private void VerifyRepairButton_Click(object sender, RoutedEventArgs e)
    {
        State?.ShowGameForRepair();
    }

    private void ReplaceStateSubscription(SettingsUiState? previous, SettingsUiState? current)
    {
        UnsubscribeFromState(previous);
        if (IsLoaded)
        {
            SubscribeToState(current);
        }
    }

    private void SubscribeToState(SettingsUiState? state)
    {
        if (state is null || ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        UnsubscribeFromState(_subscribedState);
        _subscribedState = state;
        _subscribedState.PropertyChanged += State_PropertyChanged;
    }

    private void UnsubscribeFromState(SettingsUiState? state)
    {
        if (state is null || !ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        state.PropertyChanged -= State_PropertyChanged;
        _subscribedState = null;
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(SettingsUiState.Current))
        {
            ApplyState();
        }
    }
}
