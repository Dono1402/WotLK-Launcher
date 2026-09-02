using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class AddonsViewV2 : UserControl
{
    private AddonsUiState? _subscribedState;
    private bool _isApplyingState;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AddonsUiState),
        typeof(AddonsViewV2),
        new PropertyMetadata(null, StateChanged));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(AdaptiveLayoutMode),
        typeof(AddonsViewV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, LayoutModeChanged));

    public AddonsViewV2()
    {
        InitializeComponent();
        Loaded += AddonsViewV2_Loaded;
        Unloaded += AddonsViewV2_Unloaded;
    }

    public AddonsUiState? State
    {
        get => (AddonsUiState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public AdaptiveLayoutMode LayoutMode
    {
        get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    internal ListBox ListHost => AddonList;

    internal TextBox SearchBox => SearchInput;

    internal FrameworkElement DetailsHost => DetailPanel;

    internal FrameworkElement DeleteConfirmationHost => DeleteConfirmationPanel;

    internal bool IsDetailOpen => State?.Current.IsDetailOpen == true;

    internal bool IsDeleteConfirmationOpen => State?.Current.IsDeleteConfirmationOpen == true;

    internal void OnNavigatedAway()
    {
        State?.OnNavigatedAway();
        AddonList.SelectedItem = null;
    }

    internal bool TryCloseTopLayer()
    {
        if (IsDeleteConfirmationOpen)
        {
            State?.CancelRemove();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => Keyboard.Focus(RemoveSelectedAddonButton));
            return true;
        }

        if (!IsDetailOpen)
        {
            return false;
        }

        CloseDetails();
        return true;
    }

    private static void StateChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        AddonsViewV2 view = (AddonsViewV2)dependencyObject;
        view.ReplaceStateSubscription(
            args.OldValue as AddonsUiState,
            args.NewValue as AddonsUiState);
        view.ApplyState();
    }

    private static void LayoutModeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((AddonsViewV2)dependencyObject).ApplyLayout((AdaptiveLayoutMode)args.NewValue);
    }

    private void AddonsViewV2_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToState(State);
        ApplyLayout(LayoutMode);
        ApplyState();
    }

    private void AddonsViewV2_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromState(_subscribedState);
    }

    private void ApplyLayout(AdaptiveLayoutMode mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        ContentFrame.MaxWidth = mode switch
        {
            AdaptiveLayoutMode.Wide => 1180,
            AdaptiveLayoutMode.Compact => 1080,
            _ => 900
        };
        ContentFrame.Width = ContentFrame.MaxWidth;
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(34, 26, 34, 28),
            AdaptiveLayoutMode.Compact => new Thickness(28, 24, 28, 26),
            _ => new Thickness(22, 20, 22, 22)
        };
        PageTitle.FontSize = mode switch
        {
            AdaptiveLayoutMode.Wide => 30,
            AdaptiveLayoutMode.Compact => 29,
            _ => 28
        };
        SearchField.Width = mode == AdaptiveLayoutMode.Stacked ? 268 : 310;
        DetailPanel.Width = mode == AdaptiveLayoutMode.Stacked ? 360 : 390;
    }

    private void ApplyState()
    {
        if (!IsInitialized || State is null)
        {
            return;
        }

        _isApplyingState = true;
        try
        {
            AddonsViewState current = State.Current;
            AllFilterButton.Tag = current.Filter == AddonCatalogFilter.All ? "Active" : null;
            InstalledFilterButton.Tag = current.Filter == AddonCatalogFilter.Installed ? "Active" : null;
            UpdatesFilterButton.Tag = current.Filter == AddonCatalogFilter.Updates ? "Active" : null;
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(current.SearchText)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!string.Equals(SearchInput.Text, current.SearchText, StringComparison.Ordinal))
            {
                SearchInput.Text = current.SearchText;
                SearchInput.CaretIndex = SearchInput.Text.Length;
            }

            UpdateAllButton.Visibility = current.UpdateCount > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            AddonList.SelectedItem = current.IsDetailOpen
                ? current.SelectedAddon
                : null;
            DeleteConfirmationTitle.Text = current.SelectedAddon is null
                ? "Supprimer cet addon ?"
                : $"Supprimer {current.SelectedAddon.Name} ?";

            if (current.IsDeleteConfirmationOpen)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    () => Keyboard.Focus(CancelDeleteButton));
            }
            else if (current.IsDetailOpen)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    () => Keyboard.Focus(CloseDetailButton));
            }
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_isApplyingState)
        {
            State?.UpdateSearch(SearchInput.Text);
        }
    }

    private void AllFilterButton_Click(object sender, RoutedEventArgs e) =>
        State?.SelectFilter(AddonCatalogFilter.All);

    private void InstalledFilterButton_Click(object sender, RoutedEventArgs e) =>
        State?.SelectFilter(AddonCatalogFilter.Installed);

    private void UpdatesFilterButton_Click(object sender, RoutedEventArgs e) =>
        State?.SelectFilter(AddonCatalogFilter.Updates);

    private void UpdateAllButton_Click(object sender, RoutedEventArgs e) =>
        State?.UpdateAll();

    private void AddonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || AddonList.SelectedItem is not AddonUiItem addon)
        {
            return;
        }

        State?.OpenDetails(addon.Id);
    }

    private void AddonPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AddonUiItem addon })
        {
            State?.InvokePrimary(addon.Id);
            e.Handled = true;
        }
    }

    private void DetailPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (State?.Current.SelectedAddon is AddonUiItem addon)
        {
            State.InvokePrimary(addon.Id);
        }
    }

    private void RemoveSelectedAddonButton_Click(object sender, RoutedEventArgs e) =>
        State?.RequestRemoveSelected();

    private void CancelDeleteButton_Click(object sender, RoutedEventArgs e) =>
        State?.CancelRemove();

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e) =>
        State?.ConfirmRemove();

    private void CloseDetailButton_Click(object sender, RoutedEventArgs e) =>
        CloseDetails();

    private void DetailBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseDetails();
        e.Handled = true;
    }

    private void DetailPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void CloseDetails()
    {
        State?.CloseDetails();
        AddonList.SelectedItem = null;
        Keyboard.Focus(SearchInput);
    }

    private void ReplaceStateSubscription(AddonsUiState? previous, AddonsUiState? current)
    {
        UnsubscribeFromState(previous);
        if (IsLoaded)
        {
            SubscribeToState(current);
        }
    }

    private void SubscribeToState(AddonsUiState? state)
    {
        if (state is null || ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        UnsubscribeFromState(_subscribedState);
        _subscribedState = state;
        state.PropertyChanged += State_PropertyChanged;
    }

    private void UnsubscribeFromState(AddonsUiState? state)
    {
        if (state is null || !ReferenceEquals(_subscribedState, state))
        {
            return;
        }

        state.PropertyChanged -= State_PropertyChanged;
        _subscribedState = null;
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(AddonsUiState.Current))
        {
            ApplyState();
        }
    }
}
