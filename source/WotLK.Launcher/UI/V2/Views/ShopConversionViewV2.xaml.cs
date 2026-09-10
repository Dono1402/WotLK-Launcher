using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopConversionViewV2 : UserControl
{
    private ShopUiState? State => DataContext as ShopUiState;
    private CancellationTokenSource? _transfer;
    internal bool IsTransferring => _transfer is not null;
    public ShopConversionViewV2()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            ConversionFrame.MaxHeight = Math.Max(200, ActualHeight - 40);
            bool compact = ActualWidth < 1250;
            ConversionFrame.Margin = new Thickness(compact ? 24 : 60, 20, compact ? 24 : 60, 20);
            GoldSourceCard.Padding = GoldAmountCard.Padding = CreditResultCard.Padding = new Thickness(compact ? 18 : 26);
            ConversionTitle.FontSize = compact ? 32 : 40;
            ConversionCreditText.FontSize = compact ? 32 : 40;
            MaximumGoldText.FontSize = compact ? 32 : 38;
            ConversionTitle.LineHeight = compact ? 40 : 50;
            CloseConversionButton.Margin = new Thickness(0, 0, 0, compact ? 10 : 18);
            ConversionToolbar.Margin = compact ? new Thickness(0, 12, 0, 14) : new Thickness(0, 19, 0, 20);
            GoldAvailableRow.Height = ConversionCreditText.Height = compact ? 58 : 66;
            GoldAvailableRow.Margin = new Thickness(0, 13, 0, compact ? 16 : 22);
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(FocusFirstControl); else CancelTransfer(); };
        Unloaded += (_, _) => CancelTransfer();
        DataContextChanged += (_, _) => CancelTransfer();
    }
    internal void FocusFirstControl() { if (IsVisible) ConversionCharacterPicker.Focus(); }
    private void Close_Click(object sender, RoutedEventArgs e) { CancelTransfer(); State?.CloseConversion(); }
    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { CanConvert: true } state || _transfer is not null) return;
        object? snapshot = state.ConversionSnapshot;
        ShopCharacterRow? character = state.SelectedConversionCharacter;
        string requested = state.ConversionGold;
        using CancellationTokenSource transfer = new();
        _transfer = transfer;
        GoldAmountCard.IsEnabled = false;
        ConversionCharacterPicker.SetCurrentValue(IsEnabledProperty, false);
        ConvertButton.SetCurrentValue(IsEnabledProperty, false);
        try
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                AnimateTransfer();
                await Task.Delay(860, transfer.Token);
            }
            // Navigation, session replacement or a concurrent refresh invalidates this operation.
            if (!transfer.IsCancellationRequested && IsVisible && ReferenceEquals(State, state)
                && ReferenceEquals(snapshot, state.ConversionSnapshot)
                && ReferenceEquals(character, state.SelectedConversionCharacter) && requested == state.ConversionGold)
                state.TryConvertPreview();
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_transfer, transfer)) { _transfer = null; RestoreControls(); }
        }
    }
    private void AnimateTransfer()
    {
        TransferLayer.Children.Clear();
        Point origin = MaximumGoldText.TranslatePoint(new Point(-22, MaximumGoldText.ActualHeight / 2), TransferLayer);
        Point destination = ConversionCreditText.TranslatePoint(new Point(12, ConversionCreditText.ActualHeight / 2), TransferLayer);
        for (int i = 0; i < 4; i++)
        {
            Image coin = new() { Source = (ImageSource)FindResource("ShopGoldCoin"), Width = 22, Height = 22, Opacity = 0, IsHitTestVisible = false };
            TransferLayer.Children.Add(coin);
            Canvas.SetLeft(coin, origin.X); Canvas.SetTop(coin, origin.Y - 11);
            TimeSpan delay = TimeSpan.FromMilliseconds(i * 50);
            coin.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(origin.X, destination.X, TimeSpan.FromMilliseconds(640))
            { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            DoubleAnimationUsingKeyFrames arc = new() { BeginTime = delay };
            arc.KeyFrames.Add(new LinearDoubleKeyFrame(origin.Y - 11, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            arc.KeyFrames.Add(new SplineDoubleKeyFrame(Math.Min(origin.Y, destination.Y) - 76, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310)), new KeySpline(.2, .6, .4, 1)));
            arc.KeyFrames.Add(new SplineDoubleKeyFrame(destination.Y - 11, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640)), new KeySpline(.4, 0, .8, .4)));
            coin.BeginAnimation(Canvas.TopProperty, arc);
            DoubleAnimationUsingKeyFrames fade = new() { BeginTime = delay };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(510))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640))));
            coin.BeginAnimation(OpacityProperty, fade);
        }
    }
    private void CancelTransfer()
    {
        CancellationTokenSource? transfer = _transfer; _transfer = null;
        transfer?.Cancel(); RestoreControls();
    }
    private void RestoreControls()
    {
        TransferLayer.Children.Clear();
        GoldAmountCard.IsEnabled = true;
        ConversionCharacterPicker.SetCurrentValue(IsEnabledProperty, State?.HasCharacters == true);
        ConvertButton.SetCurrentValue(IsEnabledProperty, State?.CanConvert == true);
    }
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_transfer is null && sender is Button { Tag: string tag } && int.TryParse(tag, CultureInfo.InvariantCulture, out int percent)) State?.SetConversionPercent(percent);
    }
    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_transfer is null && sender is Slider slider && (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin)) State?.SetConversionPercent((int)Math.Round(e.NewValue));
    }
    private void Amount_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !ValidInsertion(e.Text);
    private void Amount_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !ValidInsertion(text)) e.CancelCommand();
    }
    private bool ValidInsertion(string text) => ShopUiState.IsNumericGoldInput(ConversionAmount.Text.Remove(ConversionAmount.SelectionStart, ConversionAmount.SelectionLength).Insert(ConversionAmount.SelectionStart, text));
}
