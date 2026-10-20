using FileDropMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    // No CLI credentials/config overrides; stdout belongs exclusively to MCP JSON-RPC.
    if (args.Length != 0) throw new DeliveryException("arguments_not_supported");
    var options = McpOptions.Load(Environment.GetEnvironmentVariable("FILEDROP_CONFIG"));
    var key = Environment.GetEnvironmentVariable("FILE_API_KEY"); // Read exactly once, never persisted.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
    builder.Logging.ClearProviders(); // SDK/tool arguments and bearer results must not become logs.
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<PolicyStore>();
    builder.Services.AddSingleton(_ => FilePublisher.CreateHttpClient());
    builder.Services.AddSingleton(services => new FilePublisher(options, services.GetRequiredService<PolicyStore>(),
        services.GetRequiredService<HttpClient>(), key));
    builder.Services.AddMcpServer(server =>
    {
        server.ServerInfo = new() { Name = "filedrop-delivery", Version = "1.0.0" };
        server.ServerInstructions = "For explicitly requested file/image deliveries, consult get_delivery_policy first. Native first; after ONE clear technical failure report it and use scoped FileDrop fallback for 24h and the active task. Never bypass permission denial, cancellation or pending confirmation. For uncertain delivery, verify before republishing; never switch channel or auto retry. This server does not observe native tools. No capture/scanning. Never persist/log bearer URLs, keys or tool results. Report the delivery outcome to the user.";
    }).WithStdioServerTransport().WithTools<DeliveryTools>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception)
{
    // No exception text/config/environment/tool data on either stream.
    Console.Error.WriteLine("FileDrop MCP could not start. Check local non-secret configuration and permissions.");
    return 1;
}
