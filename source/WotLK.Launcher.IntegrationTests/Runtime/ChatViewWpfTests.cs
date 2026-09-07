using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static class ChatViewWpfTests
{
    private static int _checks;
    private static readonly Guid Session = Guid.Parse("43e20968-4137-4f23-9174-4a097cc6a873");
    private static readonly DateTimeOffset Date = DateTimeOffset.Parse("2026-09-06T18:24:00Z");

    public static async Task<int> RunAsync(string? captureDirectory = null)
    {
        try
        {
            _checks = 0;
            ValidateDraftLifecycle();
            ValidatePresentation();
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Exception? failure = null;
                dispatcher.UnhandledException += (_, args) => { failure ??= args.Exception; args.Handled = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
                _ = ExecuteAsync();
                Dispatcher.Run();
                if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);

                async Task ExecuteAsync()
                {
                    Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    try
                    {
                        foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                        await ValidateViewAsync(captureDirectory);
                        Equal(0, application.Windows.Count, "La validation doit rester entièrement en mémoire, sans fenêtre native.");
                    }
                    catch (Exception error) { failure ??= error; }
                    finally { application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }
            }) { IsBackground = true, Name = "AtlasChatMemoryWpfHarness" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
            Console.WriteLine($"Chat UI WPF OK: {_checks} assertions; drafts, retries, session isolation, real bindings/events, FR/EN and in-memory rendering. No native window, popup or keyboard input.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static ChatViewState Selected(uint friend = 91) => new()
    {
        SessionId = Session, OwnerAccountId = 42, SelectedFriendAccountId = friend,
        SelectedFriendUsername = friend == 91 ? "AmiAtlas" : "SecondFriend", IsAvailable = true, CanSend = true,
        Conversations = [new(91, "AmiAtlas", "On se retrouve à Dalaran ?", Date, 3), new(92, "SecondFriend", "À tout à l’heure !", Date.AddMinutes(-15), 0)],
        UnreadCount = 3
    };

    private static void ValidateDraftLifecycle()
    {
        ChatUiState state = new();
        state.Draft = "interdit";
        Equal(string.Empty, state.Draft, "Un compte déconnecté ne conserve aucun brouillon.");
        True(!state.CanEditDraft && !state.CanSend && state.TryBeginSend() is null, "La saisie et l’envoi exigent une identité de session.");
        state.ApplyState(Selected());
        state.Draft = "  Bonjour\r\nAmiAtlas  ";
        ChatSendRequestedEventArgs first = state.TryBeginSend() ?? throw new InvalidOperationException("L’envoi valide manque.");
        Equal((uint)91, first.AccountId, "Le destinataire est le compte Atlas sélectionné.");
        Equal(Session, first.SessionId, "L’envoi est lié à la génération de session.");
        Equal("Bonjour\nAmiAtlas", first.Body, "L’envoi normalise CRLF et les espaces périphériques.");
        True(first.ClientMessageId != Guid.Empty && state.IsSending && state.TryBeginSend() is null, "Un envoi en cours possède un UUID et bloque les doubles clics.");
        Equal("  Bonjour\r\nAmiAtlas  ", state.Draft, "L’envoi ne supprime pas le brouillon avant confirmation.");
        state.CompleteSend(first.ClientMessageId, false);
        True(state.CanSend && state.HasError && state.ErrorMessage.Contains("brouillon", StringComparison.Ordinal), "Un échec conserve le texte et affiche l’erreur locale.");
        state.SetLocale("en-US");
        True(state.ErrorMessage.Contains("draft", StringComparison.Ordinal), "Une erreur de repli est retraduite avec la langue.");
        ChatSendRequestedEventArgs retry = state.TryBeginSend()!;
        Equal(first.ClientMessageId, retry.ClientMessageId, "Réessayer le même texte réutilise son UUID idempotent.");
        state.Draft = "Un autre message pendant la requête";
        state.CompleteSend(retry.ClientMessageId, true);
        Equal("Un autre message pendant la requête", state.Draft, "La confirmation ne supprime pas une saisie plus récente.");
        ChatSendRequestedEventArgs fresh = state.TryBeginSend()!;
        True(fresh.ClientMessageId != first.ClientMessageId, "Un texte différent reçoit un nouvel UUID.");
        state.CompleteSend(fresh.ClientMessageId, false, "Erreur explicite");
        state.Draft = "Texte corrigé";
        True(!state.HasError, "Corriger le brouillon efface l’erreur précédente.");
        ChatSendRequestedEventArgs corrected = state.TryBeginSend()!;
        True(corrected.ClientMessageId != fresh.ClientMessageId, "Un brouillon corrigé ne réutilise pas l’UUID du texte précédent.");
        state.ApplyState(Selected(92));
        Equal(string.Empty, state.Draft, "Un nouvel ami commence avec son propre brouillon.");
        state.Draft = "Brouillon du second ami";
        state.CompleteSend(corrected.ClientMessageId, true);
        Equal("Brouillon du second ami", state.Draft, "Une confirmation tardive d’un autre ami respecte le brouillon courant.");
        state.ApplyState(Selected());
        Equal(string.Empty, state.Draft, "Le brouillon confirmé de l’autre ami est supprimé.");
        state.ApplyState(Selected(92));
        Equal("Brouillon du second ami", state.Draft, "La navigation restaure le brouillon associé à cet ami.");
        state.ApplyState(Selected(92) with { IsAvailable = false });
        True(state.CanEditDraft && !state.CanSend, "Une indisponibilité conserve la saisie mais bloque l’envoi.");
        Equal("Brouillon du second ami", state.Draft, "Une indisponibilité conserve le brouillon.");
        state.ApplyState(Selected(92));
        ChatSendRequestedEventArgs old = state.TryBeginSend()!;
        state.ApplyState(Selected(92) with { SessionId = Guid.NewGuid() });
        state.Draft = "Nouvelle session";
        state.CompleteSend(old.ClientMessageId, false, "Ancienne erreur");
        True(!state.HasError && !state.IsSending, "Une réponse d’une ancienne session ne pollue pas la nouvelle.");
        Equal("Nouvelle session", state.Draft, "Une réponse tardive conserve le nouveau brouillon.");
        state.ApplyState(Selected() with { OwnerAccountId = 84 });
        Equal(string.Empty, state.Draft, "Changer de compte purge tous les brouillons.");
        state.Draft = new string('a', 1000);
        True(state.CanSend && state.DraftLengthText == "1000 / 1000", "La limite de 1 000 unités UTF-16 est acceptée.");
        state.Draft += "b";
        True(!state.CanSend, "Un texte trop long ne peut pas être envoyé.");
        state.Draft = " \t\r\n ";
        True(!state.CanSend && state.DraftLengthText == "0 / 1000", "Un brouillon blanc est vide après normalisation.");
        state.ApplyState(new ChatViewState { Conversations = default, Messages = default });
        True(!state.HasConversations && state.Messages.IsEmpty && state.ShowsNoSelection, "La déconnexion accepte aussi les collections par défaut.");
        Equal(string.Empty, state.Draft, "La déconnexion efface le texte privé.");
    }

    private static void ValidatePresentation()
    {
        ChatUiState state = new();
        state.ApplyState(Selected() with { UnreadCount = 120, HasEarlier = true, Messages =
        [new(9, 42, "MonAtlas", "Réponse", "launcher", Date), new(7, 91, "AmiAtlas", "Depuis le jeu", "game", Date)] });
        Equal(120, state.UnreadCount, "Le total non lu provient du compteur global serveur.");
        Equal("99+", state.UnreadText, "Le badge global borne son affichage.");
        True(state.Conversations[0].IsSelected && state.Conversations[0].HasUnread, "La conversation conserve sa sélection et son compteur local.");
        Equal("3", state.Conversations[0].UnreadText, "Le compteur de la conversation reste distinct du total.");
        Equal(7L, state.Messages[0].Id, "L’historique est trié par identifiant canonique.");
        True(!state.Messages[0].IsOwn && state.Messages[1].IsOwn, "Les bulles utilisent le compte expéditeur pour leur direction.");
        Equal("En jeu", state.Messages[0].OriginText, "L’origine du jeu est traduite en français.");
        True(state.CanLoadEarlier, "Un historique tronqué disponible permet le chargement antérieur.");
        state.SetLocale("en-GB");
        Equal("In game", state.Messages[0].OriginText, "L’origine est retraduite en anglais.");
        Equal("Send", state.SendText, "L’action d’envoi est traduite.");
        True(state.Conversations[0].AccessibleName.Contains("unread messages", StringComparison.Ordinal), "Le nom accessible du badge suit la langue.");
        state.ApplyState(state.Current with { IsLoadingEarlier = true });
        True(!state.CanLoadEarlier && state.EarlierText == "Loading…", "Un chargement antérieur en cours bloque une seconde demande.");
        state.ApplyState(state.Current with { UnreadCount = -5 });
        True(!state.HasUnread && state.UnreadText == "0", "Un compteur négatif n’est pas affiché.");
        Equal("?", ChatStrings.Initial("  "), "Un nom vide reçoit un monogramme sûr.");
        Equal("😀", ChatStrings.Initial("😀Ami"), "Le monogramme conserve le graphème Unicode complet.");
        True(new ChatStrings(false).ErrorForCode("chat-not-friends").Contains("amis", StringComparison.Ordinal), "Le retrait d’amitié possède un message dédié.");
    }

    private static async Task ValidateViewAsync(string? captures)
    {
        ChatViewV2 view = new();
        Border host = new() { Width = 1597.6, Height = 996.8, Background = new SolidColorBrush(Color.FromRgb(8, 20, 32)), Child = view };
        ChatViewState populated = Selected() with { HasEarlier = true, Messages =
        [new(200, 91, "AmiAtlas", "Salut ! On se retrouve à Dalaran avant de partir en donjon ?", "game", Date),
         new(201, 42, "MonAtlas", "Oui, j’arrive dans cinq minutes.\nJe termine de préparer mon équipement.", "launcher", Date.AddMinutes(1)),
         new(202, 91, "AmiAtlas", "Parfait, je t’attends près de la fontaine. À tout de suite !", "game", Date.AddMinutes(2))] };
        view.ApplyState(populated);
        await LayoutAsync(host);
        TextBox composer = Required<TextBox>(view, "ComposerBox");
        Button send = Required<Button>(view, "SendButton");
        True(composer.IsEnabled && !send.IsEnabled, "Les bindings autorisent la saisie mais bloquent l’envoi vide.");
        Equal(1000, composer.MaxLength, "Le champ WPF limite le texte à 1 000 unités UTF-16.");
        True(composer.AcceptsReturn && composer.TextWrapping == TextWrapping.Wrap, "Le champ prend en charge le texte multiligne.");
        Equal("Texte du message", AutomationProperties.GetName(composer), "Le champ possède un nom accessible français.");
        composer.Text = "À tout de suite !";
        await LayoutAsync(host);
        Equal(composer.Text, view.UiState.Draft, "Le vrai champ WPF transmet son texte au brouillon.");
        True(send.IsEnabled, "La saisie valide active le bouton Envoyer.");
        List<ChatSendRequestedEventArgs> sends = [];
        view.SendRequested += (_, args) => sends.Add(args);
        True(!view.HandleComposerKey(Key.Enter, ModifierKeys.Shift), "Maj+Entrée reste une insertion de ligne.");
        True(!view.HandleComposerKey(Key.ImeProcessed, ModifierKeys.None), "Une touche traitée par l’IME n’envoie pas le message.");
        True(!view.HandleComposerKey(Key.Enter, ModifierKeys.Control), "Ctrl+Entrée ne déclenche pas un envoi involontaire.");
        Equal(0, sends.Count, "Les raccourcis de saisie ne déclenchent pas l’envoi.");
        True(view.HandleComposerKey(Key.Enter, ModifierKeys.None), "Entrée simple est traitée par la messagerie.");
        Equal(1, sends.Count, "Entrée déclenche un seul événement applicatif.");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(1, sends.Count, "Le handler protège aussi un double clic pendant l’envoi.");
        view.CompleteSend(sends[0].ClientMessageId, false);
        await LayoutAsync(host);
        True(Required<Border>(view, "ErrorNotice").Visibility == Visibility.Visible, "Un échec apparaît dans la vue.");
        Equal("À tout de suite !", composer.Text, "Un échec conserve le texte dans le vrai champ.");
        Capture(host, captures, "chat-fr-error.png");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(2, sends.Count, "Le bouton permet une nouvelle tentative après erreur.");
        Equal(sends[0].ClientMessageId, sends[1].ClientMessageId, "Le bouton réutilise l’identifiant idempotent.");
        view.CompleteSend(sends[1].ClientMessageId, true);
        await LayoutAsync(host);
        Equal(string.Empty, composer.Text, "Un succès confirmé efface le champ inchangé.");
        Capture(host, captures, "chat-fr-conversation.png");
        ChatLoadEarlierRequestedEventArgs? earlier = null;
        view.LoadEarlierRequested += (_, args) => earlier = args;
        Required<Button>(view, "LoadEarlierButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(earlier is { AccountId: 91, BeforeId: 200 }, "Le bouton de pagination transmet le plus ancien identifiant affiché.");
        ChatConversationRequestedEventArgs? requested = null;
        view.ConversationRequested += (_, args) => requested = args;
        Button other = Visuals<Button>(Required<ItemsControl>(view, "ConversationsList")).Single(button => button.DataContext is ChatConversationRow { AccountId: 92 });
        other.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(requested is { AccountId: 92, Username: "SecondFriend" }, "La conversation transmet l’identité Atlas de sa ligne.");
        True(view.FindName("BackButton") is null, "Le titre Messages ne propose plus de flèche de retour.");
        view.SetLocale("en-US");
        composer.Text = "Draft kept while the service is unavailable.";
        view.ApplyState(populated with { IsAvailable = false });
        await LayoutAsync(host);
        True(Required<Border>(view, "UnavailableNotice").Visibility == Visibility.Visible && !send.IsEnabled && composer.IsEnabled,
            "La vue indisponible affiche sa notice et préserve la saisie.");
        Equal("Message text", AutomationProperties.GetName(composer), "L’accessibilité du champ passe en anglais.");
        Equal("Send", send.Content as string, "Le bouton passe en anglais.");
        Capture(host, captures, "chat-en-unavailable.png");
        view.ApplyState(new ChatViewState { IsAvailable = true, SessionId = Session, OwnerAccountId = 42 });
        await LayoutAsync(host);
        True(Required<Grid>(view, "ConversationPanel").Visibility == Visibility.Collapsed, "Sans sélection, aucun fil privé n’est affiché.");
        Capture(host, captures, "chat-en-empty.png");
        await ValidateScrollAsync(view, host);
        True(PresentationSource.FromVisual(view) is null, "Le rendu est détaché de toute surface native du bureau.");
        host.Child = null;
    }

    private static async Task ValidateScrollAsync(ChatViewV2 view, Border host)
    {
        List<ChatViewportReadChangedEventArgs> reads = [];
        view.ViewportReadChanged += (_, args) => reads.Add(args);
        ChatMessageUiItem Message(int id) => new(id, id % 2 == 0 ? 91u : 42u, id % 2 == 0 ? "AmiAtlas" : "MonAtlas",
            $"Message de validation {id}. Une conversation avec assez de texte pour parcourir l’historique sans marquer les nouveaux messages comme lus.",
            id % 2 == 0 ? "game" : "launcher", Date.AddSeconds(id));
        ChatViewState history = Selected() with { HasEarlier = true, Messages = Enumerable.Range(100, 45).Select(Message).ToImmutableArray() };
        view.ApplyState(history);
        True(!reads.Any(item => item.AtBottom), "L’arrivée du modèle ne doit pas confirmer la lecture avant le rendu.");
        await LayoutAsync(host);
        ScrollViewer scroll = Required<ScrollViewer>(view, "MessagesScrollViewer");
        True(scroll.ScrollableHeight > 1000, "Le scénario possède un véritable historique défilant.");
        True(reads.Last() is { AtBottom: true, ThroughMessageId: 144, FriendAccountId: 91 }, "Le fond rendu confirme précisément le dernier message affiché.");
        scroll.ScrollToVerticalOffset(200);
        await LayoutAsync(host);
        True(!reads.Last().AtBottom && reads.Last().ThroughMessageId == 0, "Remonter l’historique retire immédiatement la confirmation du fond.");
        double offset = scroll.VerticalOffset;
        history = history with { Messages = history.Messages.Add(Message(145)) };
        view.ApplyState(history);
        await LayoutAsync(host);
        True(Math.Abs(scroll.VerticalOffset - offset) <= 1 && !reads.Last().AtBottom,
            "Un message entrant préserve la position quand l’utilisateur lit plus haut.");
        double previousExtent = scroll.ExtentHeight;
        offset = scroll.VerticalOffset;
        history = history with { Messages = Enumerable.Range(90, 10).Select(Message).Concat(history.Messages).ToImmutableArray() };
        view.ApplyState(history);
        await LayoutAsync(host);
        double expectedOffset = offset + scroll.ExtentHeight - previousExtent;
        True(Math.Abs(scroll.VerticalOffset - expectedOffset) <= 2 && !reads.Last().AtBottom,
            "Charger les messages antérieurs compense la hauteur ajoutée et conserve le passage consulté.");
        offset = scroll.VerticalOffset;
        view.SetLocale("fr-FR");
        await LayoutAsync(host);
        True(Math.Abs(scroll.VerticalOffset - offset) <= 1 && !reads.Last().AtBottom, "Changer de langue conserve la position dans l’historique.");
        scroll.ScrollToEnd();
        await LayoutAsync(host);
        True(reads.Last() is { AtBottom: true, ThroughMessageId: 145 }, "Revenir au fond confirme le curseur réellement visible.");
        reads.Clear();
        history = history with { Messages = history.Messages.Add(Message(146)) };
        view.ApplyState(history);
        True(reads.Count > 0 && !reads.Last().AtBottom, "Un nouveau snapshot invalide la lecture pendant sa mise en page.");
        history = history with { Messages = history.Messages.Add(Message(147)) };
        view.ApplyState(history);
        True(!reads.Any(item => item.AtBottom), "Deux snapshots rapprochés ne confirment aucun message avant le rendu.");
        await LayoutAsync(host);
        True(reads.Last() is { AtBottom: true, ThroughMessageId: 147 }, "Des snapshots rapprochés conservent le suivi du fond et confirment le bon curseur.");
        True(reads.Where(item => item.AtBottom).All(item => item.SessionId == Session && item.FriendAccountId == 91),
            "La confirmation visible reste liée au compte ami et à la génération de session.");
        view.ApplyState(new ChatViewState());
        await LayoutAsync(host);
        True(!reads.Last().AtBottom && reads.Last().ThroughMessageId == 0 && reads.Last().FriendAccountId is null,
            "La déconnexion invalide la lecture et l’identité du viewport.");
    }

    private static async Task LayoutAsync(FrameworkElement host)
    {
        for (int i = 0; i < 3; i++)
        {
            host.Measure(new Size(host.Width, host.Height));
            host.Arrange(new Rect(0, 0, host.Width, host.Height));
            host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static void Capture(FrameworkElement host, string? directory, string filename)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        RenderTargetBitmap bitmap = new((int)host.Width, (int)host.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(directory, filename));
        encoder.Save(file);
    }

    private static T Required<T>(FrameworkElement root, string name) where T : FrameworkElement => root.FindName(name) as T ?? throw new InvalidOperationException($"Contrôle {name} introuvable.");
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void True(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) { _checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}."); }
}
