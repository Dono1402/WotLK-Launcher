using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class PatchNoteOverlayV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(150));
    private bool _hasOpened;
    private int _transitionVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(DashboardUiState),
        typeof(PatchNoteOverlayV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(PatchNoteOverlayV2),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            IsOpenChanged));

    public PatchNoteOverlayV2()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyOpenState(IsOpen, animate: false);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? Closed;

    public DashboardUiState? State
    {
        get => (DashboardUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    internal bool IsFullyClosed => Visibility == Visibility.Collapsed
        && !IsHitTestVisible
        && !Scrim.IsHitTestVisible;

    internal bool ContainsKeyboardFocusTarget(DependencyObject? target)
    {
        return target is not null && IsDescendantOf(target, DialogCard);
    }

    internal void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(CloseButton));
    }

    internal void DetachFromShell()
    {
        ++_transitionVersion;
        DialogCard.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IsHitTestVisible = false;
        Scrim.IsHitTestVisible = false;
        DialogCard.Opacity = 0;
        Scrim.Opacity = 0;
        DialogScale.ScaleX = 0.985;
        DialogScale.ScaleY = 0.985;
        Visibility = Visibility.Collapsed;
    }

    private static void IsOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        PatchNoteOverlayV2 overlay = (PatchNoteOverlayV2)dependencyObject;
        if (overlay.IsLoaded)
        {
            overlay.ApplyOpenState((bool)args.NewValue, animate: true);
        }
    }

    private void ApplyOpenState(bool isOpen, bool animate)
    {
        int transitionVersion = ++_transitionVersion;
        double currentCardOpacity = DialogCard.Opacity;
        double currentScrimOpacity = Scrim.Opacity;
        double currentScale = DialogScale.ScaleX;
        StopAnimations();

        if (isOpen)
        {
            _hasOpened = true;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            Scrim.IsHitTestVisible = true;
            if (!animate)
            {
                DialogCard.Opacity = 1;
                Scrim.Opacity = 1;
                DialogScale.ScaleX = 1;
                DialogScale.ScaleY = 1;
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
        DialogCard.Opacity = targetCardOpacity;
        Scrim.Opacity = targetScrimOpacity;
        DialogScale.ScaleX = targetScale;
        DialogScale.ScaleY = targetScale;
        DoubleAnimation cardOpacity = CreateAnimation(fromCardOpacity, targetCardOpacity, ease);
        DoubleAnimation scrimOpacity = CreateAnimation(fromScrimOpacity, targetScrimOpacity, ease);
        DoubleAnimation scaleX = CreateAnimation(fromScale, targetScale, ease);
        DoubleAnimation scaleY = CreateAnimation(fromScale, targetScale, ease);
        cardOpacity.Completed += (_, _) =>
        {
            if (transitionVersion != _transitionVersion || IsOpen != opening)
            {
                return;
            }

            StopAnimations();
            if (opening)
            {
                FocusFirstControl();
            }
            else
            {
                CompleteClose();
            }
        };
        DialogCard.BeginAnimation(OpacityProperty, cardOpacity, HandoffBehavior.SnapshotAndReplace);
        Scrim.BeginAnimation(OpacityProperty, scrimOpacity, HandoffBehavior.SnapshotAndReplace);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX, HandoffBehavior.SnapshotAndReplace);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateAnimation(double from, double to, IEasingFunction ease)
    {
        return new DoubleAnimation(from, to, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
    }

    private void StopAnimations()
    {
        DialogCard.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void CompleteClose()
    {
        DialogCard.Opacity = 0;
        Scrim.Opacity = 0;
        DialogScale.ScaleX = 0.985;
        DialogScale.ScaleY = 0.985;
        Visibility = Visibility.Collapsed;
        if (_hasOpened)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
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

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestClose();
            e.Handled = true;
        }
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RequestClose();
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestClose();
    }
}
