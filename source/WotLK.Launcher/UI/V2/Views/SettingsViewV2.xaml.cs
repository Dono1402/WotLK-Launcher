using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        SizeChanged += (_, _) => ApplyLayout(LayoutMode);
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

        // Native WPF layout remains responsive; the reference coordinates are DIPs at 96 DPI.
        double width = ActualWidth > 0 ? ActualWidth : 1672;
        double scale = Math.Clamp(width / 1672, 0.64, 1.20);
        Resources["SettingsPilot.Font.Body"] = Math.Max(14, 16 * scale);
        Resources["SettingsPilot.Font.Caption"] = Math.Max(12, 14 * scale);
        Resources["SettingsPilot.Font.Helper"] = Math.Max(12, 16 * scale);
        Resources["SettingsPilot.Font.Option"] = Math.Max(14, 20 * scale);
        Resources["SettingsPilot.Font.SectionTitle"] = Math.Max(18, 24 * scale);
        Resources["SettingsPilot.Font.CategoryTitle"] = Math.Max(24, 36 * scale);
        Resources["SettingsPilot.Font.Navigation"] = Math.Max(14, 20 * scale);
        Resources["SettingsPilot.Font.Action"] = Math.Max(13, 16 * scale);
        Resources["SettingsPilot.NavigationHeight"] = Math.Max(48, 68 * scale);
        Resources["SettingsPilot.NavigationPadding"] = new Thickness(26 * scale, 0, 26 * scale, 0);
        Resources["SettingsPilot.CardPadding"] = new Thickness(28 * scale);
        Resources["SettingsPilot.SeparatorMargin"] = new Thickness(0, 20 * scale, 0, 20 * scale);
        Resources["SettingsPilot.FieldHeight"] = Math.Max(44, 60 * scale);
        Resources["SettingsPilot.ToggleWidth"] = Math.Max(52, 68 * scale);
        Resources["SettingsPilot.ToggleHeight"] = Math.Max(30, 38 * scale);
        Resources["SettingsPilot.ToggleCornerRadius"] = new CornerRadius(Math.Max(30, 38 * scale) / 2);
        Resources["SettingsPilot.ToggleThumb"] = Math.Max(22, 28 * scale);
        Resources["SettingsPilot.ToggleInset"] = new Thickness(Math.Max(4, 5 * scale));

        ContentFrame.MaxWidth = 1900;
        SettingsActionContent.MaxWidth = ContentFrame.MaxWidth;
        ContentFrame.Margin = new Thickness(67 * scale, 17 * scale, 60 * scale, 66 * scale);
        PageHeader.Margin = new Thickness(16 * scale, 0, 0, 0);
        PageEyebrow.Height = 26 * scale;
        PageEyebrowLine.Width = 36 * scale;
        PageEyebrowLine.Margin = new Thickness(4 * scale, 0, 22 * scale, 0);
        PageEyebrowText.FontSize = Math.Max(11, 14 * scale);
        PageTitle.FontSize = Math.Max(44, 72 * scale);
        PageTitle.Margin = new Thickness(0, 8 * scale, 0, 0);
        PageSubtitle.FontSize = Math.Max(18, 26 * scale);
        SettingsWorkspace.Margin = new Thickness(0, 37 * scale, 0, 0);
        NavigationColumn.Width = new GridLength(347 * scale);
        NavigationGap.Width = new GridLength(17 * scale);
        CategoryNavigation.Padding = new Thickness(14 * scale);
        CategoryNavigation.MinHeight = 544 * scale;
        CategoryHeading.Margin = new Thickness(18 * scale, 20 * scale, 18 * scale, 20 * scale);
        CategoryContentSurface.MinHeight = 544 * scale;
        CategoryContentSurface.Padding = new Thickness(20 * scale, 22 * scale, 20 * scale, 22 * scale);
        foreach (Button button in new[]
        {
            GeneralCategoryButton, GameCategoryButton, UpdatesCategoryButton,
            NotificationsCategoryButton, DiagnosticCategoryButton
        })
        {
            if (button.Content is Grid row && row.Children[0] is System.Windows.Shapes.Path icon
                && row.Children[1] is TextBlock label)
            {
                row.ColumnDefinitions[0].Width = new GridLength(Math.Max(24, 34 * scale));
                icon.Width = icon.Height = Math.Max(22, 28 * scale);
                label.Margin = new Thickness(20 * scale, 0, 0, 0);
            }
        }

        GeneralIntro.FontSize = Math.Max(15, 20 * scale);
        GeneralCardDescription.FontSize = Math.Max(14, 18 * scale);
        GeneralPreferencesCard.Margin = new Thickness(0, 26 * scale, 0, 0);
        GeneralPreferencesCard.Padding = new Thickness(28 * scale, 32 * scale, 38 * scale, 36 * scale);
        GeneralCardDescription.Margin = new Thickness(0, 4 * scale, 0, 0);
        InterfaceLanguageRow.Margin = new Thickness(0, 24 * scale, 0, 0);
        InterfaceLanguageRow.MinHeight = Math.Max(44, 60 * scale);
        StartWithWindowsRow.MinHeight = MinimizeToTrayRow.MinHeight = Math.Max(48, 54 * scale);
        double iconColumnWidth = Math.Max(44, 66 * scale);
        InterfaceLanguageIconColumn.Width = StartWithWindowsIconColumn.Width =
            MinimizeToTrayIconColumn.Width = new GridLength(iconColumnWidth);
        InterfaceLanguageFieldColumn.Width = new GridLength(Math.Max(220, 322 * scale));
        foreach (System.Windows.Shapes.Path icon in new[]
        {
            InterfaceLanguageIcon, StartWithWindowsIcon, MinimizeToTrayIcon
        })
        {
            icon.Width = icon.Height = Math.Max(22, 30 * scale);
            icon.Margin = new Thickness(6 * scale, 0, 0, 0);
        }
        InterfaceLanguageCopy.Margin = new Thickness(0, 10 * scale, 20 * scale, 0);
        StartWithWindowsCopy.Margin = MinimizeToTrayCopy.Margin = new Thickness(0, 6 * scale, 20 * scale, 0);
        GeneralFirstSeparator.Margin = GeneralSecondSeparator.Margin =
            new Thickness(iconColumnWidth, 24 * scale, 0, 19 * scale);
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
        SetCategoryState(DiagnosticCategoryButton, DiagnosticPanel, category == SettingsCategory.Diagnostic);
        SettingsScrollViewer.ScrollToTop();
    }

    internal void SelectAndFocusCategory(SettingsCategory category)
    {
        SelectCategory(category);
        Button target = category switch
        {
            SettingsCategory.Game => GameCategoryButton,
            SettingsCategory.Updates => UpdatesCategoryButton,
            SettingsCategory.Notifications => NotificationsCategoryButton,
            SettingsCategory.Diagnostic => DiagnosticCategoryButton,
            _ => GeneralCategoryButton
        };
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => Keyboard.Focus(target)));
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

    private void InterfaceLanguageComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isApplyingState
            || State is null
            || InterfaceLanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string locale = LauncherSettings.NormalizeInterfaceLocale(item.Tag?.ToString());
        if (!State.TryChangeInterfaceLocale(locale))
        {
            InterfaceLanguageComboBox.SelectedValue =
                State.Current.General.InterfaceLocale;
        }
    }

    private void StartWithWindowsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingState || State is null)
        {
            return;
        }

        bool requested = StartWithWindowsToggle.IsChecked == true;
        if (!State.TryChangeStartWithWindows(requested))
        {
            StartWithWindowsToggle.IsChecked = State.Current.General.StartWithWindows;
        }
    }

    private void MinimizeToTrayOnCloseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingState || State is null)
        {
            return;
        }

        bool requested = MinimizeToTrayOnCloseToggle.IsChecked == true;
        if (!State.TryChangeMinimizeToTrayOnClose(requested))
        {
            MinimizeToTrayOnCloseToggle.IsChecked =
                State.Current.General.MinimizeToTrayOnClose;
        }
    }

    private void FriendPresenceNotificationsToggle_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isApplyingState || State is null)
        {
            return;
        }

        bool requested = FriendPresenceNotificationsToggle.IsChecked == true;
        if (!State.TryChangeFriendPresenceNotifications(requested))
        {
            FriendPresenceNotificationsToggle.IsChecked =
                State.Current.Notifications.FriendPresence;
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
