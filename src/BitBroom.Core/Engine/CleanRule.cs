namespace BitBroom.Core.Engine;

/// <summary>Well-known base folders a rule may anchor to. Rules can never leave their base.</summary>
public enum KnownBase
{
    LocalAppData,
    RoamingAppData,
    LocalLow,
    UserProfile,
    ProgramData,
    SystemRoot,
    SystemDrive,

    /// <summary>Resolved by a custom provider (e.g. the Steam install folder from the registry).</summary>
    Custom,
}

public enum RuleKind
{
    /// <summary>Delete matching files under the resolved root(s).</summary>
    FilesUnderRoot,

    /// <summary>Delete exactly the named file(s) directly under the base (e.g. C:\Windows\memory.dmp).</summary>
    FixedFiles,
}

/// <summary>
/// A declarative cleaning rule. <see cref="RelativePattern"/> is split into path segments;
/// segments may contain '*'/'?' wildcards which are expanded by enumerating real directories
/// (never by string substitution), skipping reparse points at every level.
/// </summary>
public sealed record CleanRule
{
    public required KnownBase Base { get; init; }

    /// <summary>Path relative to the base, e.g. @"Google\Chrome\User Data\*\Cache".</summary>
    public required string RelativePattern { get; init; }

    public RuleKind Kind { get; init; } = RuleKind.FilesUnderRoot;

    /// <summary>File name patterns to delete; defaults to everything.</summary>
    public IReadOnlyList<string> FilePatterns { get; init; } = ["*"];

    public bool Recurse { get; init; } = true;

    /// <summary>Overrides the global minimum file age; 0 disables the age filter for this rule.</summary>
    public int? MinAgeHoursOverride { get; init; }

    /// <summary>Remove directories left empty after deletion (the root itself is always kept).</summary>
    public bool DeleteEmptyDirs { get; init; } = true;

    /// <summary>Resolver for <see cref="KnownBase.Custom"/> bases; returns null when unavailable.</summary>
    public Func<string?>? CustomBaseProvider { get; init; }

    public CleanRule Validate()
    {
        if (string.IsNullOrWhiteSpace(RelativePattern) && Kind != RuleKind.FixedFiles)
        {
            throw new InvalidOperationException("Rule must have a relative pattern.");
        }

        if (RelativePattern.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Rule pattern may not contain '..': {RelativePattern}");
        }

        if (Path.IsPathRooted(RelativePattern))
        {
            throw new InvalidOperationException($"Rule pattern must be relative: {RelativePattern}");
        }

        if (Base == KnownBase.Custom && CustomBaseProvider is null)
        {
            throw new InvalidOperationException("Custom-base rule requires a CustomBaseProvider.");
        }

        return this;
    }
}
