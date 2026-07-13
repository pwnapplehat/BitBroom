namespace BitBroom.Cli;

/// <summary>
/// Tiny, dependency-free argument parser for the BitBroom CLI.
/// Strict by design: unknown options and malformed values are rejected rather than
/// silently ignored — for a tool that deletes files, a mistyped <c>--dry-run</c> must
/// never fall through to a real clean.
/// </summary>
public sealed class ArgumentParser
{
    public string? Command { get; }
    public List<string> Positional { get; } = [];
    public List<string>? CategoryIds { get; }
    public bool All { get; }
    public bool Json { get; }
    public bool Yes { get; }
    public bool DryRun { get; }
    public bool ExplicitCategories => CategoryIds is { Count: > 0 };
    public int? MinAgeHours { get; }
    public int Top { get; } = 25;
    public int Depth { get; } = 2;

    /// <summary>dupes: minimum file size in MB to consider (default 1 MB).</summary>
    public int MinSizeMb { get; } = 1;

    /// <summary>Non-null when the arguments were invalid; the CLI prints it and returns exit code 3.</summary>
    public string? Error { get; }

    public ArgumentParser(string[] args)
    {
        string? error = null;
        List<string>? categoryIds = null;
        int? minAge = null;
        int top = 25;
        int depth = 2;
        int minSizeMb = 1;

        // Returns the value token for a value-flag, or sets Error and returns null when the
        // value is missing or looks like another option.
        string? TakeValue(string flag, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                error ??= $"Option '{flag}' requires a value.";
                return null;
            }

            string value = args[index + 1];
            if (value.StartsWith('-') && !(value.Length > 1 && (char.IsDigit(value[1]) || value[1] == '.')))
            {
                // Next token is another option (but "-5" is a valid negative number value).
                error ??= $"Option '{flag}' requires a value.";
                return null;
            }

            index++;
            return value;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--json":
                    Json = true;
                    break;
                case "--yes" or "-y":
                    Yes = true;
                    break;
                case "--dry-run" or "--dryrun" or "--simulate":
                    DryRun = true;
                    break;
                case "--all":
                    All = true;
                    break;
                case "--defaults":
                    break;
                case "--categories" or "-c":
                {
                    string? value = TakeValue(arg, ref i);
                    if (value is not null)
                    {
                        categoryIds = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                        if (categoryIds.Count == 0)
                        {
                            error ??= "Option '--categories' requires at least one category id.";
                        }
                    }

                    break;
                }

                case "--min-age":
                {
                    string? value = TakeValue(arg, ref i);
                    if (value is not null)
                    {
                        if (int.TryParse(value, out int parsed) && parsed >= 0)
                        {
                            minAge = parsed;
                        }
                        else
                        {
                            error ??= $"Invalid value for --min-age: '{value}' (expected a non-negative integer).";
                        }
                    }

                    break;
                }

                case "--top":
                {
                    string? value = TakeValue(arg, ref i);
                    if (value is not null)
                    {
                        if (int.TryParse(value, out int parsed) && parsed > 0)
                        {
                            top = parsed;
                        }
                        else
                        {
                            error ??= $"Invalid value for --top: '{value}' (expected a positive integer).";
                        }
                    }

                    break;
                }

                case "--depth":
                {
                    string? value = TakeValue(arg, ref i);
                    if (value is not null)
                    {
                        if (int.TryParse(value, out int parsed) && parsed >= 0)
                        {
                            depth = parsed;
                        }
                        else
                        {
                            error ??= $"Invalid value for --depth: '{value}' (expected a non-negative integer).";
                        }
                    }

                    break;
                }

                case "--min-size":
                {
                    string? value = TakeValue(arg, ref i);
                    if (value is not null)
                    {
                        if (int.TryParse(value, out int parsed) && parsed >= 0)
                        {
                            minSizeMb = parsed;
                        }
                        else
                        {
                            error ??= $"Invalid value for --min-size: '{value}' (expected megabytes as a non-negative integer).";
                        }
                    }

                    break;
                }

                default:
                    if (arg.StartsWith('-'))
                    {
                        // Unknown option — never silently ignore it (a mistyped --dry-run must not clean).
                        error ??= $"Unknown option '{arg}'. Run 'bitbroom-cli help'.";
                    }
                    else if (Command is null)
                    {
                        Command = arg.ToLowerInvariant();
                    }
                    else
                    {
                        Positional.Add(arg);
                    }

                    break;
            }
        }

        CategoryIds = categoryIds;
        MinAgeHours = minAge;
        Top = top;
        Depth = depth;
        MinSizeMb = minSizeMb;
        Error = error;
    }
}
