using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class WalletBalanceV2 : UserControl
{
    private ShopUiState? _state;
    private bool _euros;
    private static readonly DependencyProperty AnimatedCentsProperty = DependencyProperty.Register(
        "AnimatedCents", typeof(double), typeof(WalletBalanceV2), new PropertyMetadata(0d, (target, args) =>
            ((WalletBalanceV2)target).AnimatedAmountText.Text = ShopUiState.FormatEuros((long)Math.Round((double)args.NewValue))));
    public WalletBalanceV2()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (_state is not null) _state.PropertyChanged -= StateChanged; StopAnimation(); WalletPopup.IsOpen = false; };
    }
    private void Attach()
    {
        if (_state is not null) _state.PropertyChanged -= StateChanged;
        _state = DataContext as ShopUiState;
        if (_state is not null) _state.PropertyChanged += StateChanged;
        UpdateDisplay();
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_state?.HasOffers != true) StopAnimation();
        UpdateDisplay();
    }
    private void UpdateDisplay()
    {
        WalletKindText.Text = _euros ? "Euros" : _state?.CreditsLabel;
        WalletAmountText.Text = _euros ? _state?.EuroBalance ?? "—" : _state?.CreditBalance ?? "—";
    }
    private void Wallet_Click(object sender, RoutedEventArgs e) => WalletPopup.IsOpen = !WalletPopup.IsOpen;
    private void Credits_Click(object sender, RoutedEventArgs e) { _euros = false; StopAnimation(); UpdateDisplay(); WalletPopup.IsOpen = false; }
    private void Euros_Click(object sender, RoutedEventArgs e) { _euros = true; StopAnimation(); UpdateDisplay(); WalletPopup.IsOpen = false; }
    internal void CloseMenu() => WalletPopup.IsOpen = false;
    internal void AnimateCredit(ShopCreditChange change)
    {
        StopAnimation(); _euros = false; UpdateDisplay();
        if (!SystemParameters.ClientAreaAnimation) return;
        WalletAmountText.Opacity = 0; AnimatedAmountText.Visibility = Visibility.Visible;
        AnimatedAmountText.Text = ShopUiState.FormatEuros(change.BeforeCents);
        SetValue(AnimatedCentsProperty, (double)change.BeforeCents);
        DoubleAnimation counter = new(change.BeforeCents, change.AfterCents, TimeSpan.FromMilliseconds(600))
        { BeginTime = TimeSpan.FromMilliseconds(750), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        counter.Completed += (_, _) => StopAnimation();
        BeginAnimation(AnimatedCentsProperty, counter);
        CreditPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(650)) { BeginTime = TimeSpan.FromMilliseconds(700) });
    }
    internal void StopAnimation()
    {
        BeginAnimation(AnimatedCentsProperty, null);
        CreditPulse.BeginAnimation(OpacityProperty, null); CreditPulse.Opacity = 0;
        AnimatedAmountText.Visibility = Visibility.Collapsed; WalletAmountText.Opacity = 1;
    }
}
