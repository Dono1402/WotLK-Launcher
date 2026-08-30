using System.Windows;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Validation;

namespace WotLK.Launcher;

internal enum LauncherStartupMode
{
    Legacy,
    UiV2,
    UiV2Preview,
    GrantGameDirectoryAccess,
    UninstallGame
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LauncherStartupMode startupMode = ResolveStartupMode(e.Args);

        base.OnStartup(e);

        if (startupMode is LauncherStartupMode.UiV2 or LauncherStartupMode.UiV2Preview)
        {
            try
            {
                ManropeFontValidator.ValidateOrThrow();
                LoadV2Resources();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Atlas Launcher ne peut pas afficher l'interface V2.\n\n{ex.Message}",
                    "Erreur de validation Manrope",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            if (startupMode == LauncherStartupMode.UiV2Preview)
            {
                GamePreviewScenario previewScenario = LauncherV2PreviewData.ResolveScenario(e.Args);
                LauncherShellV2 previewWindow = new(previewScenario);
                ApplyV2PreviewOptions(previewWindow, e.Args);
                MainWindow = previewWindow;
                previewWindow.Show();
                return;
            }

            StartRuntimeV2();
            return;
        }

        if (startupMode == LauncherStartupMode.GrantGameDirectoryAccess)
        {
            Shutdown(GameDirectoryAccess.RunGrantAccess(e.Args));
            return;
        }

        if (startupMode == LauncherStartupMode.UninstallGame)
        {
            Shutdown(GameInstallServices.RunGameUninstall(e.Args));
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    internal static LauncherStartupMode ResolveStartupMode(IEnumerable<string> arguments)
    {
        string[] args = arguments as string[] ?? arguments.ToArray();
        bool useUiV2 = args.Any(argument =>
            string.Equals(argument, "--ui-v2", StringComparison.OrdinalIgnoreCase));
        if (useUiV2)
        {
            bool usePreview = args.Any(argument =>
                argument.StartsWith("--preview-state=", StringComparison.OrdinalIgnoreCase));
            return usePreview ? LauncherStartupMode.UiV2Preview : LauncherStartupMode.UiV2;
        }

        if (GameDirectoryAccess.IsGrantAccessMode(args))
        {
            return LauncherStartupMode.GrantGameDirectoryAccess;
        }

        return GameInstallServices.IsGameUninstallMode(args)
            ? LauncherStartupMode.UninstallGame
            : LauncherStartupMode.Legacy;
    }

    private void StartRuntimeV2()
    {
        LauncherRuntime runtime;
        try
        {
            runtime = LauncherRuntime.CreateProduction();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Atlas Launcher ne peut pas lire l'installation locale.\n\n{ex.Message}",
                "Erreur de démarrage V2",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ShellUiState shellState = LauncherV2RuntimePresentation.CreateShell(runtime);
        LauncherShellV2 window = new(
            shellState,
            LauncherV2RuntimePresentation.CreateGame(runtime.LocalClient),
            LauncherV2RuntimePresentation.CreateFriends());

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = async (_, _) =>
        {
            window.Loaded -= loadedHandler;
            LauncherSessionRestoreResult result = await runtime.InitializeAsync();
            if (!runtime.IsDisposed && window.IsVisible)
            {
                LauncherV2RuntimePresentation.ApplySession(shellState, result);
            }
        };
        window.Loaded += loadedHandler;
        window.Closed += (_, _) =>
        {
            window.Loaded -= loadedHandler;
            runtime.Dispose();
        };

        MainWindow = window;
        window.Show();
    }

    private void LoadV2Resources()
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];

        foreach (string resourcePath in resourcePaths)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
        }
    }

    private static void ApplyV2PreviewOptions(LauncherShellV2 window, IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
        {
            const string sizePrefix = "--ui-v2-size=";
            if (argument.StartsWith(sizePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string[] dimensions = argument[sizePrefix.Length..].Split(['x', 'X']);
                if (dimensions.Length == 2
                    && double.TryParse(dimensions[0], out double width)
                    && double.TryParse(dimensions[1], out double height))
                {
                    window.Width = Math.Max(window.MinWidth, width);
                    window.Height = Math.Max(window.MinHeight, height);
                }
            }

            if (string.Equals(argument, "--ui-v2-friends-open", StringComparison.OrdinalIgnoreCase))
            {
                window.SetFriendsDrawerOpenForPreview();
            }
        }
    }
}
