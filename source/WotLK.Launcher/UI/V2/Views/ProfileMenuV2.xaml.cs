using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ProfileMenuV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(140));
    private bool _hasOpened;
    private int _transitionVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(ProfileUiState),
        typeof(ProfileMenuV2),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(ProfileMenuV2),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            IsOpenChanged));

    public ProfileMenuV2()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyOpenState(IsOpen, animate: false);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? Closed;

    public ProfileUiState? State
    {
        get => (ProfileUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    internal bool IsFullyClosed => Visibility == Visibility.Collapsed
        && !IsHitTestVisible;

    internal bool ContainsTarget(DependencyObject? target)
    {
        return target is not null && IsDescendantOf(target, MenuPanel);
    }

    internal void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(LogoutButton.IsEnabled ? LogoutButton : CloseProfileButton));
    }

    internal void DetachFromShell()
    {
        ++_transitionVersion;
        MenuPanel.BeginAnimation(OpacityProperty, null);
        MenuTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        IsHitTestVisible = false;
        MenuPanel.Opacity = 0;
        MenuTranslate.Y = -8;
        Visibility = Visibility.Collapsed;
    }

    private static void IsOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ProfileMenuV2 menu = (ProfileMenuV2)dependencyObject;
        if (menu.IsLoaded)
        {
            menu.ApplyOpenState((bool)args.NewValue, animate: true);
        }
    }

    private void ApplyOpenState(bool isOpen, bool animate)
    {
        int transitionVersion = ++_transitionVersion;
        double currentOpacity = MenuPanel.Opacity;
        double currentOffset = MenuTranslate.Y;
        MenuPanel.BeginAnimation(OpacityProperty, null);
        MenuTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (isOpen)
        {
            _hasOpened = true;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            if (!animate)
            {
                MenuPanel.Opacity = 1;
                MenuTranslate.Y = 0;
                FocusFirstControl();
                return;
            }

            AnimateTo(currentOpacity, currentOffset, 1, 0, transitionVersion, opening: true);
            return;
        }

        IsHitTestVisible = false;
        if (!animate || Visibility != Visibility.Visible)
        {
            CompleteClose();
            return;
        }

        AnimateTo(currentOpacity, currentOffset, 0, -8, transitionVersion, opening: false);
    }

    private void AnimateTo(
        double fromOpacity,
        double fromOffset,
        double targetOpacity,
        double targetOffset,
        int transitionVersion,
        bool opening)
    {
        CubicEase ease = new()
        {
            EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn
        };
        MenuPanel.Opacity = targetOpacity;
        MenuTranslate.Y = targetOffset;
        DoubleAnimation opacity = CreateAnimation(fromOpacity, targetOpacity, ease);
        DoubleAnimation offset = CreateAnimation(fromOffset, targetOffset, ease);
        opacity.Completed += (_, _) =>
        {
            if (transitionVersion != _transitionVersion || IsOpen != opening)
            {
                return;
            }

            MenuPanel.BeginAnimation(OpacityProperty, null);
            MenuTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            if (opening)
            {
                FocusFirstControl();
            }
            else
            {
                CompleteClose();
            }
        };
        MenuPanel.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
        MenuTranslate.BeginAnimation(TranslateTransform.YProperty, offset, HandoffBehavior.SnapshotAndReplace);
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
        MenuPanel.Opacity = 0;
        MenuTranslate.Y = -8;
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
