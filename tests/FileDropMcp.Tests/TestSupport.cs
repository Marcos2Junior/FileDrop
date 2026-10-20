using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class Fixture : IDisposable
{
    public const string FakeKey = "TEST-ONLY-fictitious-secret-00000000000000000000";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FileDropMcpTests", Guid.NewGuid().ToString("N"));
    public string Files => Path.Combine(Root, "files");
    public Clock Clock { get; } = new();
    public McpOptions Options { get; }
    public PolicyStore Policy { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Files);
        Options = new() { ApiEndpoint = "https://files.example.invalid/files", Roots = [Files], StateDirectory = Path.Combine(Root, "state") };
        Options.Validate();
        Policy = new(Options, Clock);
    }
    public async Task Activate(string task = "task")
    {
        await Policy.Get("chat", "image-delivery", task, TestContext.Current.CancellationToken);
        await Policy.Record("chat", "image-delivery", task, DeliveryOutcome.TechnicalFailure, TestContext.Current.CancellationToken);
    }
    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FileDropMcpTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
