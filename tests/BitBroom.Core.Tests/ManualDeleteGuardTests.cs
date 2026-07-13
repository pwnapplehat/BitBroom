using BitBroom.Core.Engine;
using Xunit;

namespace BitBroom.Core.Tests;

public class ManualDeleteGuardTests
{
    private static string Drive => System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

    [Fact]
    public void Refuses_drive_root()
    {
        Assert.NotNull(ManualDeleteGuard.Validate(Drive));
        Assert.False(ManualDeleteGuard.CanDelete(Drive));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    public void Refuses_protected_roots(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Assert.False(ManualDeleteGuard.CanDelete(path));
    }

    [Fact]
    public void Refuses_the_users_root()
    {
        string users = System.IO.Path.Combine(Drive, "Users");
        Assert.False(ManualDeleteGuard.CanDelete(users));
    }

    [Fact]
    public void Refuses_content_inside_windows_and_program_files()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(ManualDeleteGuard.CanDelete(System.IO.Path.Combine(windows, "System32")));
        Assert.False(ManualDeleteGuard.CanDelete(System.IO.Path.Combine(pf, "SomeApp", "app.exe")));
    }

    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("$Recycle.Bin")]
    [InlineData("System Volume Information")]
    public void Refuses_system_managed_items_at_drive_root(string name)
    {
        string path = System.IO.Path.Combine(Drive, name);
        Assert.False(ManualDeleteGuard.CanDelete(path));
    }

    [Fact]
    public void Allows_content_inside_the_user_profile()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A subfolder / file of the profile is the user's own content — deletable (confirmed, recoverable).
        Assert.True(ManualDeleteGuard.CanDelete(System.IO.Path.Combine(profile, "Downloads", "bigmovie.mkv")));
        Assert.True(ManualDeleteGuard.CanDelete(System.IO.Path.Combine(profile, "Downloads")));
    }

    [Fact]
    public void Allows_ordinary_folders_on_any_drive()
    {
        Assert.True(ManualDeleteGuard.CanDelete(@"D:\Projects\old-build"));
        Assert.True(ManualDeleteGuard.CanDelete(@"E:\Games\SomeGame"));
        Assert.True(ManualDeleteGuard.CanDelete(System.IO.Path.Combine(Drive, "MyStuff", "junk")));
    }

    [Fact]
    public void Refuses_malformed_paths()
    {
        Assert.False(ManualDeleteGuard.CanDelete("not-a-rooted-path"));
        Assert.False(ManualDeleteGuard.CanDelete(""));
    }
}
