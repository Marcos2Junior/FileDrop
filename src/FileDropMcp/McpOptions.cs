using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDropMcp;

public sealed class McpOptions
{
    public string ApiEndpoint { get; set; } = "";
    public string[] Roots { get; set; } = [];
    public string StateDirectory { get; set; } = "";
    public const long MaxFileBytes = 100 * 1024 * 1024;
    public Uri Endpoint => new(ApiEndpoint);

    public static McpOptions Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new DeliveryException("configuration_invalid");
        PathGuard.NoLinks(path);
        if (new FileInfo(path).Length > 65536) throw new DeliveryException("configuration_invalid");
        var options = JsonSerializer.Deserialize<McpOptions>(File.ReadAllText(path), new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = true
        }) ?? throw new DeliveryException("configuration_invalid");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(ApiEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !endpoint.AbsolutePath.EndsWith("/files", StringComparison.Ordinal) || endpoint.AbsolutePath.Contains('%'))
            throw new DeliveryException("configuration_invalid");
        ApiEndpoint = endpoint.AbsoluteUri;
        if (Roots is not { Length: > 0 and <= 32 }) throw new DeliveryException("configuration_invalid");
        Roots = Roots.Select(PathGuard.LocalAbsolute).Distinct(PathGuard.Comparer).ToArray();
        foreach (var root in Roots)
        {
            PathGuard.NoLinks(root);
            if (!Directory.Exists(root)) throw new DeliveryException("configuration_invalid");
        }
        StateDirectory = PathGuard.LocalAbsolute(StateDirectory);
        PathGuard.NoLinks(StateDirectory);
    }
}

public sealed class DeliveryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    public static string Write(object value) => JsonSerializer.Serialize(value, Options);
}
