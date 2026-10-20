using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileDropMcp;

public sealed record PublishResult(bool Success, string Code, string DeliveryStatus, bool RequiresVerification,
    string? Url = null, string? Name = null, string? ContentType = null, DateTimeOffset? ExpiresAt = null, string? StateError = null);

public sealed class FilePublisher
{
    private readonly McpOptions options;
    private readonly PolicyStore policy;
    private readonly HttpClient http;
    private readonly string? apiKey;
    private static readonly HashSet<string> Types =
        ["image/png", "image/jpeg", "image/gif", "image/webp", "video/mp4", "video/webm", "video/ogg",
         "audio/mp4", "audio/wav", "audio/ogg", "audio/mpeg", "application/pdf", "application/octet-stream"];
    public FilePublisher(McpOptions options, PolicyStore policy, HttpClient http, string? apiKey)
    {
        this.options = options;
        this.policy = policy;
        this.http = http;
        this.apiKey = apiKey is { Length: >= 32 and <= 256 } && apiKey.All(c => c is >= '!' and <= '~') ? apiKey : null;
    }
    public static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
    };
    public static HttpClient CreateHttpClient() => new(CreateHandler()) { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<PublishResult> Publish(string path, string channel, string capability, string taskId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return new(false, "cancelled", "not_sent", false);
        var decision = await policy.BeginPublish(channel, capability, taskId, ct);
        if (!decision.MayPublish) return new(false, decision.Code, "not_sent", decision.Action == "verify_result");
        using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transferTimeout.CancelAfter(TimeSpan.FromMinutes(4));
        ct = transferTimeout.Token;
        var disposition = "not_sent";
        var sending = false;
        PublishResult? committed = null;
        async Task<PublishResult> Transfer()
        {
        try
        {
            if (apiKey is null) return new(false, "credentials_unavailable", "not_sent", false);
            using var file = PathGuard.OpenAuthorized(path, options);
            var expectedLength = file.Length;
            var expectedHash = await SHA256.HashDataAsync(file, ct);
            file.Position = 0;
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
            request.Headers.Add("X-Api-Key", apiKey);
            var form = new MultipartFormDataContent();
            request.Content = form;
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var name = DisplayName(Path.GetFileName(path));
            form.Add(content, "file", name);
            ct.ThrowIfCancellationRequested();
            sending = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status != 201)
            {
                disposition = status is 401 or 403 ? "denied" : status >= 500 || status is >= 300 and < 400 ? "uncertain" : "not_sent";
                return new(false, status is >= 300 and < 400 ? "redirect_rejected" : status is 401 or 403 ? "permission_denied" : "upload_rejected",
                    disposition == "uncertain" ? "uncertain" : "not_sent", disposition == "uncertain");
            }
            disposition = "uncertain"; // A file exists even if response parsing or verification now fails.
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var json = await ReadLimited(body, 8192, ct);
            ApiResponse? result;
            try { result = JsonSerializer.Deserialize<ApiResponse>(json, JsonOutput.Options); }
            catch (JsonException) { return new(false, "upload_response_invalid", "uploaded_unverified", true); }
            if (result is null || !ValidUrl(result))
                return new(false, "upload_response_invalid", "uploaded_unverified", true);
            committed = new(false, "expiry_metadata_missing", "uploaded_unverified", true, result.Url, name, result.ContentType);
            if (!response.Headers.TryGetValues("X-FileDrop-Expires-At", out var expiryValues) ||
                !DateTimeOffset.TryParseExact(expiryValues.SingleOrDefault(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt))
                return committed;
            committed = new(false, "verification_failed", "uploaded_unverified", true, result.Url, name, result.ContentType, expiresAt);
            using var get = new HttpRequestMessage(HttpMethod.Get, result.Url); // Never attach POST credential here.
            using var verification = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            if (verification.StatusCode != HttpStatusCode.OK ||
                verification.Content.Headers.ContentType?.MediaType != result.ContentType ||
                !verification.Headers.TryGetValues("X-Content-Type-Options", out var sniff) || sniff.SingleOrDefault() != "nosniff" ||
                verification.Content.Headers.ContentLength > McpOptions.MaxFileBytes) return committed;
            await using var download = await verification.Content.ReadAsStreamAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await download.ReadAsync(buffer, ct)) != 0)
            {
                total += count;
                if (total > McpOptions.MaxFileBytes || total > expectedLength) return committed;
                hash.AppendData(buffer, 0, count);
            }
            if (total != expectedLength || !CryptographicOperations.FixedTimeEquals(expectedHash, hash.GetHashAndReset())) return committed;
            disposition = "delivered";
            return committed with { Success = true, Code = "delivered", DeliveryStatus = "delivered", RequiresVerification = false };
        }
        catch (DeliveryException exception)
        {
            disposition = exception.Code == "path_not_allowed" ? "denied" : sending ? "uncertain" : "not_sent";
            return committed ?? new(false, exception.Code, sending ? "uncertain" : "not_sent", sending);
        }
        catch (UnauthorizedAccessException)
        {
            disposition = committed is null ? "denied" : "uncertain";
            return committed ?? new(false, "permission_denied", "not_sent", false);
        }
        catch (HttpRequestException exception) when (DeniedSocket(exception))
        {
            disposition = committed is null ? "denied" : "uncertain";
            return committed ?? new(false, "permission_denied", "not_sent", false);
        }
        catch (OperationCanceledException)
        {
            disposition = sending ? "uncertain" : "cancelled";
            return committed ?? new(false, sending ? "delivery_uncertain" : "cancelled", sending ? "uncertain" : "not_sent", sending);
        }
        catch (Exception)
        {
            disposition = sending ? "uncertain" : "not_sent";
            return committed ?? new(false, sending ? "delivery_uncertain" : "local_file_error", sending ? "uncertain" : "not_sent", sending);
        }
        }
        var finalResult = await Transfer();
        try
        {
            // The state stores only disposition, never path, URL, key, name, hash or content.
            // Use a separate short timeout so caller cancellation cannot erase uncertainty.
            using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await policy.FinishPublish(channel, capability, taskId, disposition, saveTimeout.Token);
        }
        catch (Exception)
        {
            if (disposition is "denied" or "cancelled")
                return finalResult with { Success = false, StateError = "state_save_failed" };
            return finalResult with { Success = false, Code = "state_save_failed", DeliveryStatus = sending ? "uncertain" : "not_sent", RequiresVerification = sending, StateError = "state_save_failed" };
        }
        return finalResult;
    }

    private bool ValidUrl(ApiResponse result)
    {
        if (!Regex.IsMatch(result.Id ?? "", "^[a-f0-9]{64}$") || !Types.Contains(result.ContentType ?? "") ||
            !Uri.TryCreate(result.Url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
            uri.Authority.Equals(options.Endpoint.Authority, StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath == options.Endpoint.AbsolutePath + "/" + result.Id &&
            Regex.IsMatch(uri.Query, "^\\?key=[a-zA-Z0-9_-]{43}$", RegexOptions.CultureInvariant);
    }
    private static bool DeniedSocket(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AccessDenied) return true;
        return false;
    }
    private static async Task<byte[]> ReadLimited(Stream stream, int limit, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + count > limit) throw new DeliveryException("upload_response_invalid");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    private static string DisplayName(string name)
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || " ._-()".Contains(c)).Take(120).ToArray()).Trim(' ', '.');
        return string.IsNullOrEmpty(safe) ? "download.bin" : safe;
    }
    private sealed record ApiResponse(string Id, string Url, string ContentType);
}
