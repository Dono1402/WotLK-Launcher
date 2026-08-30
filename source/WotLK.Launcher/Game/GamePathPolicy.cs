using System.IO;

namespace WotLK.Launcher.Game;

internal static class GamePathPolicy
{
    internal static string NormalizeManifestPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    internal static string GetSafeTargetPath(string installRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Chemin vide dans le manifeste.");
        }

        string normalizedRelative = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
        {
            throw new InvalidOperationException("Chemin absolu interdit dans le manifeste: " + relativePath);
        }

        string root = Path.GetFullPath(installRoot);
        string target = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        if (!target.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Chemin hors dossier d'installation: " + relativePath);
        }

        return target;
    }
}
