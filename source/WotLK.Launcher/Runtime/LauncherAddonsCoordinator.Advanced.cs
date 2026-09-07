using System.Collections.Immutable;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherAddonsCoordinator
{
    private readonly Dictionary<string, VerificationCache> _verificationResults = new(StringComparer.OrdinalIgnoreCase);
    private OperationPlan? _retryPlan;

    internal AddonsPlanPreview PreviewInstall(IEnumerable<string> addonIds, bool forceReinstall = false)
    {
        ImmutableArray<string> requested = NormalizeTargets(addonIds);
        lock (_sync)
        {
            if (_catalog is null)
            {
                return new(requested, [], "catalog-unavailable", []);
            }
            return BuildInstallPreviewUnsafe(requested, forceReinstall);
        }
    }

    internal AddonsActionStartResult TryInstallSelection(IEnumerable<string> addonIds, bool allowExternalReplacement = false)
        => TryStartAction(AddonsRequestedAction.InstallSelection, NormalizeTargets(addonIds), allowExternalReplacement);

    internal AddonsActionStartResult TryUpdateSelection(IEnumerable<string> addonIds)
        => TryStartAction(AddonsRequestedAction.UpdateAll, NormalizeTargets(addonIds));

    internal AddonsActionStartResult TryVerify(string addonId)
        => TryStartAction(AddonsRequestedAction.Verify, [addonId]);

    internal AddonsActionStartResult TryVerifySelection(IEnumerable<string> addonIds)
        => TryStartAction(AddonsRequestedAction.VerifySelection, NormalizeTargets(addonIds));

    internal AddonsActionStartResult TryReinstall(string addonId, bool allowExternalReplacement = false)
        => TryStartAction(AddonsRequestedAction.Reinstall, [addonId], allowExternalReplacement);

    internal AddonsPlanPreview PreviewRetry()
    {
        lock (_sync)
        {
            ImmutableArray<string> remaining = GetRetryTargetsUnsafe();
            if (_retryPlan is null || remaining.IsDefaultOrEmpty)
            {
                return new(remaining, [], "retry-unavailable", []);
            }
            if (!string.Equals(_retryPlan.InstallRoot, _settings.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                return new(remaining, [], "client-changed", []);
            }
            if (_catalog is null)
            {
                return new(remaining, [], "catalog-unavailable", []);
            }

            if (!IsVerification(_retryPlan.Action) && _retryPlan.Action != AddonsRequestedAction.Remove)
            {
                return BuildInstallPreviewUnsafe(remaining, forceReinstall: false, _retryPlan.Actions);
            }

            ImmutableArray<string> missing = remaining.Where(id => FindItemUnsafe(id) is null).ToImmutableArray();
            if (!missing.IsEmpty)
            {
                return new(remaining, [], "addon-not-found", missing);
            }
            AddonsRequestedAction action = RetryActionFor(_retryPlan);
            AddonsActionStartResult? error = TryBuildOperationPlanUnsafe(action, remaining,
                allowExternalReplacement: true, _retryPlan.Actions, out OperationPlan? plan);
            if (error is not null)
            {
                return new(remaining, [], string.IsNullOrEmpty(error.ErrorCode) ? "invalid-state" : error.ErrorCode,
                    error.RelatedAddonIds);
            }
            OperationPlan retryPlan = plan!;
            return new(remaining, retryPlan.Packages.Select(package => new AddonsPlanItem(package.Id, package.Name,
                package.Version, retryPlan.Actions[package.Id], false, false, package.Folders.ToImmutableArray())).ToImmutableArray(),
                string.Empty, []);
        }
    }

    internal AddonsActionStartResult TryRetryFailed(bool allowExternalReplacement = false)
    {
        ImmutableArray<string> remaining;
        OperationPlan? previous;
        lock (_sync)
        {
            previous = _retryPlan;
            remaining = GetRetryTargetsUnsafe();
            if (previous is null || remaining.IsDefaultOrEmpty
                || !string.Equals(previous.InstallRoot, _settings.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidState);
            }
        }

        return TryStartAction(RetryActionFor(previous), remaining, allowExternalReplacement, previous.Actions);
    }

    private ImmutableArray<string> GetRetryTargetsUnsafe()
        => _currentSnapshot.FailedAddonIds.Concat(_currentSnapshot.UnprocessedAddonIds)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    private static AddonsRequestedAction RetryActionFor(OperationPlan plan)
        => IsVerification(plan.Action) ? AddonsRequestedAction.VerifySelection
            : plan.Action == AddonsRequestedAction.Remove ? AddonsRequestedAction.Remove
                : AddonsRequestedAction.InstallSelection;

    private static ImmutableArray<string> NormalizeTargets(IEnumerable<string>? addonIds)
        => addonIds is null ? [] : addonIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    private AddonsPlanPreview BuildInstallPreviewUnsafe(ImmutableArray<string> requested, bool forceReinstall,
        ImmutableDictionary<string, AddonsRequestedAction>? retryActions = null)
    {
        try
        {
            HashSet<string> explicitIds = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ImmutableArray<AddonsPlanItem>.Builder items = ImmutableArray.CreateBuilder<AddonsPlanItem>();
            foreach (AddonPackage package in AddonDependencyPlanner.Plan(_catalog!, requested))
            {
                AddonRuntimeItem? item = FindItemUnsafe(package.Id);
                AddonsRequestedAction action = item?.NeedsRepair == true ? AddonsRequestedAction.Repair
                    : item?.LocalStatus == AddonLocalStatus.UpdateAvailable ? AddonsRequestedAction.Update
                    : item?.IsManaged != true ? AddonsRequestedAction.Install : AddonsRequestedAction.None;
                if (explicitIds.Contains(package.Id)
                    && (forceReinstall || (retryActions is not null && retryActions.TryGetValue(package.Id, out AddonsRequestedAction retry)
                        && retry is AddonsRequestedAction.Reinstall or AddonsRequestedAction.Repair)))
                {
                    action = AddonsRequestedAction.Reinstall;
                }
                if (action != AddonsRequestedAction.None)
                {
                    items.Add(new(package.Id, package.Name, package.Version, action,
                        !explicitIds.Contains(package.Id), item?.IsDetectedUnmanaged == true, package.Folders.ToImmutableArray()));
                }
            }
            return new(requested, items.ToImmutable(), string.Empty, []);
        }
        catch (AddonPlanException exception)
        {
            return new(requested, [], exception.Code, exception.AddonIds.ToImmutableArray());
        }
    }

    private AddonsActionStartResult? TryBuildOperationPlanUnsafe(AddonsRequestedAction action,
        ImmutableArray<string> requested, bool allowExternalReplacement,
        ImmutableDictionary<string, AddonsRequestedAction>? retryActions, out OperationPlan? plan)
    {
        plan = null;
        AddonCatalog catalog = _catalog!;
        Dictionary<string, AddonPackage> byId = catalog.Addons.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        ImmutableArray<AddonPackage> packages;
        ImmutableDictionary<string, AddonsRequestedAction> actions;
        if (IsVerification(action) || action == AddonsRequestedAction.Remove)
        {
            if (action == AddonsRequestedAction.Remove)
            {
                HashSet<string> installed = _currentSnapshot.Items
                    .Where(item => item.IsManaged || item.IsDetectedUnmanaged).Select(item => item.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<AddonPackage> graphBlockers = AddonDependencyPlanner.FindBlockingDependents(catalog, installed, requested);
                HashSet<string> removedFolders = requested.SelectMany(id => byId[id].Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
                ImmutableArray<string> blockers = graphBlockers.Select(package => package.Id).Concat(_currentSnapshot.ManualAddons
                    .Where(manual => manual.Dependencies.Any(removedFolders.Contains)).Select(manual => manual.Id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
                if (!blockers.IsDefaultOrEmpty)
                {
                    return AddonsActionStartResult.Rejected(AddonsActionStartStatus.DependencyInUse) with
                    { ErrorCode = "dependency-in-use", RelatedAddonIds = blockers };
                }
            }
            packages = requested.Select(id => byId[id]).ToImmutableArray();
            actions = requested.ToImmutableDictionary(id => id,
                _ => IsVerification(action) ? AddonsRequestedAction.Verify : AddonsRequestedAction.Remove,
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            AddonsPlanPreview preview = BuildInstallPreviewUnsafe(requested, action == AddonsRequestedAction.Reinstall, retryActions);
            if (!preview.IsValid)
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidPlan) with
                { ErrorCode = preview.ErrorCode, RelatedAddonIds = preview.RelatedAddonIds };
            }
            if (preview.Items.IsDefaultOrEmpty)
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.InvalidState);
            }
            if (!allowExternalReplacement && preview.RequiresExternalReplacementConfirmation)
            {
                return AddonsActionStartResult.Rejected(AddonsActionStartStatus.ConfirmationRequired) with
                {
                    ErrorCode = "external-replacement-required",
                    RelatedAddonIds = preview.Items.Where(item => item.ReplacesExternal).Select(item => item.Id).ToImmutableArray()
                };
            }
            packages = preview.Items.Select(item => byId[item.Id]).ToImmutableArray();
            actions = preview.Items.ToImmutableDictionary(item => item.Id, item => item.Action, StringComparer.OrdinalIgnoreCase);
        }

        plan = new(catalog, packages, action, _settings.InstallPath, actions, allowExternalReplacement);
        return null;
    }

    private static bool IsVerification(AddonsRequestedAction action)
        => action is AddonsRequestedAction.Verify or AddonsRequestedAction.VerifySelection;

    private static AddonsOperationPhase PhaseFor(AddonsRequestedAction action) => action switch
    {
        AddonsRequestedAction.Remove => AddonsOperationPhase.Removing,
        AddonsRequestedAction.Verify or AddonsRequestedAction.VerifySelection => AddonsOperationPhase.Verifying,
        _ => AddonsOperationPhase.Downloading
    };

    private ImmutableArray<AddonRuntimeItem> PrepareTargetItemsUnsafe(ImmutableArray<AddonRuntimeItem> items,
        string addonId, AddonsRequestedAction action)
    {
        if (IsVerification(action))
        {
            _verificationResults.Remove(addonId);
            items = items.Select(item => string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase)
                ? item with
                {
                    VerificationStatus = item.IsManaged
                        ? item.VerificationStatus == AddonVerificationStatus.LegacyUnverified
                            ? AddonVerificationStatus.LegacyUnverified : AddonVerificationStatus.NotVerified
                        : item.IsDetectedUnmanaged ? AddonVerificationStatus.Unmanaged : AddonVerificationStatus.NotInstalled,
                    VerificationMessage = string.Empty,
                    VerifiedAtUtc = null
                } : item).ToImmutableArray();
        }
        return MarkActiveItem(items, addonId, ToOperationState(action));
    }

    private ImmutableArray<ManualAddonInstallation> InspectManualSafely(AddonCatalog catalog, string installRoot)
    {
        try { return _service.InspectManualAddons(catalog, installRoot).ToImmutableArray(); }
        catch (Exception exception)
        {
            WriteFailureSafely(string.Empty, AddonsRequestedAction.None, ClassifyFailure(exception), exception);
            return [];
        }
    }

    private AddonRuntimeItem AttachVerificationUnsafe(AddonRuntimeItem item, AddonPackage package, AddonInspection inspection)
    {
        AddonVerificationResult? result = null;
        if (_verificationResults.TryGetValue(package.Id, out VerificationCache? cached))
        {
            if (string.Equals(cached.InstallRoot, _settings.InstallPath, StringComparison.OrdinalIgnoreCase)
                && cached.InstalledAtUtc == inspection.InstalledAtUtc
                && string.Equals(cached.InstalledSha256, inspection.InstalledSha256, StringComparison.OrdinalIgnoreCase))
            {
                result = cached.Result;
            }
            else { _verificationResults.Remove(package.Id); }
        }
        AddonVerificationStatus status = result?.Status ?? (inspection.IsManaged
            ? inspection.HasFileManifest ? AddonVerificationStatus.NotVerified : AddonVerificationStatus.LegacyUnverified
            : inspection.Status == AddonLocalStatus.DetectedUnmanaged
                ? AddonVerificationStatus.Unmanaged : AddonVerificationStatus.NotInstalled);
        return item with
        {
            VerificationStatus = status,
            VerificationMessage = result?.Message ?? string.Empty,
            VerifiedAtUtc = result?.VerifiedAtUtc,
            SourceUrl = package.SourceUrl,
            KnownLimitations = package.KnownLimitations,
            AtlasValidationEvidence = package.ValidatedAtlasEvidenceUrl
        };
    }

    private sealed record VerificationCache(string InstallRoot, string? InstalledSha256,
        DateTimeOffset? InstalledAtUtc, AddonVerificationResult Result);
}
