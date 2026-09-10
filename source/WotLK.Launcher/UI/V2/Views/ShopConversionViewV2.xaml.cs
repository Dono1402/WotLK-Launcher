using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopConversionViewV2 : UserControl
{
    private ShopUiState? State => DataContext as ShopUiState;
    internal Point? CreditOrigin { get; private set; }
    public ShopConversionViewV2()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ConversionFrame.MaxHeight = Math.Max(200, ActualHeight - 40);
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(FocusFirstControl); };
    }
    internal void FocusFirstControl() { if (IsVisible) ConversionCharacterPicker.Focus(); }
    private void Close_Click(object sender, RoutedEventArgs e) => State?.CloseConversion();
    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (State?.CanConvert != true) return;
        if (Window.GetWindow(this) is Window shell)
            CreditOrigin = ConversionCreditText.TranslatePoint(new Point(ConversionCreditText.ActualWidth / 2, ConversionCreditText.ActualHeight / 2), shell);
        State.TryConvertPreview();
    }
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, CultureInfo.InvariantCulture, out int percent)) State?.SetConversionPercent(percent);
    }
    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider slider && (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin)) State?.SetConversionPercent((int)Math.Round(e.NewValue));
    }
    private void Amount_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !ValidInsertion(e.Text);
    private void Amount_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !ValidInsertion(text)) e.CancelCommand();
    }
    private bool ValidInsertion(string text) => ShopUiState.IsNumericGoldInput(ConversionAmount.Text.Remove(ConversionAmount.SelectionStart, ConversionAmount.SelectionLength).Insert(ConversionAmount.SelectionStart, text));
}
