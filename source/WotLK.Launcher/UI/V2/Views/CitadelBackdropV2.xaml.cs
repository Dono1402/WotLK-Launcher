using System.Windows;
using System.Windows.Controls;

namespace WotLK.Launcher.UI.V2.Views;

public partial class CitadelBackdropV2 : UserControl
{
    public static readonly DependencyProperty ShowArtworkProperty = DependencyProperty.Register(
        nameof(ShowArtwork), typeof(bool), typeof(CitadelBackdropV2),
        new PropertyMetadata(true, (target, args) => ((CitadelBackdropV2)target).ArtworkLayer.Visibility =
            (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed));
    public bool ShowArtwork
    {
        get => (bool)GetValue(ShowArtworkProperty);
        set => SetValue(ShowArtworkProperty, value);
    }
    public CitadelBackdropV2()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            CitadelFocus.Width = ActualWidth * 0.76;
            CitadelFocus.Margin = new Thickness(0, 0, -ActualWidth * 0.09, 0);
        };
    }
}
