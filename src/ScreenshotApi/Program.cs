using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ScreenshotApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.PostConfigure<LoggerFilterOptions>(options =>
{
    // Keep this policy after configuration/reload: hosting diagnostics can include bearer queries.
    // No provider/category-specific Trace setting may re-enable those request/response logs.
    options.MinLevel = LogLevel.Warning;
    options.Rules.Clear();
    options.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None, null));
});
builder.Services.AddSingleton(services =>
{
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var configured = services.GetRequiredService<IConfiguration>().GetSection("Files").Get<ApiSettings>() ?? new ApiSettings();
    configured.Validate(environment, environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
    return configured;
});
builder.Services.AddOptions<KestrelServerOptions>().Configure<ApiSettings>((options, configured) => options.Limits.MaxRequestBodySize = configured.MaxRequestBytes);
builder.Services.AddOptions<IISServerOptions>().Configure<ApiSettings>((options, configured) => options.MaxRequestBodySize = configured.MaxRequestBytes);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FileStorage>();
builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
builder.Services.AddHostedService<FileRetention>();
var app = builder.Build();
var settings = app.Services.GetRequiredService<ApiSettings>();
Directory.CreateDirectory(settings.StorageDirectory);
var expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey));

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    try
    {
        await next(context);
    }
    catch (ApiException exception) when (!context.Response.HasStarted)
    {
        await ApiError.Write(context, exception.Status, exception.Code, exception.Message);
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception) when (!context.Response.HasStarted)
    {
        var tooLarge = exception.StatusCode == StatusCodes.Status413PayloadTooLarge;
        await ApiError.Write(context, tooLarge ? 413 : 400, tooLarge ? "upload_too_large" : "invalid_request",
            tooLarge ? "Upload excede o limite permitido." : "Requisicao HTTP invalida.");
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // Client disconnected; storage's finally removes this request's unfinished file.
    }
    catch (Exception) when (!context.Response.HasStarted)
    {
        // Do not log keys, headers, filenames, capability URLs or exception payloads.
        app.Logger.LogError("Falha interna ao processar requisicao. TraceId: {TraceId}", context.TraceIdentifier);
        await ApiError.Write(context, 500, "internal_error", "Falha interna. Repetir o upload pode criar outro arquivo.");
    }
});
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    await ApiError.Write(statusContext.HttpContext, response.StatusCode,
        response.StatusCode == 405 ? "method_not_allowed" : "not_found",
        response.StatusCode == 405 ? "Metodo nao permitido." : "Recurso nao encontrado.");
});

app.MapPost("/files", async (HttpContext context, FileStorage storage, SemaphoreSlim uploads) =>
{
    var keys = context.Request.Headers["X-Api-Key"];
    if (keys.Count != 1 || keys[0] is not { Length: >= 32 and <= 256 } suppliedKey ||
        !CryptographicOperations.FixedTimeEquals(expectedKeyHash, SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey))))
        throw new ApiException(401, "unauthorized", "X-Api-Key ausente ou invalida.");

    if (!await uploads.WaitAsync(0, context.RequestAborted))
    {
        context.Response.Headers.RetryAfter = "1";
        throw new ApiException(429, "upload_busy", "Uploads ocupados. Tente novamente mais tarde.");
    }
    try
    {
        if (context.Request.ContentLength > settings.MaxRequestBytes)
            throw new ApiException(413, "upload_too_large", "Upload excede o limite permitido.");
        using var upload = await UploadReader.Read(context.Request, settings.MaxUploadBytes, context.RequestAborted);
        var saved = await storage.Save(upload.Bytes, upload.FileName, context.RequestAborted);
        context.Response.Headers["X-FileDrop-Expires-At"] = saved.ExpiresAt.ToString("O");
        return Results.Created(saved.Url, saved);
    }
    finally
    {
        uploads.Release();
    }
});

app.MapGet("/files/{id}", async (string id, HttpContext context, FileStorage storage, SemaphoreSlim transfers) =>
{
    // AES-GCM verifies the whole file in memory before any plaintext can be returned.
    // One upload/decryption at a time bounds peak memory for 100 MiB input.
    if (!await transfers.WaitAsync(0, context.RequestAborted))
    {
        context.Response.Headers.RetryAfter = "1";
        throw new ApiException(429, "transfer_busy", "Transferencias ocupadas. Tente novamente mais tarde.");
    }
    try
    {
        await storage.Send(id, context.Request.Query["key"], context);
    }
    finally { transfers.Release(); }
});
app.Run();

public partial class Program;

namespace ScreenshotApi
{
    public sealed record FileResponse(string Id, string Url, string ContentType)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTimeOffset ExpiresAt { get; init; }
    }
    public sealed class ApiException(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }
    internal static class ApiError
    {
        public static Task Write(HttpContext context, int status, string code, string message)
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new { error = new { code, message } }, context.RequestAborted);
        }
    }
}
