using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenshotApi;
using Xunit;

namespace ScreenshotApi.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string TestKey = "TEST-ONLY-fictitious-secret-00000000000000000000";
    public string Storage { get; } = Path.Combine(Path.GetTempPath(), "ScreenshotApiTests", Guid.NewGuid().ToString("N"));
    public ManualClock Clock { get; } = new();
    public Dictionary<string, string?> Overrides { get; } = [];
    public RecordingLoggerProvider Logs { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Files:ApiKey"] = TestKey,
                ["Files:PublicBaseUrl"] = "https://files.example.invalid/agents",
                ["Files:StorageDirectory"] = Storage,
                ["Logging:LogLevel:Default"] = "None"
            };
            foreach (var entry in Overrides) values[entry.Key] = entry.Value;
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(Storage))
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ScreenshotApiTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Storage).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(Storage, recursive: true);
        }
    }
}

public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
    public void Dispose() { }
    private sealed class RecordingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }
}

public sealed class ApiTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;
    private static string GetPath(FileResponse result) => $"/files/{result.Id}{new Uri(result.Url).Query}";
    [Fact]
    public async Task Hosting_diagnostics_stay_disabled_even_with_provider_specific_trace_configuration()
    {
        using var factory = new ApiFactory();
        factory.Overrides["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace";
        factory.Overrides["Logging:Console:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace";
        using var client = factory.CreateClient();
        var diagnostics = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        Assert.False(diagnostics.IsEnabled(LogLevel.Critical));
        var result = await Save(client);
        using var response = await client.GetAsync(GetPath(result), CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hasCapturedLogs = !factory.Logs.Messages.IsEmpty;
        Assert.False(hasCapturedLogs);
    }
    public static TheoryData<byte[], string, string> Samples => new()
    {
        { Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aE5sAAAAASUVORK5CYII="), "image/png", "inline" },
        { Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"), "image/gif", "inline" },
        { Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF"), "application/pdf", "inline" },
        { new byte[] { 0,0,0,24,102,116,121,112,105,115,111,109,0,0,0,0,105,115,111,109,109,112,52,50 }, "video/mp4", "inline" },
        { new byte[] { 0,1,2,3,4,5,255,0 }, "application/octet-stream", "attachment" },
        { Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "application/octet-stream", "attachment" },
        { Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>"), "application/octet-stream", "attachment" },
        { Encoding.ASCII.GetBytes("alert('javascript');"), "application/octet-stream", "attachment" },
        { new byte[] { 77,90,1,2,3,4,5,6,7,8 }, "application/octet-stream", "attachment" },
        { new byte[] { 80,75,3,4,0,0,0,0,0,0 }, "application/octet-stream", "attachment" } // ZIP/Office accepted
    };
    private static HttpRequestMessage Upload(byte[]? bytes = null, string? key = ApiFactory.TestKey,
        string field = "file", bool secondFile = false)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? [1,2,3,4]);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/html"); // deliberately untrusted
        form.Add(part, field, "../../private-client-name.html");
        if (secondFile) form.Add(new ByteArrayContent([4,3,2,1]), "file", "extra");
        var request = new HttpRequestMessage(HttpMethod.Post, "/files") { Content = form };
        if (key is not null) request.Headers.Add("X-Api-Key", key);
        return request;
    }
    private static async Task<FileResponse> Save(HttpClient client, byte[]? bytes = null)
    {
        using var request = Upload(bytes);
        using var response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileResponse>(CT))!;
    }
    private static async Task Error(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(CT);
        Assert.Equal(code, json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetProperty("message").GetString()));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Any_format_roundtrips_exactly_with_conservative_serving(byte[] original, string type, string disposition)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = Upload(original);
        request.Headers.Host = "attacker.invalid";
        request.Headers.Add("X-Forwarded-Host", "attacker.invalid");
        using var response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(CT);
        Assert.Equal(factory.Clock.Now.AddDays(7),
            DateTimeOffset.Parse(Assert.Single(response.Headers.GetValues("X-FileDrop-Expires-At")), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3, json.EnumerateObject().Count());
        var result = json.Deserialize<FileResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Matches("^[a-f0-9]{64}$", result.Id);
        // Assert booleans so failure reports cannot serialize the generated bearer secret.
        var canonicalUrl = result.Url.StartsWith($"https://files.example.invalid/agents/files/{result.Id}?key=", StringComparison.Ordinal);
        var validSecret = System.Text.RegularExpressions.Regex.IsMatch(new Uri(result.Url).Query[5..], "^[a-zA-Z0-9_-]{43}$");
        Assert.True(canonicalUrl);
        Assert.True(validSecret);
        Assert.Equal(type, result.ContentType);
        Assert.True(result.Url == response.Headers.Location?.AbsoluteUri);
        using var get = await client.GetAsync(GetPath(result), CT); // no POST credential
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(original, await get.Content.ReadAsByteArrayAsync(CT));
        Assert.Equal(type, get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(disposition, get.Content.Headers.ContentDisposition?.DispositionType);
        Assert.DoesNotContain("..", get.Content.Headers.ContentDisposition?.ToString() ?? "");
        Assert.Equal("nosniff", Assert.Single(get.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", Assert.Single(get.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("sandbox", Assert.Single(get.Headers.GetValues("Content-Security-Policy")));
        Assert.Single(Directory.GetFiles(factory.Storage));
        Assert.Empty(Directory.GetFiles(factory.Storage, "*.tmp"));
    }

    [Fact]
    public async Task Storage_encrypts_directly_without_HTTP()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var bytes = new MemoryStream();
        bytes.Write(new byte[] { 1,2,3,4 });
        bytes.Position = 0;
        var stored = await factory.Services.GetRequiredService<FileStorage>().Save(bytes, "fixture.bin", CT);
        Assert.True(File.Exists(Path.Combine(factory.Storage, stored.Id + ".bin")));
    }

    [Fact]
    public async Task Ciphertext_contains_neither_plaintext_metadata_nor_key()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var original = Encoding.UTF8.GetBytes("PRIVATE-PLAINTEXT-unique-file-content-should-never-be-on-disk");
        var result = await Save(client, original);
        var path = Assert.Single(Directory.GetFiles(factory.Storage));
        Assert.Equal(result.Id + ".bin", Path.GetFileName(path));
        var envelope = await File.ReadAllBytesAsync(path, CT);
        Assert.Equal(-1, envelope.AsSpan().IndexOf(original));
        Assert.Equal(-1, envelope.AsSpan().IndexOf("private-client-name"u8));
        Assert.Equal(-1, envelope.AsSpan().IndexOf("application/octet-stream"u8));
        var token = new Uri(result.Url).Query[5..];
        Assert.Equal(-1, envelope.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token)));
        Assert.Equal(-1, envelope.AsSpan().IndexOf(WebEncoders.Base64UrlDecode(token)));
        Assert.True(envelope.AsSpan(0,5).SequenceEqual(new byte[] {70,68,65,80,1}));
        Assert.Empty(Directory.GetFiles(factory.Storage, "*.tmp"));
    }

    [Fact]
    public async Task Each_upload_has_distinct_id_secret_nonce_and_ciphertext()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var first = await Save(client);
        var second = await Save(client);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(new Uri(first.Url).Query != new Uri(second.Url).Query);
        var a = await File.ReadAllBytesAsync(Path.Combine(factory.Storage, first.Id + ".bin"), CT);
        var b = await File.ReadAllBytesAsync(Path.Combine(factory.Storage, second.Id + ".bin"), CT);
        Assert.False(a.AsSpan(13,12).SequenceEqual(b.AsSpan(13,12)));
        Assert.False(a.AsSpan(41).SequenceEqual(b.AsSpan(41)));
        Assert.False(Convert.FromHexString(first.Id).AsSpan().SequenceEqual(WebEncoders.Base64UrlDecode(new Uri(first.Url).Query[5..])));
        using var swapped = await client.GetAsync($"/files/{first.Id}{new Uri(second.Url).Query}", CT);
        await Error(swapped, 404, "not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?key=bad")]
    [InlineData("?key=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("?key=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&key=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Missing_or_wrong_read_secret_is_generic_404(string query)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var result = await Save(client);
        using var response = await client.GetAsync($"/files/{result.Id}{query}", CT);
        await Error(response, 404, "not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(25)]
    [InlineData(41)]
    public async Task Tampering_is_404_without_plaintext(int offset)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var result = await Save(client);
        var path = Path.Combine(factory.Storage, result.Id + ".bin");
        var envelope = await File.ReadAllBytesAsync(path, CT);
        envelope[offset] ^= 1;
        await File.WriteAllBytesAsync(path, envelope, CT);
        using var response = await client.GetAsync(GetPath(result), CT);
        await Error(response, 404, "not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(41)]
    [InlineData(50)]
    public async Task Truncated_envelope_is_generic_404(int length)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var result = await Save(client);
        var path = Path.Combine(factory.Storage, result.Id + ".bin");
        var envelope = await File.ReadAllBytesAsync(path, CT);
        await File.WriteAllBytesAsync(path, envelope[..length], CT);
        using var response = await client.GetAsync(GetPath(result), CT);
        await Error(response, 404, "not_found");
    }

    [Fact]
    public async Task Oversized_disk_envelope_is_rejected_before_reading()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var result = await Save(client);
        using (var file = File.OpenWrite(Path.Combine(factory.Storage, result.Id + ".bin")))
            file.SetLength(105_000_000);
        using var response = await client.GetAsync(GetPath(result), CT);
        await Error(response, 404, "not_found");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("TEST-ONLY-wrong-secret-000000000000000000000")]
    public async Task Wrong_upload_key_is_401_before_body_read(string? key)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = Upload(key: key);
        using var response = await client.SendAsync(request, CT);
        await Error(response, 401, "unauthorized");
        Assert.Empty(Directory.GetFiles(factory.Storage));
    }

    [Theory]
    [InlineData("wrong", false)]
    [InlineData("file", true)]
    public async Task Wrong_field_or_multiple_files_is_400(string field, bool second)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = Upload(field: field, secondFile: second);
        using var response = await client.SendAsync(request, CT);
        await Error(response, 400, "file_required");
        Assert.Empty(Directory.GetFiles(factory.Storage));
    }

    [Fact]
    public async Task Empty_file_is_400()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = Upload([]);
        using var response = await client.SendAsync(request, CT);
        await Error(response, 400, "empty_file");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Size_limit_applies_with_or_without_content_length(bool chunked)
    {
        using var factory = new ApiFactory();
        factory.Overrides["Files:MaxUploadBytes"] = "128";
        using var client = factory.CreateClient();
        using var request = Upload(new byte[129]);
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
            request.Content = new UnknownLengthContent(request.Content!);
        }
        using var response = await client.SendAsync(request, CT);
        await Error(response, 413, "upload_too_large");
        Assert.Empty(Directory.GetFiles(factory.Storage));
    }

    [Fact]
    public async Task Default_limit_is_100_MiB_and_large_declared_body_is_413()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(104857600, factory.Services.GetRequiredService<ApiSettings>().MaxUploadBytes);
        using var request = Upload();
        request.Content!.Headers.ContentLength = 104857600 + 65536 + 1;
        using var response = await client.SendAsync(request, CT);
        await Error(response, 413, "upload_too_large");
    }

    [Theory]
    [InlineData("application/json", "{}", 415, "multipart_required")]
    [InlineData("multipart/form-data", "", 400, "invalid_multipart")]
    [InlineData("multipart/form-data; boundary=x", "--x\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a\"\r\n\r\ntruncated", 400, "invalid_multipart")]
    [InlineData("multipart/form-data; boundary=x", "--x--\r\n", 400, "file_required")]
    public async Task Invalid_multipart_is_json(string type, string body, int status, string code)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/files") { Content = new StringContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        request.Headers.Add("X-Api-Key", ApiFactory.TestKey);
        using var response = await client.SendAsync(request, CT);
        await Error(response, status, code);
        Assert.Empty(Directory.GetFiles(factory.Storage));
    }

    [Theory]
    [InlineData("/files", 405, "method_not_allowed")]
    [InlineData("/files/not-an-id", 404, "not_found")]
    [InlineData("/files/..%5Cappsettings.json", 404, "not_found")]
    [InlineData("/files/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 404, "not_found")]
    public async Task No_listing_or_path_traversal(string path, int status, string code)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, CT);
        await Error(response, status, code);
    }

    [Fact]
    public async Task New_host_can_decrypt_existing_ciphertext_using_only_returned_url()
    {
        using var first = new ApiFactory();
        using var client = first.CreateClient();
        var result = await Save(client);
        using var second = new ApiFactory();
        second.Overrides["Files:StorageDirectory"] = first.Storage;
        using var reader = second.CreateClient();
        using var response = await reader.GetAsync(GetPath(result), CT);
        Assert.Equal(new byte[] {1,2,3,4}, await response.Content.ReadAsByteArrayAsync(CT));
    }

    [Fact]
    public async Task Expiry_uses_upload_time_and_rejects_at_exactly_seven_days()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var result = await Save(client);
        factory.Clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromMilliseconds(1);
        using var before = await client.GetAsync(GetPath(result), CT);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        factory.Clock.Now += TimeSpan.FromMilliseconds(1);
        using var after = await client.GetAsync(GetPath(result), CT);
        await Error(after, 404, "not_found");
        Assert.Single(Directory.GetFiles(factory.Storage)); // denied even before sweep
        Assert.Equal(1, factory.Services.GetRequiredService<FileStorage>().SweepExpired(CT));
        Assert.Empty(Directory.GetFiles(factory.Storage));
    }

    [Fact]
    public async Task Restart_runs_initial_sweep_with_controlled_clock()
    {
        using var first = new ApiFactory();
        using var writer = first.CreateClient();
        var result = await Save(writer);
        using var second = new ApiFactory();
        second.Clock.Now = first.Clock.Now + TimeSpan.FromDays(7);
        second.Overrides["Files:StorageDirectory"] = first.Storage;
        using var reader = second.CreateClient();
        Assert.False(File.Exists(Path.Combine(first.Storage, result.Id + ".bin")));
    }

    [Fact]
    public async Task Multiple_upload_keys_are_401()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = Upload();
        request.Headers.Add("X-Api-Key", ApiFactory.TestKey);
        using var response = await client.SendAsync(request, CT);
        await Error(response, 401, "unauthorized");
    }

    [Fact]
    public async Task Sweep_preserves_young_unknown_incomplete_and_locked_files_then_continues()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var locked = await Save(client);
        var expired = await Save(client);
        factory.Clock.Now += TimeSpan.FromDays(7);
        var young = await Save(client);
        await File.WriteAllTextAsync(Path.Combine(factory.Storage, "keep.txt"), "unrelated fixture", CT);
        await File.WriteAllBytesAsync(Path.Combine(factory.Storage, new string('e',64) + ".bin"), new byte[64], CT);
        await File.WriteAllBytesAsync(Path.Combine(factory.Storage, "." + new string('d',64) + ".tmp"), new byte[64], CT);
        using (var file = new FileStream(Path.Combine(factory.Storage, locked.Id + ".bin"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(1, factory.Services.GetRequiredService<FileStorage>().SweepExpired(CT));
        Assert.True(File.Exists(Path.Combine(factory.Storage, locked.Id + ".bin")));
        Assert.False(File.Exists(Path.Combine(factory.Storage, expired.Id + ".bin")));
        Assert.True(File.Exists(Path.Combine(factory.Storage, young.Id + ".bin")));
        Assert.True(File.Exists(Path.Combine(factory.Storage, "keep.txt")));
        Assert.Equal(5, Directory.GetFiles(factory.Storage).Length);
    }

    [Fact]
    public async Task Busy_transfer_is_429_with_retry_after()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var gate = factory.Services.GetRequiredService<SemaphoreSlim>();
        await gate.WaitAsync(CT);
        try
        {
            using var request = Upload();
            using var response = await client.SendAsync(request, CT);
            await Error(response, 429, "upload_busy");
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData("Files:ApiKey", "")]
    [InlineData("Files:ApiKey", "short")]
    [InlineData("Files:ApiKey", "replace-this-placeholder-key-before-use")]
    [InlineData("Files:PublicBaseUrl", "http://public.example.invalid")]
    [InlineData("Files:PublicBaseUrl", "https://files.example.invalid/?bad=true")]
    [InlineData("Files:StorageDirectory", "relative")]
    [InlineData("Files:MaxUploadBytes", "0")]
    public void Invalid_configuration_fails_closed(string setting, string value)
    {
        using var factory = new ApiFactory();
        factory.Overrides[setting] = value;
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
    [Theory]
    [InlineData("https://files.example.invalid:4958/agents", false)]
    [InlineData("https://files.example.invalid:4958/agents", true)]
    [InlineData("http://files.example.invalid:4958/agents", true)]
    [InlineData("http://127.0.0.1:4958/agents", false)]
    public async Task Configured_transport_is_preserved_in_returned_URL_and_roundtrip(string baseUrl, bool allowHttp)
    {
        using var factory = new ApiFactory();
        factory.Overrides["Files:PublicBaseUrl"] = baseUrl;
        factory.Overrides["Files:AllowInsecureHttp"] = allowHttp.ToString();
        using var client = factory.CreateClient();
        using var request = Upload();
        request.Headers.Host = "attacker.invalid";
        using var created = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = (await created.Content.ReadFromJsonAsync<FileResponse>(CT))!;
        var uri = new Uri(result.Url);
        Assert.Equal(baseUrl + "/files/" + result.Id, uri.GetLeftPart(UriPartial.Path));
        Assert.True(created.Headers.Location?.AbsoluteUri == result.Url);
        using var fetched = await client.GetAsync(GetPath(result), CT);
        Assert.Equal(new byte[] { 1,2,3,4 }, await fetched.Content.ReadAsByteArrayAsync(CT));
    }

    [Theory]
    [InlineData("http://files.example.invalid:4958/agents", false)]
    [InlineData("ftp://files.example.invalid/agents", true)]
    [InlineData("relative", true)]
    [InlineData("http://user:secret@files.example.invalid:4958", true)]
    [InlineData("http://files.example.invalid:4958/?bad=true", true)]
    [InlineData("http://files.example.invalid:4958/#fragment", true)]
    public void HTTP_optin_does_not_relax_other_URL_restrictions(string baseUrl, bool allowHttp)
    {
        using var factory = new ApiFactory();
        factory.Overrides["Files:PublicBaseUrl"] = baseUrl;
        factory.Overrides["Files:AllowInsecureHttp"] = allowHttp.ToString();
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Storage_inside_app_is_rejected()
    {
        using var factory = new ApiFactory();
        factory.Overrides["Files:StorageDirectory"] = AppContext.BaseDirectory;
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent inner;
        public UnknownLengthContent(HttpContent inner)
        {
            this.inner = inner;
            foreach (var h in inner.Headers)
                if (h.Key != "Content-Length") Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
