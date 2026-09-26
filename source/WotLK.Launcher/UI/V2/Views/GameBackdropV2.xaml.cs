using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WotLK.Launcher.UI.V2.Views;

// The original game artwork, separated from interactive content so the shell can
// blend whole-window backgrounds without rasterizing text or controls.
public partial class GameBackdropV2 : UserControl
{
    private ResourceDictionary? _minecraftPalette;
    public static readonly DependencyProperty IsMinecraftThemeProperty = DependencyProperty.Register(
        nameof(IsMinecraftTheme), typeof(bool), typeof(GameBackdropV2), new PropertyMetadata(false, ThemeChanged));

    public GameBackdropV2() => InitializeComponent();
    public bool IsMinecraftTheme
    {
        get => (bool)GetValue(IsMinecraftThemeProperty);
        set => SetValue(IsMinecraftThemeProperty, value);
    }

    private static void ThemeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (GameBackdropV2)sender;
        bool minecraft = (bool)e.NewValue;
        if (minecraft)
        {
            view._minecraftPalette ??= new ResourceDictionary
            {
                Source = new Uri("/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.MinecraftGame.xaml", UriKind.Relative)
            };
            view.Resources.MergedDictionaries.Add(view._minecraftPalette);
        }
        else if (view._minecraftPalette is not null) view.Resources.MergedDictionaries.Remove(view._minecraftPalette);
        view.HeroArtwork.Visibility = minecraft ? Visibility.Collapsed : Visibility.Visible;
        view.BaseArtworkBrush.AlignmentX = minecraft ? AlignmentX.Center : AlignmentX.Right;
    }
}
