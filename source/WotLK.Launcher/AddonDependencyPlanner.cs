namespace WotLK.Launcher;

internal sealed class AddonPlanException(string code, IEnumerable<string> addonIds) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
    internal IReadOnlyList<string> AddonIds { get; } = addonIds.ToArray();
}

internal static class AddonDependencyPlanner
{
    internal static IReadOnlyList<AddonPackage> Plan(AddonCatalog catalog, IEnumerable<string> requestedIds)
    {
        Dictionary<string, AddonPackage> packages = catalog.Addons.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> visits = new(StringComparer.OrdinalIgnoreCase);
        List<string> chain = [];
        List<AddonPackage> ordered = [];
        void Visit(string id, bool dependency)
        {
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim())
                throw new AddonPlanException("invalid-dependency", [id ?? ""]);
            if (!packages.TryGetValue(id, out AddonPackage? package))
                throw new AddonPlanException(dependency ? "dependency-missing" : "addon-not-found", chain.Append(id));
            if (visits.TryGetValue(id, out int state))
            {
                if (state == 1) throw new AddonPlanException("dependency-cycle", chain.Append(id));
                return;
            }
            visits[id] = 1;
            chain.Add(package.Id);
            foreach (string required in package.Dependencies)
                Visit(required, dependency: true);
            chain.RemoveAt(chain.Count - 1);
            visits[id] = 2;
            ordered.Add(package);
        }
        foreach (string id in requestedIds) Visit(id, dependency: false);
        return ordered;
    }

    internal static IReadOnlyList<AddonPackage> FindBlockingDependents(
        AddonCatalog catalog, IEnumerable<string> installedIds, IEnumerable<string> removedIds)
    {
        HashSet<string> installed = installedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> removed = removedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AddonPackage> packages = catalog.Addons.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        bool UsesRemoved(string id, HashSet<string> seen)
        {
            if (removed.Contains(id)) return true;
            if (!seen.Add(id) || !packages.TryGetValue(id, out AddonPackage? package)) return false;
            return package.Dependencies.Any(required => UsesRemoved(required, seen));
        }
        return catalog.Addons.Where(package => installed.Contains(package.Id) && !removed.Contains(package.Id)
            && package.Dependencies.Any(required => UsesRemoved(required, new(StringComparer.OrdinalIgnoreCase)))).ToArray();
    }
}
