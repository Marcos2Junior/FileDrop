using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class PathTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Authorized_regular_file_can_be_opened_without_write_sharing()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Files, "file.bin");
        await File.WriteAllBytesAsync(path, [1,2,3], CT);
        using var opened = PathGuard.OpenAuthorized(path, fixture.Options);
        Assert.Equal(3, opened.Length);
    }
    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("relative.bin")]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"C:\files\file:stream")]
    public void Relative_network_and_ADS_paths_are_rejected(string path)
    {
        using var fixture = new Fixture();
        Assert.Throws<DeliveryException>(() => PathGuard.OpenAuthorized(path, fixture.Options));
    }
    [Fact]
    public async Task Traversal_cannot_escape_a_root_and_sibling_prefix_is_not_a_root()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside.bin");
        await File.WriteAllBytesAsync(outside, [1], CT);
        Assert.Throws<DeliveryException>(() => PathGuard.OpenAuthorized(Path.Combine(fixture.Files, "..", "outside.bin"), fixture.Options));
        var sibling = Path.Combine(fixture.Root, "files-other");
        Directory.CreateDirectory(sibling);
        var path = Path.Combine(sibling, "file.bin");
        await File.WriteAllBytesAsync(path, [1], CT);
        Assert.Throws<DeliveryException>(() => PathGuard.OpenAuthorized(path, fixture.Options));
    }
    [Fact]
    public async Task Sparse_file_over_100MiB_is_rejected_before_reading()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Files, "oversized.bin");
        await using (var file = File.Create(path)) file.SetLength(McpOptions.MaxFileBytes + 1);
        Assert.Throws<DeliveryException>(() => PathGuard.OpenAuthorized(path, fixture.Options));
    }
    [Theory]
    [InlineData("http://files.example.invalid/files")]
    [InlineData("https://user:secret@files.example.invalid/files")]
    [InlineData("https://files.example.invalid/files?key=forbidden")]
    [InlineData("https://files.example.invalid/other")]
    public void Endpoint_must_be_fixed_HTTPS_without_credentials_query_or_fragment(string endpoint)
    {
        using var fixture = new Fixture();
        fixture.Options.ApiEndpoint = endpoint;
        Assert.Throws<DeliveryException>(() => fixture.Options.Validate());
    }

    [Theory]
    [InlineData("https://files.example.invalid:4958/files", false)]
    [InlineData("https://files.example.invalid:4958/files", true)]
    [InlineData("http://files.example.invalid:4958/files", true)]
    public void Endpoint_allows_external_HTTP_only_with_explicit_optin(string endpoint, bool allowHttp)
    {
        using var fixture = new Fixture();
        fixture.Options.ApiEndpoint = endpoint;
        fixture.Options.AllowInsecureHttp = allowHttp;
        fixture.Options.Validate();
        Assert.Equal(endpoint, fixture.Options.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://files.example.invalid:4958/files", false)]
    [InlineData("ftp://files.example.invalid/files", true)]
    [InlineData("relative", true)]
    [InlineData("http://user:secret@files.example.invalid/files", true)]
    [InlineData("http://files.example.invalid/files?query=bad", true)]
    [InlineData("http://files.example.invalid/files#fragment", true)]
    [InlineData("http://files.example.invalid/%66iles", true)]
    public void HTTP_optin_preserves_endpoint_restrictions(string endpoint, bool allowHttp)
    {
        using var fixture = new Fixture();
        fixture.Options.ApiEndpoint = endpoint;
        fixture.Options.AllowInsecureHttp = allowHttp;
        Assert.Throws<DeliveryException>(() => fixture.Options.Validate());
    }
}
