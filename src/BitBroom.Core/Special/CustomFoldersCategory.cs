using BitBroom.Core.Engine;
using BitBroom.Core.Settings;

namespace BitBroom.Core.Special;

/// <summary>
/// The user-defined "clean these folders of old files" category. Each configured folder
/// becomes a Custom-base rule anchored at the folder's parent, so the full PathGuard
/// pipeline applies unchanged: protected locations (Documents, Desktop, Windows, Program
/// Files, drive roots, OneDrive…) are refused at scan time, junctions are never followed,
/// and every deletion is re-validated. BitBroom's safety model is not relaxed for custom
/// folders — a folder the guard rejects simply reports its rejection in the scan.
/// </summary>
public static class CustomFoldersCategory
{
    public const string CategoryId = "custom-folders";

    public static CleanCategory Create(IReadOnlyList<CustomCleanFolder> folders)
    {
        var rules = new List<CleanRule>();

        foreach (CustomCleanFolder folder in folders)
        {
            string trimmed = folder.Path.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue;
            }

            string normalized;
            string? parent;
            string? leaf;
            try
            {
                normalized = PathGuard.Normalize(trimmed);
                parent = Path.GetDirectoryName(normalized);
                leaf = Path.GetFileName(normalized);
            }
            catch (Exception)
            {
                continue;
            }

            // A drive root has no parent/leaf — and must never be a clean target anyway.
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
            {
                continue;
            }

            rules.Add(new CleanRule
            {
                Base = KnownBase.Custom,
                // Trusted only in the sense that the base resolves to the folder's parent;
                // Program Files subtrees stay guarded because trustedCustomBase only
                // relaxes registry-located installs — see ValidateRuleRoot call below.
                CustomBaseProvider = () => Directory.Exists(parent) ? parent : null,
                RelativePattern = leaf,
                MinAgeHoursOverride = folder.MinAgeHours,
            }.Validate());
        }

        return new CustomFoldersCleanCategory
        {
            Id = CategoryId,
            Name = "Custom folders",
            Description = "Your own folders (Settings → Custom folders), cleaned of files older than the per-folder age you chose. Protected locations (Documents, Desktop, system folders…) are refused by the safety guard even if added here.",
            Group = CategoryGroup.Advanced,
            Risk = RiskLevel.Moderate,
            EnabledByDefault = false,
            Warning = "These are your folders, not app caches — double-check the folder list and age limits before cleaning.",
            Rules = rules,
        };
    }

    /// <summary>
    /// Same declarative pipeline, but Custom-base roots from user input must NOT get the
    /// trusted-custom-base relaxation that registry-located installs (Steam) get.
    /// </summary>
    private sealed class CustomFoldersCleanCategory : CleanCategory
    {
        public override Task<CategoryScanResult> ScanAsync(ScanContext context, CancellationToken cancellationToken)
            => Task.Run(() => ScanWithStrictGuard(context, cancellationToken), cancellationToken);

        private CategoryScanResult ScanWithStrictGuard(ScanContext context, CancellationToken cancellationToken)
        {
            var result = new CategoryScanResult { CategoryId = Id };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool anyRootExisted = false;

            foreach (CleanRule rule in Rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? basePath = context.Resolver.ResolveBase(rule.Base, rule);
                if (basePath is null || !Directory.Exists(basePath))
                {
                    continue;
                }

                basePath = PathGuard.Normalize(basePath);
                string rootPath = Path.Combine(basePath, rule.RelativePattern);
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                string normalizedRoot = PathGuard.Normalize(rootPath);

                // Strict validation: trustedCustomBase=false so Program Files and every
                // other protected subtree is refused for user-entered folders.
                string? rejection = context.Resolver.Guard.ValidateRuleRoot(normalizedRoot, basePath, trustedCustomBase: false);
                if (rejection is not null)
                {
                    result.Errors.Add($"Refused '{normalizedRoot}': {rejection}");
                    continue;
                }

                anyRootExisted = true;
                var root = new ResolvedRoot(normalizedRoot, basePath, rule);
                var stats = new FileSystemWalker.WalkStats();
                try
                {
                    foreach (ScanItem item in FileSystemWalker.Walk(root, context.NowUtc, context.GlobalMinAgeHours, stats, cancellationToken, context.Exclusions))
                    {
                        result.Items.Add(item);
                        result.TotalBytes += item.SizeBytes;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Walk failed under {root.Path}: {ex.Message}");
                }

                result.SkippedReparsePoints += stats.SkippedReparsePoints;
                result.SkippedCloudPlaceholders += stats.SkippedCloudPlaceholders;
                result.SkippedTooNew += stats.SkippedTooNew;
                result.Inaccessible += stats.Inaccessible;
                result.SkippedExcluded += stats.SkippedExcluded;
            }

            result.NotDetected = !anyRootExisted && Rules.Count == 0;
            result.Duration = stopwatch.Elapsed;
            return result;
        }
    }
}
