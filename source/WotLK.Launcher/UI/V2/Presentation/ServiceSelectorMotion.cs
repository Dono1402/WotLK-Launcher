using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WotLK.Launcher.UI.V2.Presentation;

// Keep the existing ComboBox's selection, keyboard navigation and automation.
// Only defer its popup's visual closure long enough to finish the fade.
public static class ServiceSelectorMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ServiceSelectorMotion), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static readonly DependencyProperty IsPopupVisibleProperty = DependencyProperty.RegisterAttached(
        "IsPopupVisible", typeof(bool), typeof(ServiceSelectorMotion), new PropertyMetadata(false));
    public static bool GetIsPopupVisible(DependencyObject target) => (bool)target.GetValue(IsPopupVisibleProperty);
    public static void SetIsPopupVisible(DependencyObject target, bool value) => target.SetValue(IsPopupVisibleProperty, value);
    private static readonly DependencyProperty MotionProperty = DependencyProperty.RegisterAttached(
        "Motion", typeof(Motion), typeof(ServiceSelectorMotion));
    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ComboBox selector) return;
        (selector.GetValue(MotionProperty) as Motion)?.Dispose();
        selector.SetValue(MotionProperty, (bool)args.NewValue ? new Motion(selector) : null);
    }

    private sealed class Motion : IDisposable
    {
        private readonly ComboBox _selector;
        private readonly DependencyPropertyDescriptor _dropDown = DependencyPropertyDescriptor.FromProperty(
            ComboBox.IsDropDownOpenProperty, typeof(ComboBox));
        private int _generation;
        public Motion(ComboBox selector)
        {
            _selector = selector;
            selector.Loaded += Loaded;
            selector.Unloaded += Unloaded;
            if (selector.IsLoaded) Loaded(selector, new RoutedEventArgs());
        }
        private void Loaded(object sender, RoutedEventArgs args)
        {
            _dropDown.RemoveValueChanged(_selector, Changed);
            _dropDown.AddValueChanged(_selector, Changed);
            SystemParameters.StaticPropertyChanged -= PreferencesChanged;
            SystemParameters.StaticPropertyChanged += PreferencesChanged;
        }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            _dropDown.RemoveValueChanged(_selector, Changed);
            SystemParameters.StaticPropertyChanged -= PreferencesChanged;
            ++_generation;
            SetIsPopupVisible(_selector, false);
        }
        private void PreferencesChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                _selector.Dispatcher.BeginInvoke(new Action(() => Apply(animate: false)));
        }
        private void Changed(object? sender, EventArgs args) => Apply(SystemParameters.ClientAreaAnimation);
        private void Apply(bool animate)
        {
            _selector.ApplyTemplate();
            if (_selector.Template.FindName("ServiceMenuSurface", _selector) is not Border surface
                || _selector.Template.FindName("ServiceChevron", _selector) is not RotateTransform chevron) return;
            int generation = ++_generation;
            bool open = _selector.IsDropDownOpen;
            bool wasVisible = GetIsPopupVisible(_selector);
            double opacity = wasVisible ? surface.Opacity : 0;
            double y = wasVisible ? ((TranslateTransform)surface.RenderTransform).Y : -6;
            double angle = chevron.Angle;
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            var offset = (TranslateTransform)surface.RenderTransform;
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            chevron.BeginAnimation(RotateTransform.AngleProperty, null);
            surface.IsHitTestVisible = open;
            surface.Opacity = open ? 1 : 0;
            offset.Y = open ? 0 : -4;
            chevron.Angle = open ? 180 : 0;
            if (open) SetIsPopupVisible(_selector, true);
            if (!animate)
            {
                SetIsPopupVisible(_selector, open);
                return;
            }
            var duration = TimeSpan.FromMilliseconds(open ? 180 : 120);
            var fade = new DoubleAnimation(opacity, surface.Opacity, duration) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                if (generation != _generation) return;
                if (!open) SetIsPopupVisible(_selector, false);
                surface.BeginAnimation(UIElement.OpacityProperty, null);
            };
            surface.BeginAnimation(UIElement.OpacityProperty, fade);
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, offset.Y, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            chevron.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, chevron.Angle, duration)
                { FillBehavior = FillBehavior.Stop });
        }
        public void Dispose()
        {
            Unloaded(_selector, new RoutedEventArgs());
            _selector.Loaded -= Loaded;
            _selector.Unloaded -= Unloaded;
        }
    }
}
