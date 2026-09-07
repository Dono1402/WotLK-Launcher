using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class FriendsRightClickMemoryTests
{
    private static int _checks;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task<int> RunAsync(string? captures = null, bool expectOldBehavior = false)
    {
        TaskCompletionSource<int> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            async Task ExecuteAsync()
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    _checks = 0;
                    foreach (string path in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + path, UriKind.Relative) });
                    await ValidateAsync(captures, expectOldBehavior);
                    True(application.Windows.Count == 0, "Aucune fenêtre native n’a été créée.");
                    Console.WriteLine($"Friends right-click {(expectOldBehavior ? "BASELINE BUG REPRODUCED" : "FIX PASS")}: {_checks} assertions; WPF routed down/up and real Popup auto-close handler; no HWND, capture, keyboard focus or OS input.");
                    completed.SetResult(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); completed.SetResult(1); }
                finally { application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true, Name = "AtlasFriendsRightClickMemoryTests" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await completed.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }

    private static async Task ValidateAsync(string? captures, bool expectOld)
    {
        FriendsUiState state = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.Populated);
        FriendUiItem friend = state.Current.Friends[0] with { CanRemove = true };
        state.ApplyRuntimeView(state.Current with { IsRuntimeConnected = true, IsPreview = false, Friends = [friend], IncomingRequests = [], OutgoingRequests = [] });
        FriendsDrawerV2 drawer = new() { State = state, IsOpen = true, Visibility = Visibility.Visible, IsHitTestVisible = true };
        Border host = new() { Width = 1597.6, Height = 996.8, Background = Brushes.DarkSlateBlue, Child = drawer };
        await LayoutAsync(host);
        Border card = Visuals<Border>(drawer).Single(value => value.Name == "FriendCard");
        Button cardContent = Visuals<Button>(card).Single(value => value.Name == "OpenFriendProfileButton");
        Popup popup = card.Tag as Popup ?? throw new InvalidOperationException("Le menu de la carte manque.");

        // Keep the real Popup and handlers, but remove only its visual parent. Its
        // real, unshown PlacementTarget makes WPF defer native window creation.
        // We never load this tree, call Show, capture the mouse or change OS focus.
        Panel popupParent = VisualTreeHelper.GetParent(popup) as Panel ?? throw new InvalidOperationException("Le parent du popup manque.");
        popupParent.Children.Remove(popup);
        True(PresentationSource.FromVisual(popup.PlacementTarget) is null && PresentationSource.FromVisual(host) is null, "La cible de placement reste sans source native.");
        List<FriendPublicProfileRequestedEventArgs> profiles = [];
        List<FriendPublicProfileRequestedEventArgs> messages = [];
        drawer.PublicProfileRequested += (_, args) => profiles.Add(args);
        drawer.MessageRequested += (_, args) => messages.Add(args);

        RaiseRight(cardContent, Mouse.PreviewMouseDownEvent);
        True(popup.IsOpen == expectOld, expectOld
            ? "La build précédente ouvre le popup dès l’appui droit."
            : "L’appui droit ne doit pas capturer le relâchement qui termine ce même clic.");
        if (expectOld)
        {
            SimulateCapturedOutsideButton(popup, Mouse.PreviewMouseUpEvent);
            True(!popup.IsOpen, "Le vrai handler WPF referme le menu sur le relâchement du clic initial.");
            Console.WriteLine("Evidence: right-button DOWN opened IsOpen=true; WPF captured right-button UP at the menu's top margin changed IsOpen=false.");
        }
        else
        {
            RaiseRight(cardContent, Mouse.PreviewMouseUpEvent);
            True(popup.IsOpen, "Le relâchement droit ouvre le menu après la fin du clic.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            True(popup.IsOpen, "Le menu reste ouvert après le cycle complet d’entrée et de dispatcher.");
            True(!drawer.IsFriendProfileOpen && profiles.Count == 0 && messages.Count == 0,
                "Le clic droit complet n’ouvre ni le mini profil ni une navigation.");
            True(popup.Placement == PlacementMode.MousePoint, "Le clic droit conserve le placement au pointeur.");
            True(drawer.ContainsKeyboardFocusTarget(popup.Child), "Le piège de focus du tiroir reconnaît le menu actif.");
            Button profileButton = Visuals<Button>(popup.Child).Single(value => value.Name == "ViewPublicProfileMenuItem");
            Button messageButton = Visuals<Button>(popup.Child).Single(value => value.Name == "SendMessageMenuItem");
            profileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, profileButton));
            True(profiles.Count == 1 && profiles[0].AccountId == friend.AccountId && !popup.IsOpen, "Voir le profil cible l’ami et ferme le menu une seule fois.");
            profileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, profileButton));
            True(profiles.Count == 1, "Une action retardée du menu fermé est ignorée.");
            RaiseRight(card, Mouse.PreviewMouseDownEvent);
            RaiseRight(card, Mouse.PreviewMouseUpEvent);
            True(popup.IsOpen, "Le clic droit fonctionne aussi sur la bordure de la carte.");
            messageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, messageButton));
            True(messages.Count == 1 && messages[0].AccountId == friend.AccountId && !popup.IsOpen, "Envoyer un message cible le bon compte Atlas.");
            True(!Visuals<Button>(card).Any(value => value.Name == "FriendActionsButton"), "Le bouton à trois points est retiré de la ligne.");
            RaiseRight(cardContent, Mouse.PreviewMouseDownEvent);
            RaiseRight(cardContent, Mouse.PreviewMouseUpEvent);
            True(popup.IsOpen && popup.Placement == PlacementMode.MousePoint
                && ReferenceEquals(popup.PlacementTarget, cardContent), "Le clic droit conserve le menu ancré sur la ligne d’ami.");
            if (!string.IsNullOrWhiteSpace(captures)) CaptureMenu(popup, captures);
            SimulateCapturedOutsideButton(popup, Mouse.PreviewMouseDownEvent);
            True(!popup.IsOpen, "Un nouveau clic extérieur ferme toujours le menu après le correctif.");
            state.ApplyRuntimeView(state.Current with { Friends = [] });
            RaiseRight(card, Mouse.PreviewMouseUpEvent);
            True(!popup.IsOpen, "Une carte devenue obsolète ne peut pas réouvrir les actions d’un ami retiré.");
        }
        popup.IsOpen = false;
        True(PopupWindowIsAlive(popup) == false && PresentationSource.FromVisual(popup.Child) is null, "Le popup n’a créé aucun HWND.");
        host.Child = null;
    }

    private static void SimulateCapturedOutsideButton(Popup popup, RoutedEvent routedEvent)
    {
        // Exercise the installed WPF implementation instead of copying its algorithm.
        // Only its in-memory capture flag is set; Mouse.Capture is never called.
        typeof(Popup).GetMethod("CreateNewPopupRoot", PrivateInstance)!.Invoke(popup, null);
        object rootField = typeof(Popup).GetField("_popupRoot", PrivateInstance)!.GetValue(popup)!;
        FrameworkElement popupRoot = rootField as FrameworkElement
            ?? (FrameworkElement)rootField.GetType().GetProperty("Value", PrivateInstance | BindingFlags.Public)!.GetValue(rootField)!;
        popupRoot.GetType().GetProperty("Child", PrivateInstance | BindingFlags.Public)!.SetValue(popupRoot, popup.Child);
        // PopupRoot.Measure consults monitor/HWND transforms; keep this root unhosted.
        // The injected captured event deliberately represents the outside release.
        True(popup.Child is Border { Margin.Top: 4 }, "Le menu possède la marge supérieure de 4 DIP impliquée dans le bug.");
        True(popupRoot.InputHitTest(new Point(0, 0)) is null, "Le point de relâchement initial tombe hors du contenu du menu.");
        FieldInfo flagsField = typeof(Popup).GetField("_cacheValid", PrivateInstance)!;
        object flags = flagsField.GetValue(popup)!;
        PropertyInfo bit = flags.GetType().GetProperty("Item", [typeof(int)])!;
        bit.SetValue(flags, true, [1]); // Popup.CacheBits.CaptureEngaged
        flagsField.SetValue(popup, flags);
        try
        {
            MouseButtonEventArgs release = new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = routedEvent, Source = popupRoot };
            typeof(Popup).GetMethod("OnPreviewMouseButton", PrivateInstance)!.Invoke(popup, [release]);
        }
        finally
        {
            flags = flagsField.GetValue(popup)!;
            bit.SetValue(flags, false, [1]);
            flagsField.SetValue(popup, flags);
        }
    }

    private static bool PopupWindowIsAlive(Popup popup)
    {
        object helper = typeof(Popup).GetField("_secHelper", PrivateInstance)!.GetValue(popup)!;
        return (bool)helper.GetType().GetMethod("IsWindowAlive", PrivateInstance | BindingFlags.Public)!.Invoke(helper, null)!;
    }
    private static void RaiseRight(UIElement target, RoutedEvent routedEvent) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = routedEvent, Source = target });
    private static async Task LayoutAsync(FrameworkElement element)
    {
        for (int i = 0; i < 3; i++) { element.Measure(new Size(element.Width, element.Height)); element.Arrange(new Rect(0, 0, element.Width, element.Height)); element.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }
    private static void CaptureMenu(Popup popup, string directory)
    {
        FrameworkElement menu = (FrameworkElement)popup.Child;
        menu.Measure(new Size(238, 300)); menu.Arrange(new Rect(0, 0, 238, menu.DesiredSize.Height)); menu.UpdateLayout();
        RenderTargetBitmap bitmap = new(238, (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using FileStream stream = File.Create(Path.Combine(directory, "friends-menu-fixed-fr.png")); encoder.Save(stream);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (T child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void True(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
}
