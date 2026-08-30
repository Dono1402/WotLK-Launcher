using System.Windows;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Validation;

namespace WotLK.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        bool useUiV2Preview = e.Args.Any(argument =>
            string.Equals(argument, "--ui-v2", StringComparison.OrdinalIgnoreCase));

        base.OnStartup(e);

        if (useUiV2Preview)
        {
            var previewScenario = LauncherV2PreviewData.ResolveScenario(e.Args);
            try
            {
                ManropeFontValidator.ValidateOrThrow();
                LoadV2PreviewResources();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Atlas Launcher ne peut pas afficher la prévisualisation V2.\n\n{ex.Message}",
                    "Erreur de validation Manrope",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            LauncherShellV2 previewWindow = new(previewScenario);
            ApplyV2PreviewOptions(previewWindow, e.Args);
            MainWindow = previewWindow;
            previewWindow.Show();
            return;
        }

        if (GameDirectoryAccess.IsGrantAccessMode(e.Args))
        {
            Shutdown(GameDirectoryAccess.RunGrantAccess(e.Args));
            return;
        }

        if (GameInstallServices.IsGameUninstallMode(e.Args))
        {
            Shutdown(GameInstallServices.RunGameUninstall(e.Args));
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void LoadV2PreviewResources()
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
