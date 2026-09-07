using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;

namespace WotLK.Launcher.UI.V2.Commands;

internal interface IAddonLibraryDialogs
{
    bool ConfirmInstall(Window owner, AddonsPlanPreview plan);
    string? OpenProfile(Window owner);
    string? SaveProfile(Window owner);
}

internal sealed class AddonLibraryDialogs : IAddonLibraryDialogs
{
    public string? OpenProfile(Window owner)
    {
        OpenFileDialog dialog = new()
        {
            Title = L("Importer une sélection d’addons", "Import an addon selection"),
            Filter = L("Sélections Atlas (*.json)|*.json", "Atlas selections (*.json)|*.json"),
            CheckFileExists = true, Multiselect = false
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? SaveProfile(Window owner)
    {
        SaveFileDialog dialog = new()
        {
            Title = L("Exporter la sélection d’addons", "Export the addon selection"),
            FileName = "atlas-addons.json", DefaultExt = ".json", AddExtension = true,
            Filter = L("Sélections Atlas (*.json)|*.json", "Atlas selections (*.json)|*.json"),
            OverwritePrompt = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public bool ConfirmInstall(Window owner, AddonsPlanPreview plan)
    {
        if (!plan.IsValid || plan.Items.IsEmpty) return false;
        Window dialog = CreateConfirmation(owner, plan);
        return dialog.ShowDialog() == true;
    }

    // Construction is separate from ShowDialog so an isolated fixture can inspect
    // the concrete plan without opening a native dialog or writing to a game folder.
    internal static Window CreateConfirmation(Window owner, AddonsPlanPreview plan)
    {
        Brush foreground = new SolidColorBrush(Color.FromRgb(233, 238, 241));
        Brush secondary = new SolidColorBrush(Color.FromRgb(161, 180, 196));
        Window dialog = new()
        {
            Owner = owner, Title = L("Confirmer les addons", "Confirm addons"),
            Width = 580, SizeToContent = SizeToContent.Height, MaxHeight = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, AllowsTransparency = true, Background = Brushes.Transparent,
            FontFamily = owner.TryFindResource("AtlasV2.Font.Inter") as FontFamily ?? new FontFamily("Segoe UI"),
            Foreground = foreground, FontSize = 15
        };
        Grid grid = new();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock heading = new()
        {
            Text = L("Installer les composants sélectionnés", "Install selected components"),
            FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        };
        grid.Children.Add(heading);
        TextBlock explanation = new()
        {
            Margin = new Thickness(0, 10, 0, 16), FontSize = 12, Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Text = plan.RequiresExternalReplacementConfirmation
                ? L("Les dépendances nécessaires sont incluses. Les installations manuelles indiquées seront remplacées par les versions du catalogue Atlas.",
                    "Required dependencies are included. The indicated manual installations will be replaced with the Atlas catalogue versions.")
                : L("Les dépendances nécessaires sont incluses et seront installées avant les addons qui les utilisent.",
                    "Required dependencies are included and will be installed before the addons that use them.")
        };
        Grid.SetRow(explanation, 1); grid.Children.Add(explanation);
        StackPanel items = new();
        foreach (AddonsPlanItem item in plan.Items)
        {
            StackPanel text = new();
            text.Children.Add(new TextBlock
            {
                Text = item.Name + (string.IsNullOrWhiteSpace(item.Version) ? string.Empty : " · " + item.Version),
                FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            string action = item.Action switch
            {
                AddonsRequestedAction.Update => L("Mettre à jour", "Update"),
                AddonsRequestedAction.Repair => L("Réparer", "Repair"),
                AddonsRequestedAction.Reinstall => L("Réinstaller", "Reinstall"),
                _ => L("Installer", "Install")
            };
            string detail = action
                + (item.IsDependency ? L(" · Dépendance requise", " · Required dependency") : string.Empty)
                + (item.ReplacesExternal ? L(" · Remplace une installation manuelle", " · Replaces a manual installation") : string.Empty);
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            if (item.ReplacesExternal)
                text.Children.Add(new TextBlock { Text = string.Join(", ", item.Folders), FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            items.Children.Add(new Border
            {
                Child = text, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 7),
                CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(22, 39, 54))
            });
        }
        ScrollViewer scroll = new() { Content = items, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        Button cancel = ActionButton(owner, L("Annuler", "Cancel"), "AtlasV2.Button.GhostCompact");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        Button confirm = ActionButton(owner, L("Confirmer l’installation", "Confirm installation"), "AtlasV2.Button.Primary");
        confirm.IsDefault = true; confirm.Margin = new Thickness(10, 0, 0, 0);
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        dialog.Content = new Border
        {
            Child = grid, Padding = new Thickness(24), CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(59, 82, 103)),
            Background = new SolidColorBrush(Color.FromRgb(12, 25, 37))
        };
        return dialog;
    }

    private static Button ActionButton(Window owner, string text, string styleKey)
    {
        Button button = new() { Content = text, MinHeight = 38, Padding = new Thickness(14, 6, 14, 6), FontSize = 15 };
        if (owner.TryFindResource(styleKey) is Style style) button.Style = style;
        button.IsTabStop = true;
        button.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    private static string L(string french, string english) => LauncherLocalization.IsEnglish ? english : french;
}
