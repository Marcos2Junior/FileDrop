using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace ScreenshotApi;

public sealed class FileStorage(ApiSettings settings, TimeProvider clock, ILogger<FileStorage> logger)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly byte[] Magic = [(byte)'F', (byte)'D', (byte)'A', (byte)'P', 1];
    private const int HeaderLength = 41; // magic/version 5, upload UTC 8, nonce 12, GCM tag 16
    private const int MetadataLimit = 2048;
    private const int MaxEnvelopeBytes = 104857600 + HeaderLength + 4 + MetadataLimit;

    public async Task<FileResponse> Save(MemoryStream bytes, string originalName, CancellationToken cancellationToken)
    {
        var type = ContentTypes.Detect(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new Metadata(SanitizeName(originalName), type));
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var key = RandomNumberGenerator.GetBytes(32); // Independent from storage ID; never persisted.
        var payload = new byte[checked(4 + metadata.Length + (int)bytes.Length)];
        var envelope = new byte[payload.Length + HeaderLength];
        var temporaryPath = Path.Combine(settings.StorageDirectory, $".{id}.tmp");
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload, metadata.Length);
            metadata.CopyTo(payload, 4);
            bytes.Position = 0;
            await bytes.ReadExactlyAsync(payload.AsMemory(4 + metadata.Length), cancellationToken);
            Magic.CopyTo(envelope, 0);
            var uploadedMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
            BinaryPrimitives.WriteInt64LittleEndian(envelope.AsSpan(5, 8), uploadedMilliseconds);
            RandomNumberGenerator.Fill(envelope.AsSpan(13, 12));
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(envelope.AsSpan(13, 12), payload, envelope.AsSpan(HeaderLength),
                    envelope.AsSpan(25, 16), AssociatedData(envelope, id));
            }
            // Multipart, metadata and original bytes stay in memory; disk only sees this envelope.
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await output.WriteAsync(envelope, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, Path.Combine(settings.StorageDirectory, id + ".bin"), overwrite: false);
            return new FileResponse(id, $"{settings.PublicBaseUrl}/files/{id}?key={WebEncoders.Base64UrlEncode(key)}", type)
            {
                ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(uploadedMilliseconds) + Retention
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task Send(string id, StringValues secrets, HttpContext context)
    {
        if (!ValidId(id) || secrets.Count != 1 || secrets[0] is not { Length: 43 } secret ||
            secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw NotFound();
        var key = WebEncoders.Base64UrlDecode(secret);
        byte[]? plaintext = null;
        try
        {
            if (key.Length != 32 || WebEncoders.Base64UrlEncode(key) != secret) throw NotFound();
            var path = Path.Combine(settings.StorageDirectory, id + ".bin");
            byte[] envelope;
            try
            {
                if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw NotFound();
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                if (file.Length <= HeaderLength + 4 || file.Length > MaxEnvelopeBytes) throw NotFound();
                envelope = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(envelope, context.RequestAborted);
            }
            catch (FileNotFoundException) { throw NotFound(); }
            catch (DirectoryNotFoundException) { throw NotFound(); }
            catch (EndOfStreamException) { throw NotFound(); }
            if (!TryReadTimestamp(envelope, out var uploaded) || Expired(uploaded)) throw NotFound();
            plaintext = new byte[envelope.Length - HeaderLength];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(envelope.AsSpan(13, 12), envelope.AsSpan(HeaderLength),
                    envelope.AsSpan(25, 16), plaintext, AssociatedData(envelope, id));
            }
            catch (CryptographicException) { throw NotFound(); }
            var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
            if (metadataLength is < 1 or > MetadataLimit || metadataLength >= plaintext.Length - 4) throw NotFound();
            Metadata? metadata;
            try { metadata = JsonSerializer.Deserialize<Metadata>(plaintext.AsSpan(4, metadataLength)); }
            catch (JsonException) { throw NotFound(); }
            if (metadata is null || string.IsNullOrEmpty(metadata.Name) || metadata.Name != SanitizeName(metadata.Name)) throw NotFound();
            var content = plaintext.AsMemory(4 + metadataLength);
            var type = ContentTypes.Detect(content.Span);
            if (type != metadata.ContentType) throw NotFound();
            var disposition = new ContentDispositionHeaderValue(type == "application/octet-stream" ? "attachment" : "inline");
            disposition.SetHttpFileName(metadata.Name);
            context.Response.Headers.ContentDisposition = disposition.ToString();
            context.Response.ContentType = type;
            context.Response.ContentLength = content.Length;
            // No bytes are sent before successful GCM authentication. No Range support.
            await context.Response.Body.WriteAsync(content, context.RequestAborted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public int SweepExpired(CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        try
        {
            // No recursion and no following junctions/symlinks, even if storage changed after startup.
            for (var directory = new DirectoryInfo(settings.StorageDirectory); directory is not null; directory = directory.Parent)
                if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return 0;
            Span<byte> header = stackalloc byte[HeaderLength];
            foreach (var path in Directory.EnumerateFiles(settings.StorageDirectory, "*.bin", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ValidId(Path.GetFileNameWithoutExtension(path)) || Path.GetExtension(path) != ".bin") continue;
                try
                {
                    if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                    var expired = false;
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (file.Length <= HeaderLength + 4 || file.Length > MaxEnvelopeBytes) continue;
                        file.ReadExactly(header);
                        expired = TryReadTimestamp(header, out var uploaded) && Expired(uploaded);
                    }
                    if (expired) { File.Delete(path); deleted++; }
                }
                catch (IOException) { LogSweepFailure(); }
                catch (UnauthorizedAccessException) { LogSweepFailure(); }
            }
        }
        catch (IOException) { LogSweepFailure(); }
        catch (UnauthorizedAccessException) { LogSweepFailure(); }
        return deleted;
    }

    private bool Expired(DateTimeOffset uploaded) => clock.GetUtcNow() >= uploaded + Retention;
    private void LogSweepFailure() => logger.LogWarning("Falha isolada na limpeza de arquivos. A proxima varredura tentara novamente.");
    private static bool TryReadTimestamp(ReadOnlySpan<byte> data, out DateTimeOffset uploaded)
    {
        uploaded = default;
        if (data.Length < HeaderLength || !data[..5].SequenceEqual(Magic)) return false;
        try { uploaded = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(data.Slice(5, 8))); }
        catch (ArgumentOutOfRangeException) { return false; }
        return uploaded >= DateTimeOffset.UnixEpoch && uploaded <= DateTimeOffset.MaxValue - Retention;
    }
    private static byte[] AssociatedData(ReadOnlySpan<byte> envelope, string id)
    {
        var data = new byte[13 + 32];
        envelope[..13].CopyTo(data);
        Convert.FromHexString(id).CopyTo(data, 13);
        return data;
    }
    private static bool ValidId(string id) => id.Length == 64 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static ApiException NotFound() => new(404, "not_found", "Arquivo nao encontrado.");
    private static string SanitizeName(string name)
    {
        var basename = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var safe = new string(basename.Where(c => char.IsLetterOrDigit(c) || " ._-()".Contains(c)).Take(120).ToArray()).Trim(' ', '.');
        return string.IsNullOrEmpty(safe) ? "download.bin" : safe;
    }
    private sealed record Metadata(string Name, string ContentType);
}
