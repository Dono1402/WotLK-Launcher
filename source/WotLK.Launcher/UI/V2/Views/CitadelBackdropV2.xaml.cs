using System.Windows;
using System.Windows.Controls;

namespace WotLK.Launcher.UI.V2.Views;

public partial class CitadelBackdropV2 : UserControl
{
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
