using System.Diagnostics;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class ScriptUrlTests
{
    [Theory]
    [InlineData("https://files.example.invalid:4958/files", false, true)]
    [InlineData("https://files.example.invalid:4958/files", true, true)]
    [InlineData("http://files.example.invalid:4958/files", false, false)]
    [InlineData("http://files.example.invalid:4958/files", true, true)]
    [InlineData("ftp://files.example.invalid/files", true, false)]
    [InlineData("http://user:secret@files.example.invalid/files", true, false)]
    [InlineData("http://files.example.invalid/files?query=bad", true, false)]
    [InlineData("http://files.example.invalid/files#fragment", true, false)]
    public async Task Upload_script_validates_transport_before_any_file_or_network_access(string endpoint, bool allowHttp, bool accepted)
    {
        using var fixture = new Fixture();
        var marker = Path.Combine(fixture.Root, "guard-reached.txt");
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        // Run the real script, stopping at Get-Item before opening files or constructing HttpClient.
        const string command = """
            function Get-Item {
                param([string] $LiteralPath)
                [IO.File]::WriteAllText($env:FILEDROP_TEST_MARKER, 'input validation passed')
                throw 'TEST ONLY: intentionally stop before file access and HTTP'
            }
            $parameters = @{ FilePath='unused-test-path'; Endpoint=[uri]$env:FILEDROP_TEST_ENDPOINT }
            if ($env:FILEDROP_TEST_ALLOW -eq 'true') { $parameters.AllowInsecureHttp=$true }
            & $env:FILEDROP_TEST_SCRIPT @parameters
            """;
        var info = StartInfo(command);
        info.Environment["FILEDROP_TEST_SCRIPT"] = Path.Combine(project, "scripts", "Upload-File.ps1");
        info.Environment["FILEDROP_TEST_MARKER"] = marker;
        info.Environment["FILEDROP_TEST_ENDPOINT"] = endpoint;
        info.Environment["FILEDROP_TEST_ALLOW"] = allowHttp ? "true" : "false";
        info.Environment["FILE_API_KEY"] = Fixture.FakeKey;
        var result = await Run(info);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(accepted, File.Exists(marker));
        Assert.DoesNotContain(Fixture.FakeKey, result.Output + result.Error);
        Assert.DoesNotContain(endpoint, result.Output + result.Error);
    }

    [Fact]
    public async Task Installer_rejects_external_HTTP_by_default_before_any_mutation()
    {
        using var fixture = new Fixture();
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        const string command = """
            & $env:FILEDROP_TEST_SCRIPT -ApiEndpoint 'http://files.example.invalid:4958/files' -Roots $env:FILEDROP_TEST_ROOT
            """;
        var info = StartInfo(command);
        info.Environment["FILEDROP_TEST_SCRIPT"] = Path.Combine(project, "integration", "Install-FileDropMcp.ps1");
        info.Environment["FILEDROP_TEST_ROOT"] = fixture.Files;
        var result = await Run(info);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("HTTP requires -AllowInsecureHttp", result.Error);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    private static ProcessStartInfo StartInfo(string command)
    {
        var info = new ProcessStartInfo("pwsh") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var arg in new[] { "-NoProfile", "-Command", command }) info.ArgumentList.Add(arg);
        return info;
    }
    private static async Task<(int ExitCode, string Output, string Error)> Run(ProcessStartInfo info)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = child.StandardError.ReadToEndAsync(timeout.Token);
        await child.WaitForExitAsync(timeout.Token);
        return (child.ExitCode, await output, await error);
    }
}
