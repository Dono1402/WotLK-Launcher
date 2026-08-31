using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

namespace WotLK.Launcher.UI.V2.Views;

public partial class AuthOverlayViewV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(160));
    private AuthUiState? _subscribedState;
    private bool _hasOpened;
    private bool _applyingPreview;
    private int _transitionVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AuthUiState),
        typeof(AuthOverlayViewV2),
        new PropertyMetadata(null, StateChanged));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(AuthOverlayViewV2),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, IsOpenChanged));

    public AuthOverlayViewV2()
    {
        InitializeComponent();
        Loaded += AuthOverlayViewV2_Loaded;
        Unloaded += AuthOverlayViewV2_Unloaded;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? Closed;

    public AuthUiState? State
    {
        get => (AuthUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    internal AuthPreviewScenario? PreviewScenario { get; set; }

    internal bool IsFullyClosed => Visibility == Visibility.Collapsed
        && !IsHitTestVisible
        && !Scrim.IsHitTestVisible;

    internal bool ArePasswordFieldsEmpty => string.IsNullOrEmpty(LoginPasswordBox.Password)
        && string.IsNullOrEmpty(RegisterPasswordBox.Password)
        && string.IsNullOrEmpty(RegisterPasswordConfirmBox.Password);

    internal void DetachFromShell()
    {
        ++_transitionVersion;
        AuthCard.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IsHitTestVisible = false;
        Scrim.IsHitTestVisible = false;
        AuthCard.Opacity = 0;
        Scrim.Opacity = 0;
        CardScale.ScaleX = 0.985;
        CardScale.ScaleY = 0.985;
        Visibility = Visibility.Collapsed;
        ClearFields();
        SubscribeToState(_subscribedState, null);
    }

    internal void PreparePreviewScenario(AuthPreviewScenario scenario)
    {
        PreviewScenario = scenario;
        if (IsLoaded)
        {
            PopulatePreviewFields(scenario);
        }
    }

    internal bool ContainsKeyboardFocusTarget(DependencyObject? target)
    {
        return target is not null && IsDescendantOf(target, AuthCard);
    }

    internal void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(State?.Mode == AuthMode.Register
                ? RegisterUsernameBox
                : LoginUsernameBox));
    }

    internal void ValidateForPreview(bool showErrors)
    {
        AuthUiState? state = State;
        if (state is null)
        {
            return;
        }

        AuthFormValidation validation = state.Mode == AuthMode.Login
            ? AuthPreviewValidation.Login(state.LoginUsername, !string.IsNullOrEmpty(LoginPasswordBox.Password))
            : AuthPreviewValidation.Register(
                state.RegisterUsername,
                state.RegisterEmail,
                RegisterPasswordBox.Password.Length,
                !string.IsNullOrEmpty(RegisterPasswordConfirmBox.Password),
                string.Equals(
                    RegisterPasswordBox.Password,
                    RegisterPasswordConfirmBox.Password,
                    StringComparison.Ordinal));

        state.SetFormValidity(validation.IsValid);
        if (showErrors && !validation.IsValid)
        {
            state.ShowValidationError(validation.Message);
        }
    }

    private static void StateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        AuthOverlayViewV2 view = (AuthOverlayViewV2)dependencyObject;
        view.SubscribeToState(args.OldValue as AuthUiState, args.NewValue as AuthUiState);
    }

    private static void IsOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        AuthOverlayViewV2 view = (AuthOverlayViewV2)dependencyObject;
        if (view.IsLoaded)
        {
            view.ApplyOpenState((bool)args.NewValue, animate: true);
        }
    }

    private void AuthOverlayViewV2_Loaded(object sender, RoutedEventArgs e)
    {
        if (PreviewScenario is AuthPreviewScenario scenario)
        {
            PopulatePreviewFields(scenario);
        }

        ApplyOpenState(IsOpen, animate: false);
    }

    private void AuthOverlayViewV2_Unloaded(object sender, RoutedEventArgs e)
    {
        SubscribeToState(_subscribedState, null);
        ClearFields();
    }

    private void SubscribeToState(AuthUiState? oldState, AuthUiState? newState)
    {
        if (oldState is not null)
        {
            oldState.PropertyChanged -= State_PropertyChanged;
        }

        _subscribedState = newState;
        if (newState is not null)
        {
            newState.PropertyChanged += State_PropertyChanged;
        }
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AuthUiState.Mode))
        {
            return;
        }

        ValidateForPreview(showErrors: false);
        FocusFirstControl();
    }

    private void PopulatePreviewFields(AuthPreviewScenario scenario)
    {
        AuthUiState? state = State;
        if (state is null)
        {
            return;
        }

        _applyingPreview = true;
        try
        {
            const string previewPassword = "atlas-preview-02f1";
            state.LoginUsername = "Dono1402";
            state.RegisterUsername = "Dono1402";
            state.RegisterEmail = "dono1402@example.test";
            LoginPasswordBox.Password = previewPassword;
            RegisterPasswordBox.Password = previewPassword;
            RegisterPasswordConfirmBox.Password = scenario == AuthPreviewScenario.RegisterValidation
                ? "atlas-preview-different"
                : previewPassword;
            ValidateForPreview(showErrors: false);
        }
        finally
        {
            _applyingPreview = false;
        }
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingPreview || State is null)
        {
            return;
        }

        State.ClearErrorAfterInput();
        ValidateForPreview(showErrors: false);
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestClose();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || State is null || State.IsBusy)
        {
            return;
        }

        ValidateForPreview(showErrors: true);
        if (State.SubmitCommand.CanExecute(null))
        {
            State.SubmitCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        FocusFirstControl();
    }

    private void ApplyOpenState(bool isOpen, bool animate)
    {
        int transitionVersion = ++_transitionVersion;
        double currentCardOpacity = AuthCard.Opacity;
        double currentScrimOpacity = Scrim.Opacity;
        double currentScale = CardScale.ScaleX;

        AuthCard.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (isOpen)
        {
            _hasOpened = true;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            Scrim.IsHitTestVisible = true;

            if (!animate)
            {
                AuthCard.Opacity = 1;
                Scrim.Opacity = 1;
                CardScale.ScaleX = 1;
                CardScale.ScaleY = 1;
                FocusFirstControl();
                return;
            }

            AnimateTo(
                currentCardOpacity,
                currentScrimOpacity,
                currentScale,
                targetCardOpacity: 1,
                targetScrimOpacity: 1,
                targetScale: 1,
                transitionVersion,
                opening: true);
            return;
        }

        IsHitTestVisible = false;
        Scrim.IsHitTestVisible = false;
        if (!animate || Visibility != Visibility.Visible)
        {
            CompleteClose();
            return;
        }

        AnimateTo(
            currentCardOpacity,
            currentScrimOpacity,
            currentScale,
            targetCardOpacity: 0,
            targetScrimOpacity: 0,
            targetScale: 0.985,
            transitionVersion,
            opening: false);
    }

    private void AnimateTo(
        double fromCardOpacity,
        double fromScrimOpacity,
        double fromScale,
        double targetCardOpacity,
        double targetScrimOpacity,
        double targetScale,
        int transitionVersion,
        bool opening)
    {
        CubicEase ease = new()
        {
            EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        AuthCard.Opacity = targetCardOpacity;
        Scrim.Opacity = targetScrimOpacity;
        CardScale.ScaleX = targetScale;
        CardScale.ScaleY = targetScale;

        DoubleAnimation cardAnimation = CreateAnimation(fromCardOpacity, targetCardOpacity, ease);
        DoubleAnimation scrimAnimation = CreateAnimation(fromScrimOpacity, targetScrimOpacity, ease);
        DoubleAnimation scaleXAnimation = CreateAnimation(fromScale, targetScale, ease);
        DoubleAnimation scaleYAnimation = CreateAnimation(fromScale, targetScale, ease);

        cardAnimation.Completed += (_, _) =>
        {
            if (transitionVersion != _transitionVersion || IsOpen != opening)
            {
                return;
            }

            AuthCard.BeginAnimation(OpacityProperty, null);
            Scrim.BeginAnimation(OpacityProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            if (opening)
            {
                FocusFirstControl();
            }
            else
            {
                CompleteClose();
            }
        };

        AuthCard.BeginAnimation(OpacityProperty, cardAnimation, HandoffBehavior.SnapshotAndReplace);
        Scrim.BeginAnimation(OpacityProperty, scrimAnimation, HandoffBehavior.SnapshotAndReplace);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation, HandoffBehavior.SnapshotAndReplace);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateAnimation(double from, double to, IEasingFunction ease)
    {
        return new DoubleAnimation(from, to, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
    }

    private void CompleteClose()
    {
        AuthCard.Opacity = 0;
        Scrim.Opacity = 0;
        CardScale.ScaleX = 0.985;
        CardScale.ScaleY = 0.985;
        Visibility = Visibility.Collapsed;
        ClearFields();
        State?.ResetAfterClose();
        if (_hasOpened)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearFields()
    {
        LoginUsernameBox.Clear();
        LoginPasswordBox.Clear();
        RegisterUsernameBox.Clear();
        RegisterEmailBox.Clear();
        RegisterPasswordBox.Clear();
        RegisterPasswordConfirmBox.Clear();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return false;
    }

    private void RequestClose()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Scrim))
        {
            RequestClose();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestClose();
    }
}
