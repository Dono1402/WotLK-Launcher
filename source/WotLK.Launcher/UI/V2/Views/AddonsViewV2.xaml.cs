using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class AddonsViewV2 : UserControl
{
    private AddonsUiState? _subscribedState;
    private bool _isApplyingState;
    private bool _wasDetailOpen;
    private bool _wasDeleteConfirmationOpen;
    private string? _focusedAddonId;

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

    internal bool ContainsDeleteConfirmationFocus(DependencyObject? target) =>
        target is not null && (ReferenceEquals(target, DeleteConfirmationPanel)
            || DeleteConfirmationPanel.IsAncestorOf(target));

    internal void FocusDeleteConfirmation()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsDeleteConfirmationOpen && CancelDeleteButton.IsEnabled)
                Keyboard.Focus(CancelDeleteButton);
        });
    }

    internal void OnNavigatedAway()
    {
        if (AddonList.SelectedItem is AddonUiItem selected) _focusedAddonId = selected.Id;
        State?.OnNavigatedAway();
        if (State is not null) State.IsLibraryOpen = false;
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
            if (State?.IsLibraryOpen == true)
            {
                State.IsLibraryOpen = false;
                Keyboard.Focus(LibraryToggleButton);
                return true;
            }
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
        LauncherLocalization.LocaleChanged -= AddonsLocaleChanged;
        LauncherLocalization.LocaleChanged += AddonsLocaleChanged;
        SubscribeToState(State);
        ApplyLayout(LayoutMode);
        AddonsLocaleChanged(this, EventArgs.Empty);
    }

    private void AddonsViewV2_Unloaded(object sender, RoutedEventArgs e)
    {
        LauncherLocalization.LocaleChanged -= AddonsLocaleChanged;
        UnsubscribeFromState(_subscribedState);
    }

    private void AddonsLocaleChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => AddonsLocaleChanged(sender, e)));
            return;
        }
        _isApplyingState = true;
        try
        {
            CategorySelector.ItemsSource = null;
            SortSelector.ItemsSource = null;
            PackSelector.ItemsSource = null;
        }
        finally { _isApplyingState = false; }
        State?.RefreshLocalizedText();
        ApplyState();
    }

    private void ApplyLayout(AdaptiveLayoutMode mode)
    {
        if (!IsInitialized)
        {
            return;
        }

        ContentFrame.MaxWidth = mode switch
        {
            AdaptiveLayoutMode.Wide => 1520,
            AdaptiveLayoutMode.Compact => 1400,
            _ => 1180
        };
        ContentFrame.Width = double.NaN;
        ContentFrame.Margin = mode switch
        {
            AdaptiveLayoutMode.Wide => new Thickness(64, 6, 64, 20),
            AdaptiveLayoutMode.Compact => new Thickness(36, 10, 36, 20),
            _ => new Thickness(24, 12, 24, 18)
        };
        PageTitle.FontSize = mode switch
        {
            AdaptiveLayoutMode.Wide => 48,
            AdaptiveLayoutMode.Compact => 42,
            _ => 38
        };
        SearchField.Width = double.NaN;
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
            FavoritesFilterButton.Tag = current.Filter == AddonCatalogFilter.Favorites ? "Active" : null;
            ManualFilterButton.Tag = current.Filter == AddonCatalogFilter.Manual ? "Active" : null;
            LibraryToggleButton.Tag = State.IsLibraryOpen ? "Active" : null;
            if (!CategorySelector.Items.Cast<AddonLibraryChoice>().SequenceEqual(State.CategoryChoices))
                CategorySelector.ItemsSource = State.CategoryChoices;
            CategorySelector.SelectedValue = current.CategoryFilter;
            CategorySelector.IsEnabled = current.IsInteractive;
            SortSelector.ItemsSource ??= AddonsUiState.SortChoices;
            SortSelector.SelectedValue = current.SortOrder.ToString();
            SortSelector.IsEnabled = current.IsInteractive;
            PackSelector.ItemsSource ??= AddonsUiState.Packs;
            PackSelector.SelectedValue = State.SelectedPackId;
            PackSelector.ToolTip = AddonsUiState.Packs.FirstOrDefault(pack => pack.Id == State.SelectedPackId)?.DisplayDescription;
            if (!ProfileSelector.Items.Cast<AddonSelectionProfile>().SequenceEqual(State.Profiles))
                ProfileSelector.ItemsSource = State.Profiles;
            ProfileSelector.SelectedValue = State.SelectedProfileName;
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(current.SearchText)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!string.Equals(SearchInput.Text, current.SearchText, StringComparison.Ordinal))
            {
                SearchInput.Text = current.SearchText;
                SearchInput.CaretIndex = SearchInput.Text.Length;
            }

            UpdateAllButton.Visibility = current.ShowsUpdateAll
                ? Visibility.Visible
                : Visibility.Collapsed;
            AddonList.SelectedItem = current.IsDetailOpen
                ? current.SelectedAddon
                : current.VisibleAddons.FirstOrDefault(addon => addon.Id == _focusedAddonId);
            DeleteConfirmationTitle.Text = current.SelectedAddon is null
                ? "Désinstaller cet addon ?"
                : $"Désinstaller {current.SelectedAddon.Name} ?";

            bool openedDeleteConfirmation = current.IsDeleteConfirmationOpen && !_wasDeleteConfirmationOpen;
            bool closedDeleteConfirmation = !current.IsDeleteConfirmationOpen && _wasDeleteConfirmationOpen;
            bool openedDetails = current.IsDetailOpen && !_wasDetailOpen;
            _wasDeleteConfirmationOpen = current.IsDeleteConfirmationOpen;
            _wasDetailOpen = current.IsDetailOpen;
            if (openedDeleteConfirmation)
            {
                FocusDeleteConfirmation();
            }
            else if (current.IsDetailOpen && (openedDetails || closedDeleteConfirmation))
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    () =>
                    {
                        if (IsDetailOpen && !IsDeleteConfirmationOpen)
                            Keyboard.Focus(closedDeleteConfirmation ? RemoveSelectedAddonButton : CloseDetailButton);
                    });
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

    private void FavoritesFilterButton_Click(object sender, RoutedEventArgs e) => State?.SelectFilter(AddonCatalogFilter.Favorites);

    private void ManualFilterButton_Click(object sender, RoutedEventArgs e) => State?.SelectFilter(AddonCatalogFilter.Manual);

    private void CategorySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingState && CategorySelector.SelectedValue is string category) State?.SelectCategory(category);
    }

    private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingState && SortSelector.SelectedValue is string value && Enum.TryParse(value, out AddonSortOrder sort))
            State?.SelectSort(sort);
    }

    private void LibraryToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (State is null) return;
        State.IsLibraryOpen = !State.IsLibraryOpen;
        ApplyState();
        if (State.IsLibraryOpen) Keyboard.Focus(PackSelector);
    }

    private void PackSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingState && State is not null && PackSelector.SelectedValue is string id)
        {
            State.SelectedPackId = id;
            PackSelector.ToolTip = AddonsUiState.Packs.FirstOrDefault(pack => pack.Id == id)?.DisplayDescription;
        }
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingState && State is not null && ProfileSelector.SelectedValue is string name) State.SelectedProfileName = name;
    }

    private void LoadPackButton_Click(object sender, RoutedEventArgs e) { if (State is not null) State.ApplyPack(State.SelectedPackId); }
    private void LoadProfileButton_Click(object sender, RoutedEventArgs e) { if (State is not null) State.LoadProfile(State.SelectedProfileName); }
    private void RemoveProfileButton_Click(object sender, RoutedEventArgs e) { if (State is not null) State.RemoveProfile(State.SelectedProfileName); }
    private void SaveProfileButton_Click(object sender, RoutedEventArgs e) => State?.SaveProfile();
    private void SelectVisibleButton_Click(object sender, RoutedEventArgs e) => State?.SelectVisibleAddons();
    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e) => State?.ClearSelection();

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        State?.UpdateSearch(string.Empty);
        Keyboard.Focus(SearchInput);
    }

    private void AddonsRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsDeleteConfirmationOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && TryFocusSearch())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !IsDetailOpen && SearchInput.IsKeyboardFocusWithin && State?.HasSearch == true)
        {
            State.UpdateSearch(string.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !IsDetailOpen && !IsActionControl(e.OriginalSource as DependencyObject)
                 && AddonList.IsKeyboardFocusWithin && AddonList.SelectedItem is AddonUiItem addon)
        {
            State?.OpenDetails(addon.Id);
            e.Handled = true;
        }
    }

    internal bool TryFocusSearch()
    {
        if (IsDeleteConfirmationOpen || State is null) return false;
        if (IsDetailOpen) State.CloseDetails();
        Keyboard.Focus(SearchInput);
        SearchInput.SelectAll();
        return true;
    }

    private void UpdateAllButton_Click(object sender, RoutedEventArgs e) =>
        State?.UpdateAll();

    private void AddonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || AddonList.SelectedItem is not AddonUiItem addon)
        {
            return;
        }

        _focusedAddonId = addon.Id;
    }

    private void AddonList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsActionControl(source)) return;
        if (ItemsControl.ContainerFromElement(AddonList, source) is ListBoxItem { DataContext: AddonUiItem addon })
        {
            State?.OpenDetails(addon.Id);
            e.Handled = true;
        }
    }

    private static bool IsActionControl(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = ParentOf(current))
        {
            if (current is ButtonBase or TextBoxBase or ComboBox) return true;
            if (current is ListBoxItem) return false;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject current) => current is Visual
        ? VisualTreeHelper.GetParent(current)
        : current is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(current);

    private void FavoriteAddonButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AddonUiItem addon })
        {
            _focusedAddonId = addon.Id;
            State?.ToggleFavorite(addon.Id);
            RestoreRowFocus(addon.Id, "FavoriteAddonButton");
        }
        e.Handled = true;
    }

    private void AddonSelectionCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: AddonUiItem addon } check)
        {
            _focusedAddonId = addon.Id;
            State?.SetSelected(addon.Id, check.IsChecked == true);
            RestoreRowFocus(addon.Id, "AddonSelectionCheck");
        }
        e.Handled = true;
    }

    private void RestoreRowFocus(string addonId, string controlName)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            AddonUiItem? current = State?.Current.VisibleAddons.FirstOrDefault(addon => addon.Id == addonId);
            if (current is null) { Keyboard.Focus(SearchInput); return; }
            if (AddonList.ItemContainerGenerator.ContainerFromItem(current) is DependencyObject container)
            {
                UIElement? target = Descendants<FrameworkElement>(container).FirstOrDefault(element => element.Name == controlName);
                if (target is not null) Keyboard.Focus(target);
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private void AddonMoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void VerifyAddonMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: AddonUiItem addon } && State?.VerifyCommand.CanExecute(addon.Id) == true)
            State.VerifyCommand.Execute(addon.Id);
        e.Handled = true;
    }

    private void ReinstallAddonMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: AddonUiItem addon } && State?.ReinstallCommand.CanExecute(addon.Id) == true)
            State.ReinstallCommand.Execute(addon.Id);
        e.Handled = true;
    }

    private void AddonPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AddonUiItem addon })
        {
            State?.InvokePrimary(addon.Id);
            e.Handled = true;
        }
    }

    private void AddonDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AddonUiItem addon })
        {
            State?.OpenDetails(addon.Id);
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
