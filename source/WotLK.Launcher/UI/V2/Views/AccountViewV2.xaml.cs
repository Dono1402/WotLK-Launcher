using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class AccountViewV2 : UserControl
{
    private AccountUiState? _subscribedState;
    private bool _deleteConfirmationWasOpen;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AccountUiState),
        typeof(AccountViewV2),
        new PropertyMetadata(null, StateChanged));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(AdaptiveLayoutMode),
        typeof(AccountViewV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, LayoutModeChanged));

    public AccountViewV2()
    {
        InitializeComponent();
        Loaded += AccountViewV2_Loaded;
        Unloaded += AccountViewV2_Unloaded;
    }

    public event EventHandler? ModifyAvatarRequested;

    public event EventHandler? RemoveAvatarRequested;

    public event EventHandler? ConfirmAvatarDeleteRequested;

    public AccountUiState? State
    {
        get => (AccountUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public AdaptiveLayoutMode LayoutMode
    {
        get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    internal ScrollViewer ScrollHost => AccountScrollViewer;

    internal AccountSection SelectedSection => State?.Current.SelectedSection ?? AccountSection.Profile;

    internal IInputElement AvatarActionFocusTarget => ModifyAvatarButton;

    internal bool IsDeleteConfirmationOpen => State?.Current.IsDeleteConfirmationOpen == true;

    internal bool ContainsDeleteConfirmationFocus(DependencyObject? target)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (ReferenceEquals(current, DeleteConfirmationPanel))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    internal void FocusDeleteConfirmation()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(CancelDeleteAvatarButton));
    }

    internal bool TryCancelDeleteConfirmation()
    {
        if (!IsDeleteConfirmationOpen)
        {
            return false;
        }

        State?.CloseDeleteConfirmation();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(RemoveAvatarButton));
        return true;
    }

    private static void StateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        AccountViewV2 view = (AccountViewV2)dependencyObject;
        view.ReplaceStateSubscription(args.OldValue as AccountUiState, args.NewValue as AccountUiState);
        view.ApplyState();
    }

    private static void LayoutModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((AccountViewV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void AccountViewV2_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToState(State);
        ApplyLayout(LayoutMode);
        ApplyState();
    }

    private void AccountViewV2_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromState(_subscribedState);
        AccountScrollViewer.ScrollToTop();
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
            AdaptiveLayoutMode.Compact => 1140,
            _ => 1036
        };
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 28, 34, 42),
            AdaptiveLayoutMode.Compact => new Thickness(28, 24, 28, 38),
            _ => new Thickness(22, 20, 22, 34)
        };
        PageTitle.FontSize = mode == AdaptiveLayoutMode.Stacked ? 28 : 30;

        bool stacked = mode == AdaptiveLayoutMode.Stacked;
        AvatarColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(352);
        ProfileGapColumn.Width = stacked ? new GridLength(0) : new GridLength(24);
        Grid.SetRowSpan(AvatarCard, stacked ? 1 : 2);
        Grid.SetColumn(AvatarCard, 0);
        Grid.SetRow(AvatarCard, 0);
        Grid.SetColumn(ProfileDetailsColumn, stacked ? 0 : 2);
        Grid.SetRow(ProfileDetailsColumn, stacked ? 1 : 0);
        ProfileDetailsColumn.Margin = stacked ? new Thickness(0, 18, 0, 0) : new Thickness(0);
    }

    private void ApplyState()
    {
        if (!IsInitialized || State is null)
        {
            return;
        }

        AccountSection section = State.Current.SelectedSection;
        ProfilePanel.Visibility = section == AccountSection.Profile ? Visibility.Visible : Visibility.Collapsed;
        SecurityPanel.Visibility = section == AccountSection.Security ? Visibility.Visible : Visibility.Collapsed;
        SessionsPanel.Visibility = section == AccountSection.Sessions ? Visibility.Visible : Visibility.Collapsed;
        ProfileTabButton.Tag = section == AccountSection.Profile ? "Active" : null;
        SecurityTabButton.Tag = section == AccountSection.Security ? "Active" : null;
        SessionsTabButton.Tag = section == AccountSection.Sessions ? "Active" : null;

        AccountViewState state = State.Current;
        bool operationActive = state.AvatarOperation != AvatarPreviewOperation.None;
        AvatarOperationBanner.Visibility = operationActive ? Visibility.Visible : Visibility.Collapsed;
        AvatarOperationProgress.IsIndeterminate = true;
        ModifyAvatarButton.IsEnabled = state.CanModifyAvatar;
        RemoveAvatarButton.IsEnabled = state.CanRemoveAvatar;
        AvatarAvailabilityBanner.Visibility = string.IsNullOrWhiteSpace(state.AvatarAvailabilityMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AvatarErrorBanner.Visibility = string.IsNullOrWhiteSpace(state.AvatarErrorMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;
        EmailVerificationText.Text = state.IsEmailVerified ? "Vérifiée" : "Non vérifiée";
        SecuritySummaryTitle.Text = state.IsEmailVerified
            ? "Compte protégé"
            : "Vérification recommandée";
        SecuritySummaryText.Text = state.IsEmailVerified
            ? "Ton adresse e-mail est vérifiée."
            : "Vérifie ton adresse e-mail pour renforcer la sécurité du compte.";
        SessionsSummaryText.Text = state.IsPreview
            ? $"{state.ActiveSessionCount} sessions actives"
            : "Gestion des sessions à venir";
        SessionsPreviewList.Visibility = state.IsPreview
            ? Visibility.Visible
            : Visibility.Collapsed;
        SessionsUnavailableCard.Visibility = state.IsPreview
            ? Visibility.Collapsed
            : Visibility.Visible;
        Brush emailBrush = (Brush)FindResource(state.IsEmailVerified
            ? "AtlasV2.Brush.Success"
            : "AtlasV2.Brush.Gold");
        Brush emailSurface = (Brush)FindResource(state.IsEmailVerified
            ? "AtlasV2.Brush.SuccessSurface"
            : "AtlasV2.Brush.GoldSurface");
        Brush emailBorder = (Brush)FindResource(state.IsEmailVerified
            ? "AtlasV2.Brush.SuccessBorder"
            : "AtlasV2.Brush.GoldBorder");
        EmailVerificationText.Foreground = emailBrush;
        EmailVerificationIcon.Stroke = emailBrush;
        EmailVerificationBadge.Background = emailSurface;
        EmailVerificationBadge.BorderBrush = emailBorder;

        DeleteConfirmationLayer.Visibility = state.IsDeleteConfirmationOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeleteConfirmationLayer.IsHitTestVisible = state.IsDeleteConfirmationOpen;
        if (state.IsDeleteConfirmationOpen && !_deleteConfirmationWasOpen)
        {
            FocusDeleteConfirmation();
        }
        _deleteConfirmationWasOpen = state.IsDeleteConfirmationOpen;
    }

    private void ReplaceStateSubscription(AccountUiState? oldState, AccountUiState? newState)
    {
        UnsubscribeFromState(oldState);
        SubscribeToState(newState);
    }

    private void SubscribeToState(AccountUiState? state)
    {
        if (state is null || ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        state.PropertyChanged += State_PropertyChanged;
        _subscribedState = state;
    }

    private void UnsubscribeFromState(AccountUiState? state)
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
        ApplyState();
    }

    private void SelectSection(AccountSection section)
    {
        State?.SelectSection(section);
        AccountScrollViewer.ScrollToTop();
    }

    private void ProfileTabButton_Click(object sender, RoutedEventArgs e) => SelectSection(AccountSection.Profile);

    private void SecurityTabButton_Click(object sender, RoutedEventArgs e) => SelectSection(AccountSection.Security);

    private void SessionsTabButton_Click(object sender, RoutedEventArgs e) => SelectSection(AccountSection.Sessions);

    private void SecurityShortcutButton_Click(object sender, RoutedEventArgs e) => SelectSection(AccountSection.Security);

    private void ModifyAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (State?.Current.CanModifyAvatar == true)
        {
            ModifyAvatarRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (State?.Current.CanRemoveAvatar != true)
        {
            return;
        }

        if (State.Current.IsPreview)
        {
            State.StartRemovingPreview();
            RemoveAvatarRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        State.ShowDeleteConfirmation();
        RemoveAvatarRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelDeleteAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        TryCancelDeleteConfirmation();
    }

    private void ConfirmDeleteAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmAvatarDeleteRequested?.Invoke(this, EventArgs.Empty);
    }
}
