using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class FriendsDrawerV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(180));
    private bool _hasOpened;
    private int _transitionVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(FriendsUiState),
        typeof(FriendsDrawerV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(FriendsDrawerV2),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, IsOpenChanged));

    public FriendsDrawerV2()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyOpenState(IsOpen, animate: false);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? Closed;

    public FriendsUiState? State
    {
        get => (FriendsUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public bool ContainsKeyboardFocusTarget(DependencyObject? target)
    {
        return target is not null && IsDescendantOf(target, DrawerPanel);
    }

    public void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(CloseButton));
    }

    private static void IsOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        FriendsDrawerV2 drawer = (FriendsDrawerV2)dependencyObject;
        if (drawer.IsLoaded)
        {
            drawer.ApplyOpenState((bool)args.NewValue, animate: true);
        }
    }

    private void ApplyOpenState(bool isOpen, bool animate)
    {
        int transitionVersion = ++_transitionVersion;
        double currentOffset = DrawerTranslate.X;
        double currentPanelOpacity = DrawerPanel.Opacity;
        double currentScrimOpacity = Scrim.Opacity;

        DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        DrawerPanel.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);

        if (isOpen)
        {
            _hasOpened = true;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            Scrim.IsHitTestVisible = true;

            if (!animate)
            {
                DrawerTranslate.X = 0;
                DrawerPanel.Opacity = 1;
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
            DrawerTranslate.X = 376;
            DrawerPanel.Opacity = 0;
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
            376,
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

        DrawerTranslate.X = targetOffset;
        DrawerPanel.Opacity = targetPanelOpacity;
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

            DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            DrawerPanel.BeginAnimation(OpacityProperty, null);
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

        DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, offsetAnimation, HandoffBehavior.SnapshotAndReplace);
        DrawerPanel.BeginAnimation(OpacityProperty, panelAnimation, HandoffBehavior.SnapshotAndReplace);
        Scrim.BeginAnimation(OpacityProperty, scrimAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateAnimation(double from, double to, IEasingFunction ease)
    {
        return new DoubleAnimation(from, to, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
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
