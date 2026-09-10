using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class WalletBalanceV2 : UserControl
{
    private ShopUiState? _state;
    private static readonly DependencyProperty AnimatedCentsProperty = DependencyProperty.Register(
        "AnimatedCents", typeof(double), typeof(WalletBalanceV2), new PropertyMetadata(0d, (target, args) =>
            ((WalletBalanceV2)target).AnimatedAmountText.Text = ShopUiState.FormatEuros((long)Math.Round((double)args.NewValue))));
    public WalletBalanceV2()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (_state is not null) _state.PropertyChanged -= StateChanged; StopAnimation(); };
    }
    private void Attach()
    {
        if (_state is not null) _state.PropertyChanged -= StateChanged;
        _state = DataContext as ShopUiState;
        if (_state is not null) _state.PropertyChanged += StateChanged;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_state?.HasOffers != true) StopAnimation();
    }
    internal void SetCompact(bool compact)
    {
        Width = compact ? 180 : 264;
        WalletGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 20);
        WalletGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        WalletGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(EuroCell, compact ? 1 : 0); Grid.SetColumn(EuroCell, compact ? 0 : 2);
        WalletDivider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AtlasDetails.Orientation = EuroDetails.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        AtlasAmount.Margin = EuroAmountText.Margin = compact ? new Thickness(7, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        WalletKindText.VerticalAlignment = EuroKindText.VerticalAlignment = VerticalAlignment.Center;
        WalletAmountText.FontSize = AnimatedAmountText.FontSize = EuroAmountText.FontSize = compact ? 12 : 15;
        WalletAmountText.MaxWidth = AnimatedAmountText.MaxWidth = compact ? 52 : double.PositiveInfinity;
        EuroAmountText.MaxWidth = compact ? 90 : double.PositiveInfinity;
    }
    internal void AnimateCredit(ShopCreditChange change)
    {
        StopAnimation();
        if (!SystemParameters.ClientAreaAnimation) return;
        WalletAmountText.Opacity = 0; AnimatedAmountText.Visibility = Visibility.Visible;
        SetValue(AnimatedCentsProperty, (double)change.BeforeCents);
        AnimatedAmountText.Text = ShopUiState.FormatEuros(change.BeforeCents);
        DoubleAnimation counter = new(change.BeforeCents, change.AfterCents, TimeSpan.FromMilliseconds(460))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        counter.Completed += (_, _) => StopAnimation();
        BeginAnimation(AnimatedCentsProperty, counter);
    }
    internal void StopAnimation()
    {
        BeginAnimation(AnimatedCentsProperty, null);
        AnimatedAmountText.Visibility = Visibility.Collapsed; WalletAmountText.Opacity = 1;
    }
}
