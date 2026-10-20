using System.Diagnostics;
using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class InstallerTests
{
    [Theory]
    [InlineData("https", false)]
    [InlineData("http", true)]
    public async Task Installer_preview_is_read_only_apply_preserves_config_and_backups_and_repeat_refuses(string scheme, bool allowHttp)
    {
        using var fixture = new Fixture();
        fixture.Options.ApiEndpoint = scheme + "://files.example.invalid/files";
        fixture.Options.AllowInsecureHttp = allowHttp;
        var ct = TestContext.Current.CancellationToken;
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var installer = Path.Combine(project, "integration", "Install-FileDropMcp.ps1");
        var package = Path.Combine(fixture.Root, "package");
        Directory.CreateDirectory(Path.Combine(package, "server"));
        Directory.CreateDirectory(Path.Combine(package, "skill", "filedrop-delivery"));
        // Inert synthetic payload: installer tests never execute the installed server or client.
        await File.WriteAllTextAsync(Path.Combine(package, "server", "FileDropMcp.exe"), "INERT TEST FIXTURE", ct);
        File.Copy(Path.Combine(project, "integration", "skill", "filedrop-delivery", "SKILL.md"),
            Path.Combine(package, "skill", "filedrop-delivery", "SKILL.md"));
        File.Copy(Path.Combine(project, "integration", "Launch-CodexWithFileDrop.ps1"), Path.Combine(package, "Launch-CodexWithFileDrop.ps1"));
        var home = Path.Combine(fixture.Root, "codex-home");
        Directory.CreateDirectory(home);
        const string originalConfig = "model = \"existing-model\"\n[mcp_servers.keep]\ncommand = \"keep-existing\"\n";
        const string originalAgents = "Preserve existing instructions.\n";
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), originalConfig, ct);
        await File.WriteAllTextAsync(Path.Combine(home, "AGENTS.md"), originalAgents, ct);
        var install = Path.Combine(fixture.Root, "install");
        async Task<int> Run(bool apply, string target, string codeHome)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-File", installer, "-ApiEndpoint", fixture.Options.ApiEndpoint, "-Roots", fixture.Files,
                         "-InstallDirectory", target, "-CodexHomePath", codeHome, "-PackageRoot", package }) info.ArgumentList.Add(arg);
            if (apply) info.ArgumentList.Add("-Apply");
            if (allowHttp) info.ArgumentList.Add("-AllowInsecureHttp");
            using var child = Process.Start(info)!;
            var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            await stdout;
            await stderr;
            return child.ExitCode;
        }
        Assert.Equal(0, await Run(false, install, home));
        Assert.False(Directory.Exists(install));
        Assert.Equal(originalConfig, await File.ReadAllTextAsync(Path.Combine(home, "config.toml"), ct));
        Assert.Empty(Directory.GetFiles(home, "*.bak-*"));
        Assert.Equal(0, await Run(true, install, home));
        var configured = await File.ReadAllTextAsync(Path.Combine(home, "config.toml"), ct);
        Assert.StartsWith(originalConfig, configured);
        Assert.Contains("env_vars = [\"FILE_API_KEY\"]", configured);
        Assert.DoesNotContain(Fixture.FakeKey, configured);
        Assert.Equal(originalConfig, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(home, "config.toml.bak-*")), ct));
        Assert.StartsWith(originalAgents, await File.ReadAllTextAsync(Path.Combine(home, "AGENTS.md"), ct));
        Assert.Equal(originalAgents, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(home, "AGENTS.md.bak-*")), ct));
        Assert.True(File.Exists(Path.Combine(home, "skills", "filedrop-delivery", "SKILL.md")));
        var options = McpOptions.Load(Path.Combine(install, "mcp-config.json"));
        Assert.Equal(fixture.Options.ApiEndpoint, options.ApiEndpoint);
        Assert.Equal(allowHttp, options.AllowInsecureHttp);
        Assert.Single(options.Roots);
        Assert.False(Directory.Exists(options.StateDirectory)); // installer does not start MCP or upload
        var previous = configured;
        Assert.NotEqual(0, await Run(true, Path.Combine(fixture.Root, "other-install"), home));
        Assert.Equal(previous, await File.ReadAllTextAsync(Path.Combine(home, "config.toml"), ct));
    }
}
