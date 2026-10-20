using System.Diagnostics;
using System.Text.Json;
using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class StdioTests
{
    [Fact]
    public async Task Official_SDK_stdio_discovers_tools_validates_arguments_and_never_needs_network()
    {
        using var fixture = new Fixture();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var config = Path.Combine(fixture.Root, "mcp-config.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(fixture.Options), ct);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Reuse this test project's deps/runtimeconfig; no named-pipe test-runner IPC.
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--runtimeconfig");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FileDropMcp.Tests.runtimeconfig.json"));
        info.ArgumentList.Add("--depsfile");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FileDropMcp.Tests.deps.json"));
        info.ArgumentList.Add(typeof(DeliveryTools).Assembly.Location);
        info.Environment["FILEDROP_CONFIG"] = config;
        info.Environment["FILE_API_KEY"] = Fixture.FakeKey;
        info.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            var initialized = await Rpc(process, 1, "initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "local-fixture", version = "1" }
            }, ct);
            Assert.True(initialized.TryGetProperty("result", out var init));
            Assert.Equal("filedrop-delivery", init.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.Contains("Native first", init.GetProperty("instructions").GetString() ?? "");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            var list = await Rpc(process, 2, "tools/list", new { }, ct);
            var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
            Assert.Equal(4, names.Length);
            Assert.Contains("get_delivery_policy", names);
            Assert.Contains("publish_file", names);
            var publishSchema = list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "publish_file");
            var properties = publishSchema.GetProperty("inputSchema").GetProperty("properties");
            Assert.False(properties.TryGetProperty("api_key", out _));
            Assert.False(properties.TryGetProperty("endpoint", out _));
            Assert.False(properties.TryGetProperty("cancellationToken", out _));
            var scope = new { channel = "chat", capability = "image-delivery", task_id = "stdio-task" };
            var first = await Call(process, 3, "get_delivery_policy", scope, ct);
            Assert.Equal("native", first.GetProperty("action").GetString());
            var failed = await Call(process, 4, "record_delivery_outcome", new
            {
                scope.channel, scope.capability, scope.task_id, outcome = "technical_failure"
            }, ct);
            Assert.True(failed.GetProperty("mayPublish").GetBoolean());
            var invalid = await Rpc(process, 5, "tools/call", new { name = "get_delivery_policy", arguments = new { channel = 42 } }, ct);
            var rejected = invalid.TryGetProperty("error", out _) ||
                (invalid.TryGetProperty("result", out var invalidResult) && invalidResult.TryGetProperty("isError", out var error) && error.GetBoolean());
            Assert.True(rejected);
            var denied = await Call(process, 6, "publish_file", new
            {
                path = Path.Combine(fixture.Root, "outside-root.bin"), scope.channel, scope.capability, scope.task_id
            }, ct);
            Assert.Equal("path_not_allowed", denied.GetProperty("code").GetString());
            var policy = await Call(process, 7, "get_delivery_policy", scope, ct);
            Assert.Equal("permission_denied", policy.GetProperty("code").GetString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
    private static async Task<JsonElement> Rpc(Process process, int id, string method, object parameters, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await process.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            Assert.NotNull(line);
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                return document.RootElement.Clone();
        }
    }
    private static async Task<JsonElement> Call(Process process, int id, string name, object arguments, CancellationToken ct)
    {
        var response = await Rpc(process, id, "tools/call", new { name, arguments }, ct);
        var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var parsed = JsonDocument.Parse(text);
        return parsed.RootElement.Clone();
    }
}
