using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class FriendsDrawerV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration AddFriendTransitionDuration = new(TimeSpan.FromMilliseconds(150));
    private const double AddFriendExpandedHeight = 78;
    private bool _hasOpened;
    private bool _isAddFriendExpanded;
    private Popup? _openFriendActionsPopup;
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

    internal bool IsFullyClosed => Visibility == Visibility.Collapsed
        && !IsHitTestVisible
        && !Scrim.IsHitTestVisible;

    internal TextBox SearchInput => FriendSearchBox;

    internal ScrollViewer ScrollHost => FriendsScrollViewer;

    internal bool IsAddFriendEditorOpen => _isAddFriendExpanded;

    internal bool IsFriendProfileOpen => State?.IsFriendProfileOpen == true;

    public bool ContainsKeyboardFocusTarget(DependencyObject? target)
    {
        return target is not null && IsDescendantOf(target, DrawerPanel);
    }

    public void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(IsFriendProfileOpen ? BackToFriendsButton : AddFriendToggleButton));
    }

    internal bool TryCloseTransientPanel()
    {
        if (State?.CloseFriendProfile() == true)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => Keyboard.Focus(AddFriendToggleButton));
            return true;
        }

        return TryCloseAddFriendEditor();
    }

    internal bool TryCloseAddFriendEditor()
    {
        if (!_isAddFriendExpanded)
        {
            return false;
        }

        SetAddFriendExpanded(false, animate: true);
        Keyboard.Focus(AddFriendToggleButton);
        return true;
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

        SetAddFriendExpanded(false, animate: false);
        State?.CloseFriendProfile();
        CloseFriendActionsPopup();
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

    private void AddFriendToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetAddFriendExpanded(!_isAddFriendExpanded, animate: true);
    }

    private void SetAddFriendExpanded(bool expanded, bool animate)
    {
        _isAddFriendExpanded = expanded;
        AddFriendToggleButton.Tag = expanded ? "Active" : null;
        AddFriendPanel.IsHitTestVisible = expanded;

        AddFriendPanel.BeginAnimation(MaxHeightProperty, null);
        AddFriendPanel.BeginAnimation(OpacityProperty, null);
        AddFriendTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        double targetHeight = expanded ? AddFriendExpandedHeight : 0;
        double targetOpacity = expanded ? 1 : 0;
        double targetOffset = expanded ? 0 : -5;
        if (!animate || !IsLoaded)
        {
            AddFriendPanel.MaxHeight = targetHeight;
            AddFriendPanel.Opacity = targetOpacity;
            AddFriendTranslate.Y = targetOffset;
            return;
        }

        double currentHeight = Math.Clamp(AddFriendPanel.ActualHeight, 0, AddFriendExpandedHeight);
        double currentOpacity = AddFriendPanel.Opacity;
        double currentOffset = AddFriendTranslate.Y;
        AddFriendPanel.MaxHeight = targetHeight;
        AddFriendPanel.Opacity = targetOpacity;
        AddFriendTranslate.Y = targetOffset;

        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
        DoubleAnimation heightAnimation = new(currentHeight, targetHeight, AddFriendTransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        DoubleAnimation opacityAnimation = new(currentOpacity, targetOpacity, AddFriendTransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        DoubleAnimation offsetAnimation = new(currentOffset, targetOffset, AddFriendTransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        if (expanded)
        {
            heightAnimation.Completed += (_, _) =>
            {
                if (_isAddFriendExpanded)
                {
                    Keyboard.Focus(FriendSearchBox);
                }
            };
        }

        AddFriendPanel.BeginAnimation(MaxHeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
        AddFriendPanel.BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        AddFriendTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            offsetAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TryCloseTransientPanel())
        {
            e.Handled = true;
        }
    }

    private void FriendSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || State is null)
        {
            return;
        }

        if (State.SendRequestCommand.CanExecute(null))
        {
            State.SendRequestCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void FriendActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Popup popup } button)
        {
            return;
        }

        if (!ReferenceEquals(_openFriendActionsPopup, popup))
        {
            CloseFriendActionsPopup();
        }
        popup.DataContext = button.DataContext;
        popup.IsOpen = true;
        _openFriendActionsPopup = popup;
        e.Handled = true;
    }

    private void FriendItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (State is null
            || sender is not Border { DataContext: FriendUiItem friend }
            || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        CloseFriendActionsPopup();
        SetAddFriendExpanded(false, animate: false);
        State.OpenFriendProfile(friend);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(BackToFriendsButton));
        e.Handled = true;
    }

    private void BackToFriendsButton_Click(object sender, RoutedEventArgs e)
    {
        if (State?.CloseFriendProfile() == true)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => Keyboard.Focus(AddFriendToggleButton));
        }
    }

    private void RemoveFriendMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (State is null
            || State.Current.IsPreview
            || sender is not Button { DataContext: FriendUiItem friend })
        {
            return;
        }

        CloseFriendActionsPopup();

        MessageBoxResult confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"Retirer {friend.Username} de tes amis Atlas ?",
            "Retirer un ami",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (State.RemoveFriendCommand.CanExecute(friend.AccountId))
        {
            State.RemoveFriendCommand.Execute(friend.AccountId);
        }
    }

    private void CloseFriendActionsPopup()
    {
        if (_openFriendActionsPopup is not null)
        {
            _openFriendActionsPopup.IsOpen = false;
            _openFriendActionsPopup = null;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
