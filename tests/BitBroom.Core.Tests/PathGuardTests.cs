using BitBroom.Core.Engine;
using Xunit;

namespace BitBroom.Core.Tests;

public class PathGuardTests
{
    private readonly PathGuard _guard = new();

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string SystemDrive => Path.GetPathRoot(Windows)!;

    [Fact]
    public void Accepts_user_temp()
        => Assert.Null(_guard.ValidateRuleRoot(Path.Combine(LocalAppData, "Temp"), LocalAppData));

    [Fact]
    public void Accepts_windows_temp()
        => Assert.Null(_guard.ValidateRuleRoot(Path.Combine(Windows, "Temp"), Windows));

    [Fact]
    public void Rejects_drive_root()
        => Assert.NotNull(_guard.ValidateRuleRoot(SystemDrive, SystemDrive));

    [Fact]
    public void Rejects_root_equal_to_base()
        => Assert.NotNull(_guard.ValidateRuleRoot(LocalAppData, LocalAppData));

    [Fact]
    public void Rejects_root_escaping_base()
        => Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(Windows, "Temp"), LocalAppData));

    [Fact]
    public void Rejects_windows_directory_itself()
        => Assert.NotNull(_guard.ValidateRuleRoot(Windows, SystemDrive));

    [Fact]
    public void Rejects_system32()
        => Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(Windows, "System32"), Windows));

    [Fact]
    public void Rejects_winsxs()
        => Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(Windows, "WinSxS"), Windows));

    [Fact]
    public void Rejects_documents()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(documents) || !documents.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            return; // Redirected profiles: nothing to assert.
        }

        Assert.NotNull(_guard.ValidateRuleRoot(documents, profile));
        Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(documents, "Projects"), profile));
    }

    [Fact]
    public void Rejects_desktop_subfolder()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(desktop) || !desktop.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(desktop, "stuff"), profile));
    }

    [Fact]
    public void Rejects_non_allowlisted_system_drive_folder()
        => Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(SystemDrive, "MyData"), SystemDrive));

    [Fact]
    public void Accepts_allowlisted_system_drive_leftovers()
    {
        // These only validate when the directory exists or attribute checks pass; use the
        // pure path logic by asserting the rejection reason is NOT the allow-list.
        string? reason = _guard.ValidateRuleRoot(Path.Combine(SystemDrive, "NVIDIA"), SystemDrive);
        Assert.True(reason is null || !reason.Contains("allow-listed"), $"Unexpected: {reason}");
    }

    [Fact]
    public void Rejects_parent_traversal()
        => Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(LocalAppData, "Temp", "..", "..", "important"), LocalAppData));

    [Fact]
    public void Rejects_program_files_for_standard_base()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.NotNull(_guard.ValidateRuleRoot(Path.Combine(programFiles, "SomeApp", "cache"), SystemDrive));
    }

    [Fact]
    public void Allows_program_files_for_trusted_custom_base()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string steam = Path.Combine(programFiles, "Steam");
        string? reason = _guard.ValidateRuleRoot(Path.Combine(steam, "steamapps", "shadercache"), steam, trustedCustomBase: true);
        // May still fail on attributes if the dir doesn't exist — but never on the ProgramFiles rule.
        Assert.True(reason is null || !reason.Contains("protected location"), $"Unexpected: {reason}");
    }

    // -------------------------------------------------------------------------
    // Delete-time validation
    // -------------------------------------------------------------------------

    [Fact]
    public void DeleteValidation_rejects_path_outside_root()
    {
        using var sandbox = new TestSandbox();
        string root = sandbox.CreateDirectory("cache");
        string outside = sandbox.CreateFile("other/file.bin");
        Assert.NotNull(_guard.ValidateDeletePath(outside, root));
    }

    [Fact]
    public void DeleteValidation_accepts_path_inside_root()
    {
        using var sandbox = new TestSandbox();
        string root = sandbox.CreateDirectory("cache");
        string inside = sandbox.CreateFile(@"cache\file.bin");
        Assert.Null(_guard.ValidateDeletePath(inside, root));
    }

    [Fact]
    public void DeleteValidation_rejects_reparse_attributes()
    {
        Assert.NotNull(PathGuard.ValidateDeletableAttributes(FileAttributes.ReparsePoint));
        Assert.NotNull(PathGuard.ValidateDeletableAttributes(FileAttributes.Offline));
        Assert.NotNull(PathGuard.ValidateDeletableAttributes((FileAttributes)0x00400000)); // RECALL_ON_DATA_ACCESS
        Assert.NotNull(PathGuard.ValidateDeletableAttributes((FileAttributes)0x00040000)); // RECALL_ON_OPEN
        Assert.Null(PathGuard.ValidateDeletableAttributes(FileAttributes.Normal));
        Assert.Null(PathGuard.ValidateDeletableAttributes(FileAttributes.Archive | FileAttributes.Hidden));
    }

    // -------------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"E:\", true)]
    public void Detects_drive_roots(string path, bool expected)
        => Assert.Equal(expected, PathGuard.IsDriveRoot(path));

    [Fact]
    public void IsUnder_semantics()
    {
        Assert.True(PathGuard.IsUnder(@"C:\a\b\c", @"C:\a"));
        Assert.True(PathGuard.IsUnder(@"C:\a\b", @"C:\A"));
        Assert.False(PathGuard.IsUnder(@"C:\a", @"C:\a"));
        Assert.False(PathGuard.IsUnder(@"C:\abc", @"C:\a"));
        Assert.False(PathGuard.IsUnder(@"C:\a", @"C:\a\b"));
    }
}
