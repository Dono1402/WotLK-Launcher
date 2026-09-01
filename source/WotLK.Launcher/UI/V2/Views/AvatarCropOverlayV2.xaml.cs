using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class AvatarCropOverlayV2 : UserControl
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(140));
    private AvatarCropUiState? _subscribedState;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartX;
    private double _dragStartY;
    private int _transitionVersion;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AvatarCropUiState),
        typeof(AvatarCropOverlayV2),
        new PropertyMetadata(null, StateChanged));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(AvatarCropOverlayV2),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            IsOpenChanged));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(AdaptiveLayoutMode),
        typeof(AvatarCropOverlayV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, LayoutModeChanged));

    public AvatarCropOverlayV2()
    {
        InitializeComponent();
        Loaded += AvatarCropOverlayV2_Loaded;
        Unloaded += AvatarCropOverlayV2_Unloaded;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? Closed;

    public AvatarCropUiState? State
    {
        get => (AvatarCropUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public AdaptiveLayoutMode LayoutMode
    {
        get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    internal bool IsFullyClosed => Visibility == Visibility.Collapsed && !IsHitTestVisible;

    internal bool IsBusy => State?.Current.Status == AvatarCropPreviewStatus.Uploading;

    internal void FocusFirstControl()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(IsBusy ? DialogPanel : CloseCropButton));
    }

    internal bool ContainsKeyboardFocusTarget(DependencyObject? target)
    {
        return target is not null && IsDescendantOf(target, DialogPanel);
    }

    internal void DetachFromShell()
    {
        ++_transitionVersion;
        DialogPanel.BeginAnimation(OpacityProperty, null);
        DialogTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        UnsubscribeFromState(_subscribedState);
    }

    private static void StateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        AvatarCropOverlayV2 overlay = (AvatarCropOverlayV2)dependencyObject;
        overlay.ReplaceStateSubscription(args.OldValue as AvatarCropUiState, args.NewValue as AvatarCropUiState);
        overlay.ApplyState();
    }

    private static void IsOpenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        AvatarCropOverlayV2 overlay = (AvatarCropOverlayV2)dependencyObject;
        if (overlay.IsLoaded)
        {
            overlay.ApplyOpenState((bool)args.NewValue, animate: true);
        }
    }

    private static void LayoutModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((AvatarCropOverlayV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void AvatarCropOverlayV2_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToState(State);
        ApplyLayout(LayoutMode);
        ApplyState();
        ApplyOpenState(IsOpen, animate: false);
    }

    private void AvatarCropOverlayV2_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromState(_subscribedState);
        EndDrag();
    }

    private void ApplyLayout(AdaptiveLayoutMode mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        DialogPanel.Width = mode switch
        {
            AdaptiveLayoutMode.Wide => 960,
            AdaptiveLayoutMode.Compact => 930,
            _ => 980
        };
        bool stacked = mode == AdaptiveLayoutMode.Stacked;
        CropColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(510);
        CropGapColumn.Width = stacked ? new GridLength(0) : new GridLength(28);
        Grid.SetColumn(PreviewColumn, stacked ? 0 : 2);
        Grid.SetRow(PreviewColumn, stacked ? 1 : 0);
        CropWorkspace.RowDefinitions.Clear();
        CropWorkspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (stacked)
        {
            CropWorkspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PreviewColumn.Margin = new Thickness(0, 24, 0, 0);
        }
        else
        {
            PreviewColumn.Margin = new Thickness(0);
        }
    }

    private void ApplyState()
    {
        if (!IsInitialized || State is null)
        {
            return;
        }

        AvatarCropViewState state = State.Current;
        if (IsOpen != state.IsOpen)
        {
            SetCurrentValue(IsOpenProperty, state.IsOpen);
        }

        bool uploading = state.Status == AvatarCropPreviewStatus.Uploading;
        bool error = state.Status == AvatarCropPreviewStatus.Error;
        CropErrorBanner.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        UploadStatusBanner.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        SaveCropLabel.Text = uploading ? "Envoi…" : "Utiliser la photo";
        SaveCropButton.IsEnabled = !uploading;
        CancelCropButton.IsEnabled = !uploading;
        CloseCropButton.IsEnabled = !uploading;
        CropEditorColumn.IsEnabled = !uploading;

        ZoomSlider.Value = state.Zoom;
        ApplyTransform(state.Zoom, state.OffsetX, state.OffsetY, publish: false);
    }

    private void ApplyOpenState(bool isOpen, bool animate)
    {
        int version = ++_transitionVersion;
        DialogPanel.BeginAnimation(OpacityProperty, null);
        DialogTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (isOpen)
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            if (!animate)
            {
                DialogPanel.Opacity = 1;
                DialogTranslate.Y = 0;
                FocusFirstControl();
                return;
            }

            AnimateTo(DialogPanel.Opacity, DialogTranslate.Y, 1, 0, version, opening: true);
            return;
        }

        IsHitTestVisible = false;
        if (!animate || Visibility != Visibility.Visible)
        {
            CompleteClose();
            return;
        }

        AnimateTo(DialogPanel.Opacity, DialogTranslate.Y, 0, 10, version, opening: false);
    }

    private void AnimateTo(double fromOpacity, double fromOffset, double opacity, double offset, int version, bool opening)
    {
        CubicEase ease = new() { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };
        DoubleAnimation opacityAnimation = new(fromOpacity, opacity, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        DoubleAnimation offsetAnimation = new(fromOffset, offset, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        DialogPanel.Opacity = opacity;
        DialogTranslate.Y = offset;
        opacityAnimation.Completed += (_, _) =>
        {
            if (version != _transitionVersion || IsOpen != opening)
            {
                return;
            }

            DialogPanel.BeginAnimation(OpacityProperty, null);
            DialogTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            if (opening)
            {
                FocusFirstControl();
            }
            else
            {
                CompleteClose();
            }
        };
        DialogPanel.BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        DialogTranslate.BeginAnimation(TranslateTransform.YProperty, offsetAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void CompleteClose()
    {
        DialogPanel.Opacity = 0;
        DialogTranslate.Y = 10;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTransform(double zoom, double offsetX, double offsetY, bool publish)
    {
        double maxOffset = 180 * Math.Max(0, zoom - 1);
        double x = Math.Clamp(offsetX, -maxOffset, maxOffset);
        double y = Math.Clamp(offsetY, -maxOffset, maxOffset);
        CropImageScale.ScaleX = zoom;
        CropImageScale.ScaleY = zoom;
        CropImageTranslate.X = x;
        CropImageTranslate.Y = y;
        ZoomValueText.Text = $"{Math.Round(zoom * 100):0} %";

        double relativeX = x / 360;
        double relativeY = y / 360;
        foreach (ImageBrush brush in new[] { Preview32Brush, Preview64Brush, Preview128Brush })
        {
            brush.RelativeTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(zoom, zoom, 0.5, 0.5),
                    new TranslateTransform(relativeX, relativeY)
                }
            };
        }

        if (publish)
        {
            State?.SetTransform(zoom, x, y);
        }
    }

    private void ReplaceStateSubscription(AvatarCropUiState? oldState, AvatarCropUiState? newState)
    {
        UnsubscribeFromState(oldState);
        SubscribeToState(newState);
    }

    private void SubscribeToState(AvatarCropUiState? state)
    {
        if (state is null || ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        state.PropertyChanged += State_PropertyChanged;
        _subscribedState = state;
    }

    private void UnsubscribeFromState(AvatarCropUiState? state)
    {
        if (state is null || !ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        state.PropertyChanged -= State_PropertyChanged;
        _subscribedState = null;
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ApplyState();

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || State is null)
        {
            return;
        }

        ApplyTransform(e.NewValue, State.Current.OffsetX, State.Current.OffsetY, publish: true);
    }

    private void CropViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        _isDragging = true;
        _dragStart = e.GetPosition(CropViewport);
        _dragStartX = State?.Current.OffsetX ?? 0;
        _dragStartY = State?.Current.OffsetY ?? 0;
        CropViewport.CaptureMouse();
        e.Handled = true;
    }

    private void CropViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || State is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(CropViewport);
        ApplyTransform(
            State.Current.Zoom,
            _dragStartX + current.X - _dragStart.X,
            _dragStartY + current.Y - _dragStart.Y,
            publish: true);
    }

    private void CropViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void CropViewport_LostMouseCapture(object sender, MouseEventArgs e) => _isDragging = false;

    private void EndDrag()
    {
        _isDragging = false;
        if (CropViewport.IsMouseCaptured)
        {
            CropViewport.ReleaseMouseCapture();
        }
    }

    private void SaveCropButton_Click(object sender, RoutedEventArgs e)
    {
        State?.StartUploadPreview();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBusy)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OverlayScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsBusy && ReferenceEquals(e.OriginalSource, OverlayScrim))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
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
}
