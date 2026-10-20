using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ScreenshotApi;

internal static class UploadReader
{
    public static async Task<UploadData> Read(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(415, "multipart_required", "Envie multipart/form-data com um unico campo de arquivo file.");
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 70)
            throw new ApiException(400, "invalid_multipart", "Boundary multipart ausente ou invalido.");

        var data = new MemoryStream();
        try
        {
            var reader = new MultipartReader(boundary, request.Body)
            {
                HeadersCountLimit = 8,
                HeadersLengthLimit = 8192,
                BodyLengthLimit = limit + 65536
            };
            var section = await reader.ReadNextSectionAsync(cancellationToken);
            if (section is null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
                !HeaderUtilities.RemoveQuotes(disposition.Name).Equals("file", StringComparison.Ordinal) ||
                (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
                throw new ApiException(400, "file_required", "Envie exatamente um arquivo no campo file, sem outros campos.");

            var buffer = new byte[65536];
            int count;
            while ((count = await section.Body.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (data.Length + count > limit)
                    throw new ApiException(413, "upload_too_large", "Arquivo excede o limite permitido.");
                await data.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (data.Length == 0)
                throw new ApiException(400, "empty_file", "O arquivo esta vazio.");
            if (await reader.ReadNextSectionAsync(cancellationToken) is not null)
                throw new ApiException(400, "file_required", "Envie exatamente um arquivo no campo file, sem outros campos.");
            data.Position = 0;
            var originalName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "download.bin";
            return new UploadData(data, originalName);
        }
        catch (InvalidDataException)
        {
            data.Dispose();
            throw new ApiException(400, "invalid_multipart", "Corpo multipart invalido ou incompleto.");
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            data.Dispose();
            throw new ApiException(400, "invalid_multipart", "Corpo multipart invalido ou incompleto.");
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }
}

internal sealed record UploadData(MemoryStream Bytes, string FileName) : IDisposable
{
    public void Dispose() => Bytes.Dispose();
}
