using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class AddonsFullShellWpfTests
{
    private static int _checks;

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();

            async Task ExecuteAsync()
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string previousLocale = LauncherLocalization.CurrentLocale;
                try
                {
                    _checks = 0;
                    string directory = Path.GetFullPath(captureDirectory ?? Path.Combine(Path.GetTempPath(), "atlas-addons-shell-" + Guid.NewGuid().ToString("N")));
                    Directory.CreateDirectory(directory);
                    foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                    foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
                        await ValidateAsync(locale, directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new
                    {
                        status = "PASS", assertions = _checks,
                        method = "Fixed real WPF shell, synthetic addon catalogue, inactive offscreen WS_EX_NOACTIVATE. No live launcher, authentication, game directory or desktop input.",
                        locales = new[] { "fr-FR", "en-US" }
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"Addons full shell WPF PASS: {_checks} assertions; FR/EN, fixed shell, inactive offscreen fixture and synthetic catalogue only.");
                    completion.TrySetResult(0);
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); completion.TrySetResult(1); }
                finally { LauncherLocalization.SetLocale(previousLocale); application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true, Name = "AtlasAddonsFullShellOffscreenFixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private static async Task ValidateAsync(string locale, string directory)
    {
        LauncherLocalization.SetLocale(locale);
        string language = locale == LauncherLocalization.EnglishLocale ? "en" : "fr";
        LauncherShellV2 shell = new(GamePreviewScenario.Ready, AddonsPreviewScenario.Detail)
        {
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false
        };
        // Prevent synthetic control clicks from acquiring native keyboard focus.
        shell.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        shell.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(shell).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
        };
        shell.AddonsState.CloseDetails();
        shell.Show();
        try
        {
            await Layout(shell);
            AddonsViewV2 view = shell.AddonsPage;
            FrameworkElement content = (FrameworkElement)shell.Content;
            Check(!shell.IsActive && !shell.ShowInTaskbar, "The addon fixture never activates or appears in the taskbar.");
            Check(Math.Abs(content.ActualWidth - 1597.6) < 1 && Math.Abs(content.ActualHeight - 996.8) < 1,
                "The launcher keeps its established fixed dimensions.");
            Check(shell.CurrentPage == LauncherShellPage.Addons && view.IsVisible, "The real Addons page is selected.");
            Check(ScrollViewer.GetHorizontalScrollBarVisibility(view.ListHost) == ScrollBarVisibility.Disabled,
                "The addon list has no horizontal scrollbar.");
            Check(VirtualizingPanel.GetIsVirtualizing(view.ListHost), "The list retains virtualization.");
            Check(view.SearchBox.IsTabStop && view.SearchBox.Focusable, "Search is keyboard reachable.");
            Check(Required<TextBlock>(view, "PageTitle").FontSize <= 48, "The compact page title leaves space for the catalogue.");
            Check(Bounds(Required<Border>(view, "SearchField"), content).Right <= content.ActualWidth,
                "The search bar fits inside the fixed shell.");
            ListBoxItem firstRow = (ListBoxItem)view.ListHost.ItemContainerGenerator.ContainerFromIndex(0);
            AddonUiItem first = (AddonUiItem)firstRow.DataContext;
            TextBlock name = Descendants<TextBlock>(firstRow).Single(text => text.Text == first.Name);
            TextBlock description = Descendants<TextBlock>(firstRow).Single(text =>
                text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "Description");
            Check(name.FontSize == 15 && description.FontSize == 12, "Addon names and descriptions use the requested 15/12 px hierarchy.");
            Check(firstRow.IsTabStop, "An addon row is reachable through keyboard navigation.");
            Check(Bounds(firstRow, content).Bottom < content.ActualHeight, "The first complete addon row is visible.");
            Capture(content, Path.Combine(directory, $"addons-{language}-library.png"));
            Check(Descendants<TextBlock>(Required<ComboBox>(view, "CategorySelector")).Any(text =>
                    text.Text == (language == "en" ? "All categories" : "Toutes les catégories")),
                "The category selector renders a translated human-readable label.");
            Check(Descendants<TextBlock>(Required<ComboBox>(view, "SortSelector")).Any(text =>
                    text.Text == (language == "en" ? "Name (A–Z)" : "Nom (A–Z)")),
                "The sort selector renders its label instead of a model type.");

            view.SearchBox.Text = "Questie";
            await Layout(shell);
            Check(shell.AddonsState.Current.VisibleAddons.Length == 1
                && shell.AddonsState.Current.VisibleAddons[0].Id == "questie", "Typing in the actual search field filters the catalogue.");
            Capture(content, Path.Combine(directory, $"addons-{language}-search.png"));
            view.SearchBox.Text = "NoSyntheticAddonMatchesThisQuery";
            await Layout(shell);
            Check(shell.AddonsState.Current.VisibleAddons.Length == 0, "A query with no match produces an empty filtered list.");
            Capture(content, Path.Combine(directory, $"addons-{language}-empty-search.png"));
            view.SearchBox.Text = string.Empty;
            await Layout(shell);
            Check(shell.AddonsState.Current.VisibleAddons.Length == 13, "Clearing the filter restores the synthetic catalogue.");

            firstRow = (ListBoxItem)view.ListHost.ItemContainerGenerator.ContainerFromIndex(0);
            Click(Descendants<Button>(firstRow).Single(button => button.Name == "FavoriteAddonButton"));
            await Layout(shell);
            Check(shell.AddonsState.Current.FavoriteCount == 1 && !view.IsDetailOpen,
                "The actual favourite button updates the library without opening details.");
            Click(Required<Button>(view, "FavoritesFilterButton"));
            await Layout(shell);
            Check(shell.AddonsState.Current.VisibleAddons.Length == 1, "The favourites filter uses the newly saved favourite.");
            Click(Required<Button>(view, "AllFilterButton"));
            Click(Required<Button>(view, "LibraryToggleButton"));
            await Layout(shell);
            Check(Required<Border>(view, "LibraryPanel").IsVisible, "The packs and profiles panel opens from its button.");
            Click(Required<Button>(view, "LoadPackButton"));
            await Layout(shell);
            Check(shell.AddonsState.HasSelection, "Loading a pack prepares a catalogue selection.");
            Required<TextBox>(view, "ProfileNameInput").Text = "Synthetic raid profile";
            await Layout(shell);
            Click(Required<Button>(view, "SaveProfileButton"));
            await Layout(shell);
            Check(shell.AddonsState.Profiles.Any(profile => profile.Name == "Synthetic raid profile"),
                "The actual profile name field and save button create a selection profile.");
            Check(Descendants<TextBlock>(Required<ComboBox>(view, "ProfileSelector")).Any(text => text.Text == "Synthetic raid profile"),
                "The profile selector renders the personal profile name.");
            Check(Descendants<TextBlock>(Required<ComboBox>(view, "PackSelector")).Any(text =>
                    text.Text == (language == "en" ? "Getting started" : "Débuter")),
                "The pack selector renders its translated label.");
            Required<TextBox>(view, "ProfileNameInput").Text = "Mises à jour";
            await Layout(shell);
            Click(Required<Button>(view, "SaveProfileButton"));
            await Layout(shell);
            Check(Descendants<TextBlock>(Required<ComboBox>(view, "ProfileSelector")).Any(text => text.Text == "Mises à jour"),
                "Personal profile names retain their exact spelling even when matching a translated UI label.");
            Check(Bounds(Required<Border>(view, "LibraryPanel"), content).Right <= content.ActualWidth,
                "The open library panel fits the fixed launcher width.");
            Capture(content, Path.Combine(directory, $"addons-{language}-packs-profiles.png"));
            Click(Required<Button>(view, "LibraryToggleButton"));
            Required<ComboBox>(view, "CategorySelector").SelectedValue = first.Category;
            await Layout(shell);
            Check(shell.AddonsState.Current.VisibleAddons.All(addon => addon.Category == first.Category),
                "The actual category selector filters the addon list.");
            Required<ComboBox>(view, "CategorySelector").SelectedValue = string.Empty;
            Required<ComboBox>(view, "SortSelector").SelectedValue = "FavoritesFirst";
            await Layout(shell);
            Check(shell.AddonsState.Current.VisibleAddons[0].IsFavorite, "The sort selector puts favourites first.");
            Required<ComboBox>(view, "SortSelector").SelectedValue = "Name";

            shell.AddonsState.OpenDetails("questie");
            await Layout(shell);
            Check(view.IsDetailOpen && shell.AddonsState.Current.SelectedAddon?.Id == "questie",
                "The existing addon details remain available and target the selected addon.");
            Capture(content, Path.Combine(directory, $"addons-{language}-existing-details.png"));
            Check(view.TryFocusSearch(), "The shell can route Ctrl+F to the addon search from outside the page.");
            await Layout(shell);
            Check(!view.IsDetailOpen && !shell.IsActive, "Closing details preserves the inactive fixture.");

            AddonsViewState dependencyBatch = AddonsStateAdapter.Project(AddonsRuntimeSnapshot.Initial with
            {
                IsAuthenticated = true, IsClientPlayable = true, CanCancel = true,
                OperationState = AddonsOperationState.Installing, ActiveAddonPosition = 1, ActiveAddonTotal = 2,
                ActiveAddonId = "required-library"
            });
            Check(dependencyBatch.IsBatchOperation && dependencyBatch.ShowsUpdateAll && dependencyBatch.CanUpdateAll,
                "A single requested addon with dependencies keeps global cancellation available.");
            shell.AddonsState.ApplyRuntimeView(shell.AddonsState.Current with
            {
                IsPreview = false, IsRuntimeConnected = true, IsBatchOperation = true,
                CanCancelCurrent = true, CanMutate = false, ActiveAddonId = "questie",
                ActiveAddonPosition = 2, ActiveAddonTotal = 3
            });
            await Layout(shell);
            Check(Required<Button>(view, "UpdateAllButton").IsVisible
                    && Required<Border>(view, "BatchProgressPanel").IsVisible,
                "Global cancellation and component progress remain visible during a dependency batch.");
            Capture(content, Path.Combine(directory, $"addons-{language}-batch-progress.png"));

            AddonsPlanPreview plan = new(["feature"],
                [new("base", "Required Library", "1.0", AddonsRequestedAction.Install, true, false, ["RequiredLibrary"]),
                 new("feature", "Manually Installed Feature", "2.0", AddonsRequestedAction.Install, false, true, ["ManualFeature"])], string.Empty, []);
            Window confirmation = AddonLibraryDialogs.CreateConfirmation(shell, plan);
            FrameworkElement confirmationContent = (FrameworkElement)confirmation.Content;
            confirmationContent.Measure(new Size(580, 650));
            confirmationContent.Arrange(new Rect(0, 0, 580, confirmationContent.DesiredSize.Height));
            confirmationContent.UpdateLayout();
            Check(new WindowInteropHelper(confirmation).Handle == IntPtr.Zero,
                "Inspecting the confirmation content creates no native dialog.");
            Check(Descendants<TextBlock>(confirmationContent).Any(text => text.Text.Contains("ManualFeature", StringComparison.Ordinal)),
                "The confirmation names the exact manual folders that will be replaced.");
            Capture(confirmationContent, Path.Combine(directory, $"addons-{language}-installation-plan.png"));
            confirmation.Close();
            LauncherLocalization.SetLocale(language == "en" ? LauncherLocalization.FrenchLocale : LauncherLocalization.EnglishLocale);
            await Layout(shell);
            Check(Descendants<TextBlock>(Required<ComboBox>(view, "CategorySelector")).Any(text =>
                    text.Text == (language == "en" ? "Toutes les catégories" : "All categories")),
                "Changing language while the page is open refreshes the category label.");
            Check(shell.AddonsState.SelectedProfileName == "Mises à jour"
                    && Descendants<TextBlock>(Required<ComboBox>(view, "ProfileSelector")).Any(text => text.Text == "Mises à jour"),
                "Changing language preserves the selected profile and its personal name.");
            LauncherLocalization.SetLocale(locale);
            await Layout(shell);
        }
        finally { shell.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }

    private static async Task Layout(FrameworkElement element)
    {
        element.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(160);
        element.UpdateLayout();
    }

    private static void Capture(FrameworkElement content, string path)
    {
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path); encoder.Save(stream);
    }

    private static Rect Bounds(FrameworkElement child, Visual ancestor) => child.TransformToAncestor(ancestor).TransformBounds(new Rect(child.RenderSize));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static T Required<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException("Missing WPF control: " + name);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
