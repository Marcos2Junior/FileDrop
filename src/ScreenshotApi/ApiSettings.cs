namespace ScreenshotApi;

public sealed class ApiSettings
{
    public string ApiKey { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public bool AllowInsecureHttp { get; set; }
    public string StorageDirectory { get; set; } = "";
    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;
    public long MaxRequestBytes => MaxUploadBytes + 64 * 1024;

    public void Validate(IHostEnvironment environment, string? webRoot)
    {
        // No development/test bypass and no built-in credential.
        if (ApiKey.Length is < 32 or > 256 || ApiKey.Any(c => c < '!' || c > '~') ||
            new[] { "changeme", "replace", "example", "placeholder", "sua-chave" }
                .Any(word => ApiKey.Contains(word, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Configure Files:ApiKey externamente: 32 a 256 caracteres ASCII sem espacos; nao use um placeholder.");

        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && (uri.IsLoopback || AllowInsecureHttp))) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Files:PublicBaseUrl deve ser uma URL HTTPS absoluta sem credenciais, query ou fragmento. HTTP externo exige Files:AllowInsecureHttp=true.");
        PublicBaseUrl = uri.AbsoluteUri.TrimEnd('/');

        if (MaxUploadBytes is < 1 or > 104857600)
            throw new InvalidOperationException("Files:MaxUploadBytes deve estar entre 1 e 104857600 bytes.");

        if (string.IsNullOrWhiteSpace(StorageDirectory) || !Path.IsPathFullyQualified(StorageDirectory))
            throw new InvalidOperationException("Files:StorageDirectory deve ser um diretorio absoluto externo a aplicacao.");

        StorageDirectory = Path.GetFullPath(StorageDirectory);
        foreach (var forbidden in new[] { environment.ContentRootPath, AppContext.BaseDirectory, webRoot })
        {
            if (!string.IsNullOrWhiteSpace(forbidden) && IsWithin(StorageDirectory, forbidden))
                throw new InvalidOperationException("Files:StorageDirectory deve ficar fora do content root, webroot e pasta de publicacao.");
        }

        // Reject existing junctions/symlinks, including ancestors, to avoid redirecting storage.
        for (var directory = new DirectoryInfo(StorageDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Files:StorageDirectory nao pode usar links simbolicos ou junctions.");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(normalized, comparison) ||
            path.StartsWith(Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar, comparison);
    }
}
