using System.Diagnostics;
using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class JunctionTests
{
    [Fact]
    public async Task Junction_inside_upload_root_cannot_escape_or_be_followed()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("FILEDROP_TEST_JUNCTION") != "1",
            "Junction creation was denied by this sandbox. Opt in only in an environment already authorized for fixture junctions; never elevate permissions for this test.");
        using var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "private.bin"), [1,2,3], ct);
        var link = Path.Combine(fixture.Files, "junction");
        var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("New-Item -ItemType Junction -Path $env:FILEDROP_FIXTURE_LINK -Target $env:FILEDROP_FIXTURE_TARGET -ErrorAction Stop | Out-Null");
        info.Environment["FILEDROP_FIXTURE_LINK"] = link;
        info.Environment["FILEDROP_FIXTURE_TARGET"] = outside;
        using var child = Process.Start(info)!;
        var stdout = child.StandardOutput.ReadToEndAsync(ct);
        var stderr = child.StandardError.ReadToEndAsync(ct);
        try
        {
            await child.WaitForExitAsync(ct);
            await stdout;
            var error = await stderr;
            Assert.True(child.ExitCode == 0, "Fixture junction creation failed: " + error);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            Assert.Throws<DeliveryException>(() => PathGuard.OpenAuthorized(Path.Combine(link, "private.bin"), fixture.Options));
        }
        finally
        {
            // Only remove this fixture's junction itself, never its target recursively.
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        }
    }
}
