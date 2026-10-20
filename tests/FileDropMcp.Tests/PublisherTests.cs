using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class PublisherTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;
    private const string FakeUrl = "https://files.example.invalid/files/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?key=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static HttpResponseMessage Created(bool expiry = true, string url = FakeUrl)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new { id = new string('a', 64), url, contentType = "application/octet-stream" })
        };
        if (expiry) response.Headers.Add("X-FileDrop-Expires-At", "2026-10-27T12:00:00.0000000+00:00");
        return response;
    }
    private static FilePublisher Publisher(Fixture fixture, HttpMessageHandler handler, string? key = Fixture.FakeKey)
        => new(fixture.Options, fixture.Policy, new HttpClient(handler), key);
    private static async Task<string> Input(Fixture fixture)
    {
        var path = Path.Combine(fixture.Files, "document.bin");
        await File.WriteAllBytesAsync(path, [0,1,2,3,255], CT);
        return path;
    }
    [Theory]
    [InlineData("https", false)]
    [InlineData("https", true)]
    [InlineData("http", true)]
    public async Task Upload_and_full_GET_verification_use_key_only_on_POST_and_do_not_persist_url(string scheme, bool allowHttp)
    {
        using var fixture = new Fixture();
        fixture.Options.ApiEndpoint = scheme + "://files.example.invalid/files";
        fixture.Options.AllowInsecureHttp = allowHttp;
        fixture.Options.Validate();
        var expectedUrl = FakeUrl.Replace("https:", scheme + ":");
        await fixture.Activate();
        var input = await Input(fixture);
        var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(fixture.Options.ApiEndpoint, request.RequestUri?.AbsoluteUri);
                Assert.True(request.Headers.GetValues("X-Api-Key").Single() == Fixture.FakeKey);
                var body = await request.Content!.ReadAsByteArrayAsync(CT);
                Assert.True(body.AsSpan().IndexOf(new byte[] {0,1,2,3,255}) >= 0);
                return Created(url: expectedUrl);
            }
            Assert.True(request.RequestUri?.AbsoluteUri == expectedUrl);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0,1,2,3,255]) };
            result.Content.Headers.ContentType = new("application/octet-stream");
            result.Headers.Add("X-Content-Type-Options", "nosniff");
            return result;
        });
        var published = await Publisher(fixture, handler).Publish(input, "chat", "image-delivery", "task", CT);
        Assert.True(published.Success);
        Assert.False(published.RequiresVerification);
        Assert.True(published.Url == expectedUrl);
        Assert.Equal("document.bin", published.Name);
        Assert.Equal(new DateTimeOffset(2026,10,27,12,0,0,TimeSpan.Zero), published.ExpiresAt);
        Assert.Equal(2, handler.Calls);
        var disk = await File.ReadAllTextAsync(Path.Combine(fixture.Options.StateDirectory, "policy.json"), CT);
        Assert.DoesNotContain("http", disk);
        Assert.DoesNotContain("?key", disk);
        Assert.DoesNotContain(Fixture.FakeKey, disk);
        Assert.DoesNotContain("document.bin", disk);
        Assert.DoesNotContain(input, disk);
    }
    [Fact]
    public async Task Native_first_prevents_HTTP_before_any_failure()
    {
        using var fixture = new Fixture();
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.Equal("native_first", result.Code);
        Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData(DeliveryOutcome.Denied)]
    [InlineData(DeliveryOutcome.Cancelled)]
    [InlineData(DeliveryOutcome.PendingConfirmation)]
    [InlineData(DeliveryOutcome.Uncertain)]
    public async Task Blocked_native_outcomes_never_upload(DeliveryOutcome outcome)
    {
        using var fixture = new Fixture();
        await fixture.Policy.Get("chat", "image-delivery", "task", CT);
        await fixture.Policy.Record("chat", "image-delivery", "task", outcome, CT);
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.False(result.Success);
        Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData(302, true)]
    [InlineData(500, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(413, false)]
    [InlineData(429, false)]
    public async Task Status_codes_never_trigger_redirect_or_auto_retry(int status, bool uncertain)
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://other.example.invalid/steal");
            return Task.FromResult(response);
        });
        var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.False(result.Success);
        Assert.Equal(uncertain, result.RequiresVerification);
        Assert.Equal(1, handler.Calls);
        if (uncertain || status is 401 or 403)
        {
            var repeated = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
            Assert.False(repeated.Success);
            Assert.Equal(1, handler.Calls);
        }
    }
    [Theory]
    [InlineData("https", "https", "other.example.invalid")]
    [InlineData("https", "http", "files.example.invalid")]
    [InlineData("http", "https", "files.example.invalid")]
    [InlineData("http", "http", "other.example.invalid")]
    [InlineData("http", "http", "files.example.invalid:4959")]
    public async Task Response_URL_cannot_change_origin_even_with_HTTP_optin(string endpointScheme, string responseScheme, string responseHost)
    {
        using var fixture = new Fixture();
        fixture.Options.ApiEndpoint = endpointScheme + "://files.example.invalid/files";
        fixture.Options.AllowInsecureHttp = true;
        fixture.Options.Validate();
        await fixture.Activate();
        var changedUrl = FakeUrl.Replace("https:", responseScheme + ":").Replace("files.example.invalid", responseHost);
        var handler = new Handler(_ => Task.FromResult(Created(url: changedUrl)));
        var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.False(result.Success);
        Assert.True(result.RequiresVerification);
        Assert.Null(result.Url);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Transport_loss_after_send_is_uncertain_and_not_retried()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(_ => throw new HttpRequestException("TEST ONLY loss after request could have been delivered"));
        var publisher = Publisher(fixture, handler);
        var result = await publisher.Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.True(result.RequiresVerification);
        Assert.Equal("delivery_uncertain", result.Code);
        await publisher.Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Simulated_access_denied_is_terminal_without_retry_or_channel_switch()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(_ => throw new HttpRequestException("TEST ONLY", new SocketException(10013)));
        var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.Equal("permission_denied", result.Code);
        Assert.Equal("permission_denied", (await fixture.Policy.Get("chat", "image-delivery", "task", CT)).Code);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Failed_GET_returns_known_URL_without_reupload()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Post ? Created() : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var publisher = Publisher(fixture, handler);
        var result = await publisher.Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.True(result.Url == FakeUrl);
        Assert.True(result.RequiresVerification);
        await publisher.Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task Missing_expiry_retains_known_URL_and_never_duplicates_upload()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(_ => Task.FromResult(Created(expiry: false)));
        var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.True(result.RequiresVerification);
        Assert.True(result.Url == FakeUrl);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Credential_is_captured_at_construction_and_missing_credential_never_sends()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        var result = await Publisher(fixture, handler, key: null).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
        Assert.Equal("credentials_unavailable", result.Code);
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task State_write_failure_does_not_reclassify_a_permission_denial()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        FileStream? locked = null;
        try
        {
            var handler = new Handler(_ =>
            {
                locked = new FileStream(Path.Combine(fixture.Options.StateDirectory, "policy.json"), FileMode.Open, FileAccess.Read, FileShare.None);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            });
            var result = await Publisher(fixture, handler).Publish(await Input(fixture), "chat", "image-delivery", "task", CT);
            Assert.Equal("permission_denied", result.Code);
            Assert.False(result.RequiresVerification);
            Assert.Equal("state_save_failed", result.StateError);
        }
        finally { locked?.Dispose(); }
    }
    [Fact]
    public async Task File_outside_root_is_denied_before_read_or_HTTP()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send."));
        var result = await Publisher(fixture, handler).Publish(Path.Combine(fixture.Root, "outside.bin"), "chat", "image-delivery", "task", CT);
        Assert.Equal("path_not_allowed", result.Code);
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public void Production_handler_disables_all_redirects_cookies_and_default_credentials()
    {
        using var handler = FilePublisher.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request);
        }
    }
}
