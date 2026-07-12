using BitBroom.Core.Catalog;
using BitBroom.Core.Engine;
using Xunit;

namespace BitBroom.Core.Tests;

/// <summary>
/// Meta-tests that keep the whole catalog honest: every rule in every category must
/// pass structural validation, and no rule may resolve to a protected location.
/// A regression here means a category could endanger user data — these tests are the gate.
/// </summary>
public class CatalogSanityTests
{
    [Fact]
    public void Category_ids_are_unique_and_wellformed()
    {
        IReadOnlyList<CleanCategory> catalog = CategoryCatalog.Build();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanCategory category in catalog)
        {
            Assert.Matches("^[a-z0-9-]+$", category.Id);
            Assert.True(ids.Add(category.Id), $"Duplicate category id {category.Id}");
            Assert.False(string.IsNullOrWhiteSpace(category.Name));
            Assert.False(string.IsNullOrWhiteSpace(category.Description));
        }
    }

    [Fact]
    public void Default_on_categories_are_safe_risk()
    {
        foreach (CleanCategory category in CategoryCatalog.Build().Where(c => c.EnabledByDefault))
        {
            Assert.Equal(RiskLevel.Safe, category.Risk);
        }
    }

    [Fact]
    public void Advanced_categories_carry_warnings()
    {
        foreach (CleanCategory category in CategoryCatalog.Build().Where(c => c.Risk == RiskLevel.Advanced))
        {
            Assert.False(string.IsNullOrWhiteSpace(category.Warning), $"{category.Id} needs a warning");
            Assert.False(category.EnabledByDefault);
        }
    }

    [Fact]
    public void All_rules_validate_structurally()
    {
        foreach (CleanCategory category in CategoryCatalog.Build())
        {
            foreach (CleanRule rule in category.Rules)
            {
                // Validate() throws on structural problems.
                rule.Validate();
                Assert.DoesNotContain("..", rule.RelativePattern, StringComparison.Ordinal);
                Assert.False(Path.IsPathRooted(rule.RelativePattern), $"{category.Id}: rooted pattern {rule.RelativePattern}");
            }
        }
    }

    [Fact]
    public void No_rule_resolves_to_a_guard_rejected_root_on_this_machine()
    {
        var guard = new PathGuard();
        var resolver = new PathResolver(guard);
        var rejections = new List<string>();

        foreach (CleanCategory category in CategoryCatalog.Build())
        {
            foreach (CleanRule rule in category.Rules)
            {
                resolver.ExpandRoots(rule, (root, reason) => rejections.Add($"{category.Id}: {root} → {reason}"));
            }
        }

        // Every root the resolver yields is guard-approved by construction; this asserts
        // that none of our shipped rules even ATTEMPT a protected location on a real system.
        Assert.True(rejections.Count == 0, string.Join("\n", rejections));
    }

    [Fact]
    public void Resolved_roots_never_touch_user_content_folders()
    {
        var guard = new PathGuard();
        var resolver = new PathResolver(guard);
        string[] forbidden =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            BitBroom.Core.Native.NativeMethods.GetDownloadsFolderPath() ?? string.Empty,
        ];

        foreach (CleanCategory category in CategoryCatalog.Build())
        {
            foreach (CleanRule rule in category.Rules)
            {
                foreach (ResolvedRoot root in resolver.ExpandRoots(rule))
                {
                    foreach (string dir in forbidden.Where(d => !string.IsNullOrEmpty(d)))
                    {
                        Assert.False(
                            PathGuard.PathsEqual(root.Path, dir) || PathGuard.IsUnder(root.Path, dir),
                            $"{category.Id} resolved into user content: {root.Path}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Fixed_file_rules_only_target_named_files()
    {
        foreach (CleanCategory category in CategoryCatalog.Build())
        {
            foreach (CleanRule rule in category.Rules.Where(r => r.Kind == RuleKind.FixedFiles))
            {
                Assert.All(rule.FilePatterns, p => Assert.False(BitBroom.Core.Util.Glob.HasWildcards(p),
                    $"{category.Id}: fixed-file rule must name exact files, got '{p}'"));
            }
        }
    }
}
