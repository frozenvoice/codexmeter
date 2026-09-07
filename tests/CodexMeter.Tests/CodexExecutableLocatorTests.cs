using CodexMeter.Codex;

namespace CodexMeter.Tests;

public class CodexExecutableLocatorTests
{
    [Fact]
    public void MissingExecutable_ReturnsNull()
    {
        var locator = new CodexExecutableLocator(new MemoryCodexFileSystem());
        Assert.Null(locator.Locate(null));
    }

    [Fact]
    public void RelativeConfiguredPath_IsRejected()
    {
        var files = new MemoryCodexFileSystem();
        files.Files.Add(Path.GetFullPath("codex.exe"));
        var locator = new CodexExecutableLocator(files);
        Assert.Null(locator.Locate("codex.exe"));
        Assert.Null(locator.Locate(@"..\codex.exe"));
    }

    [Fact]
    public void PathDiscovery_PrefersNativeExe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-codex-path");
        var exe = Path.Combine(dir, "codex.exe");
        var files = new MemoryCodexFileSystem();
        files.PathFolders.Add(dir);
        files.Files.Add(exe);
        var command = new CodexExecutableLocator(files).Locate(null);
        Assert.NotNull(command);
        Assert.Equal(exe, command.ResolvedExecutable);
        Assert.False(command.UsesCmd);
        Assert.Equal("app-server --stdio", command.Arguments);
        Assert.Equal(exe, command.FileName);
    }

    [Fact]
    public void CmdLaunch_UsesSafelyQuotedCmd()
    {
        var dir = @"C:\Users\example\AppData\Roaming\npm";
        var cmd = Path.Combine(dir, "codex.cmd");
        var files = new MemoryCodexFileSystem();
        files.CommonFolders.Add(dir);
        files.Files.Add(cmd);
        var command = new CodexExecutableLocator(files).Locate(null);
        Assert.NotNull(command);
        Assert.True(command.UsesCmd);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), command.FileName);
        Assert.Equal(CodexProcessQuoting.CmdLaunchArguments(cmd), command.Arguments);
        Assert.StartsWith("/d /s /c \"\"", command.Arguments, StringComparison.Ordinal);
        Assert.Contains("app-server --stdio", command.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.json", command.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatLaunch_UsesCmdQuoting()
    {
        var bat = @"D:\tools\codex.bat";
        var files = new MemoryCodexFileSystem { PathFolders = { @"D:\tools" } };
        files.Files.Add(bat);
        var command = new CodexExecutableLocator(files).Locate(null);
        Assert.NotNull(command);
        Assert.True(command.UsesCmd);
        Assert.Equal(CodexProcessQuoting.CmdLaunchArguments(bat), command.Arguments);
    }

    [Fact]
    public void ConfiguredAbsolutePath_Wins()
    {
        var configured = @"E:\Codex\codex.exe";
        var files = new MemoryCodexFileSystem();
        files.Files.Add(configured);
        files.PathFolders.Add(@"C:\Windows");
        files.Files.Add(@"C:\Windows\codex.exe");
        var command = new CodexExecutableLocator(files).Locate(configured);
        Assert.Equal(configured, command?.ResolvedExecutable);
    }

    [Fact]
    public void Discovery_DoesNotReadAuthFiles()
    {
        var locator = File.ReadAllText(Path.Combine(FindCore(), "CodexExecutableLocator.cs"));
        var client = File.ReadAllText(Path.Combine(FindCore(), "CodexAppServerClient.cs"));
        var process = File.ReadAllText(Path.Combine(FindCore(), "CodexProcessHost.cs"));
        foreach (var source in new[] { locator, client, process })
        {
            Assert.DoesNotContain(".codex/auth.json", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("File.ReadAllText", source, StringComparison.Ordinal);
            Assert.DoesNotContain("File.ReadAllBytes", source, StringComparison.Ordinal);
        }
    }

    private static string FindCore()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "CodexMeter.Core", "Codex");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Codex source");
    }
}
