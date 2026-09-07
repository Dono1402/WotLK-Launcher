using System.Text.Json;
using WotLK.Launcher;

internal static class AddonDependencyPlannerTests
{
    // Pure graph checks: no client folders, downloads, settings or services.
    // Returns the assertion count; any failure throws to the parent test runner.
    internal static int Run()
    {
        int checks = 0;
        AddonPackage core = Package("core"), graphics = Package("graphics", "core"), voice = Package("voice", "CORE");
        AddonPackage raid = Package("raid", "graphics", "voice"), solo = Package("solo");
        AddonCatalog catalog = Catalog(raid, solo, voice, core, graphics);
        string before = JsonSerializer.Serialize(catalog);

        Sequence(["core", "graphics", "voice", "raid"], AddonDependencyPlanner.Plan(catalog, ["raid"]),
            "Dependencies precede their dependent in stable declared order, independent of catalog order.");
        Sequence(["solo", "core", "graphics", "voice", "raid"], AddonDependencyPlanner.Plan(catalog, ["solo", "RAID", "graphics", "CORE", "raid", "Solo"]),
            "Multiple requested roots preserve order and share each dependency once, case-insensitively.");
        Sequence(["core", "graphics", "voice", "raid", "solo"], AddonDependencyPlanner.Plan(catalog, ["raid", "solo"]),
            "Changing root order deterministically changes only unconstrained placement.");
        Sequence(["core", "voice", "graphics"], AddonDependencyPlanner.Plan(catalog, ["voice", "graphics"]),
            "A shared dependency is not repeated across independent roots.");
        Sequence([], AddonDependencyPlanner.Plan(catalog, []), "An empty selection produces no install operations.");
        IReadOnlyList<AddonPackage> plan = AddonDependencyPlanner.Plan(catalog, ["RaId"]);
        Check(ReferenceEquals(plan[0], core) && ReferenceEquals(plan[1], graphics) && ReferenceEquals(plan[2], voice) && ReferenceEquals(plan[3], raid),
            "The plan returns original catalog packages with their metadata and canonical IDs.");
        Sequence(["core", "duplicate"], AddonDependencyPlanner.Plan(Catalog(Package("duplicate", "core", "CORE", "core"), core), ["duplicate"]),
            "Repeated references inside one dependency list are deduplicated.");

        Failure("addon-not-found", ["unknown"], () => AddonDependencyPlanner.Plan(catalog, ["unknown"]),
            "Unknown requested roots have a distinct actionable error.");
        Failure("dependency-missing", ["root", "middle", "missing"],
            () => AddonDependencyPlanner.Plan(Catalog(Package("root", "middle"), Package("middle", "missing")), ["root"]),
            "Missing transitive dependencies report the complete traversal chain.");
        Failure("dependency-cycle", ["Alpha", "Beta", "Gamma", "alpha"],
            () => AddonDependencyPlanner.Plan(Catalog(Package("Alpha", "Beta"), Package("Beta", "Gamma"), Package("Gamma", "alpha")), ["ALPHA"]),
            "A case-insensitive multi-node cycle reports the chain rather than recursing indefinitely.");
        Failure("dependency-cycle", ["Self", "SELF"], () => AddonDependencyPlanner.Plan(Catalog(Package("Self", "SELF")), ["self"]),
            "A self-dependency is a cycle, including a differently cased ID.");
        Failure("invalid-dependency", [" core"], () => AddonDependencyPlanner.Plan(catalog, [" core"]),
            "Whitespace around a requested ID is rejected rather than silently normalized.");
        Failure("invalid-dependency", [""], () => AddonDependencyPlanner.Plan(Catalog(Package("root", "")), ["root"]),
            "An empty dependency is rejected explicitly.");
        Failure("invalid-dependency", ["core "], () => AddonDependencyPlanner.Plan(Catalog(Package("root", "core "), core), ["root"]),
            "Whitespace around a dependency is rejected explicitly.");
        Failure("invalid-dependency", [""], () => AddonDependencyPlanner.Plan(Catalog(Package("root", (string)null!)), ["root"]),
            "A null dependency has a safe diagnostic instead of a null dereference.");
        Sequence(["solo"], AddonDependencyPlanner.Plan(Catalog(Package("bad", "missing"), Package("loop", "loop"), solo), ["solo"]),
            "An unrelated broken package does not invalidate an independent requested plan.");

        string[] allInstalled = ["CORE", "graphics", "VOICE", "RaId", "solo"];
        Sequence(["raid", "voice", "graphics"], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled, ["CoRe"]),
            "Removing a dependency reports every retained direct/transitive dependent in catalog order.");
        Sequence(["raid", "voice"], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled, ["core", "GRAPHICS"]),
            "Dependents being removed in the same operation are excluded from blockers.");
        Sequence([], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled, ["core", "graphics", "voice", "raid"]),
            "Removing the entire dependent closure leaves no blocker.");
        Sequence(["raid"], AddonDependencyPlanner.FindBlockingDependents(catalog, ["RAID", "core", "unknown"], ["core"]),
            "Only retained installed catalog packages are reported, including transitive dependencies.");
        Sequence([], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled, ["solo"]),
            "An independent package can be removed without blocking unrelated installed packages.");
        Sequence([], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled, []),
            "An empty removal selection has no blockers.");
        Sequence([], AddonDependencyPlanner.FindBlockingDependents(catalog, [], ["core"]),
            "Uninstalled dependents do not block a removal.");
        Sequence(["raid", "voice", "graphics"], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled.Concat(allInstalled), ["core", "CORE"]),
            "Duplicate installed/removal IDs do not repeat blockers.");
        Sequence([], AddonDependencyPlanner.FindBlockingDependents(catalog, allInstalled, ["unknown"]),
            "Unknown unrelated removal IDs do not fabricate blockers.");

        AddonCatalog cyclic = Catalog(Package("cycle-a", "cycle-b"), Package("cycle-b", "cycle-a", "core"), core, solo);
        Sequence(["cycle-a", "cycle-b"], AddonDependencyPlanner.FindBlockingDependents(cyclic, ["CYCLE-A", "cycle-b", "core", "solo"], ["CORE"]),
            "A cyclic dependent graph still finds reachable removed dependencies without infinite traversal.");
        Sequence([], AddonDependencyPlanner.FindBlockingDependents(cyclic, ["cycle-a", "cycle-b", "solo"], ["solo"]),
            "A dependency cycle with no path to a removed package creates no false blocker.");
        Check(JsonSerializer.Serialize(catalog) == before,
            "Planning and removal checks do not mutate catalog ordering, metadata or dependency lists.");
        Sequence(["core", "graphics", "voice", "raid"], AddonDependencyPlanner.Plan(catalog, ["raid"]),
            "A later plan is independent of previous successful/failed plans and removal checks.");
        Console.WriteLine($"Addon dependency planner PASS: {checks} pure graph assertions.");
        return checks;

        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }
        void Sequence(string[] expected, IReadOnlyList<AddonPackage> actual, string message)
            => Check(expected.SequenceEqual(actual.Select(package => package.Id), StringComparer.Ordinal), message);
        void Failure(string code, string[] ids, Action operation, string message)
        {
            try { operation(); }
            catch (AddonPlanException error)
            {
                Check(error.Code == code && error.AddonIds.SequenceEqual(ids, StringComparer.Ordinal), message);
                return;
            }
            throw new InvalidOperationException(message + " No exception was thrown.");
        }
    }

    private static AddonPackage Package(string id, params string[] dependencies) => new()
    {
        Id = id, Name = "Fixture " + id, Version = "1.0", Dependencies = [.. dependencies]
    };

    private static AddonCatalog Catalog(params AddonPackage[] packages) => new()
    {
        SchemaVersion = 1, ClientInterface = "30403", Addons = [.. packages]
    };
}
