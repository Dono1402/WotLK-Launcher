using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ActivityCenterPanelV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(180));
    private bool _hasOpened;
    private int _transitionVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(ActivityUiState),
        typeof(ActivityCenterPanelV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(ActivityCenterPanelV2),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, IsOpenChanged));

    public ActivityCenterPanelV2()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyOpenState(IsOpen, animate: false);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? Closed;

    public ActivityUiState? State
    {
        get => (ActivityUiState?)GetValue(StateProperty);
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

    internal ScrollViewer ScrollHost => RecentScrollViewer;

    internal Border PanelHost => ActivityPanel;

    public bool ContainsKeyboardFocusTarget(DependencyObject? target) =>
        target is not null && IsDescendantOf(target, ActivityPanel);

    public void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(CloseButton));
    }

    private static void IsOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ActivityCenterPanelV2 panel = (ActivityCenterPanelV2)dependencyObject;
        if (panel.IsLoaded)
        {
            panel.ApplyOpenState((bool)args.NewValue, animate: true);
        }
    }

    private void ApplyOpenState(bool isOpen, bool animate)
    {
        int transitionVersion = ++_transitionVersion;
        double currentOffset = PanelTranslate.X;
        double currentPanelOpacity = ActivityPanel.Opacity;
        double currentScrimOpacity = Scrim.Opacity;

        PanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ActivityPanel.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);

        if (isOpen)
        {
            _hasOpened = true;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            Scrim.IsHitTestVisible = true;

            if (!animate)
            {
                PanelTranslate.X = 0;
                ActivityPanel.Opacity = 1;
                Scrim.Opacity = 1;
                FocusFirstControl();
                return;
            }

            AnimateTo(
                currentOffset,
                currentPanelOpacity,
                currentScrimOpacity,
                0,
                1,
                1,
                transitionVersion,
                opening: true);
            return;
        }

        IsHitTestVisible = false;
        Scrim.IsHitTestVisible = false;

        if (!animate || Visibility != Visibility.Visible)
        {
            PanelTranslate.X = 428;
            ActivityPanel.Opacity = 0;
            Scrim.Opacity = 0;
            Visibility = Visibility.Collapsed;
            if (_hasOpened)
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        AnimateTo(
            currentOffset,
            currentPanelOpacity,
            currentScrimOpacity,
            428,
            0,
            0,
            transitionVersion,
            opening: false);
    }

    private void AnimateTo(
        double fromOffset,
        double fromPanelOpacity,
        double fromScrimOpacity,
        double targetOffset,
        double targetPanelOpacity,
        double targetScrimOpacity,
        int transitionVersion,
        bool opening)
    {
        CubicEase ease = new()
        {
            EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        PanelTranslate.X = targetOffset;
        ActivityPanel.Opacity = targetPanelOpacity;
        Scrim.Opacity = targetScrimOpacity;

        DoubleAnimation offsetAnimation = CreateAnimation(fromOffset, targetOffset, ease);
        DoubleAnimation panelAnimation = CreateAnimation(fromPanelOpacity, targetPanelOpacity, ease);
        DoubleAnimation scrimAnimation = CreateAnimation(fromScrimOpacity, targetScrimOpacity, ease);

        offsetAnimation.Completed += (_, _) =>
        {
            if (transitionVersion != _transitionVersion || IsOpen != opening)
            {
                return;
            }

            PanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ActivityPanel.BeginAnimation(OpacityProperty, null);
            Scrim.BeginAnimation(OpacityProperty, null);

            if (opening)
            {
                FocusFirstControl();
            }
            else
            {
                Visibility = Visibility.Collapsed;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        };

        PanelTranslate.BeginAnimation(TranslateTransform.XProperty, offsetAnimation, HandoffBehavior.SnapshotAndReplace);
        ActivityPanel.BeginAnimation(OpacityProperty, panelAnimation, HandoffBehavior.SnapshotAndReplace);
        Scrim.BeginAnimation(OpacityProperty, scrimAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateAnimation(double from, double to, IEasingFunction ease) =>
        new(from, to, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };

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

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Scrim))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelActivityButton_Click(object sender, RoutedEventArgs e)
    {
        State?.RequestPreviewCancellation();
    }
}
