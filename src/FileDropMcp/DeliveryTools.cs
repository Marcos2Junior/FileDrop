using System.ComponentModel;
using ModelContextProtocol.Server;

namespace FileDropMcp;

[McpServerToolType]
public sealed class DeliveryTools(PolicyStore policy, FilePublisher publisher)
{
    [McpServerTool(Name = "get_delivery_policy", Destructive = false, OpenWorld = false), Description("Before a requested file/image delivery, consult scoped policy. May reserve ONE native attempt. Report its result before another attempt. FileDrop is fallback only after a clear technical native failure; never bypass denied, cancelled, pending or uncertain delivery.")]
    public Task<string> GetPolicy(
        [Description("Symbolic delivery channel, e.g. chat. Never a URL or secret.")] string channel,
        [Description("Symbolic capability, e.g. image-delivery or document-delivery.")] string capability,
        [Description("Stable opaque ID for this active task, e.g. thread UUID. Do not change it to escape a blocked result.")] string task_id,
        CancellationToken cancellationToken) => Safe(async () => await policy.Get(channel, capability, task_id, cancellationToken));

    [McpServerTool(Name = "record_delivery_outcome", Destructive = false, OpenWorld = false), Description("Report an observed outcome; this server cannot see native tools. technical_failure activates fallback for 24h and this active task. denied/cancelled/pending_confirmation never activate fallback. uncertain requires verification; use verified_not_delivered only after confirmed non-delivery, or delivered after confirmed delivery.")]
    public Task<string> Record(string channel, string capability, string task_id, DeliveryOutcome outcome, CancellationToken cancellationToken)
        => Safe(async () => await policy.Record(channel, capability, task_id, outcome, cancellationToken));

    [McpServerTool(Name = "publish_file", Destructive = false), Description("Publish ONE explicitly requested file under authorized roots, only when scoped fallback permits. The POST credential is read once at startup from FILE_API_KEY, never a tool argument. No capture, scanning, auto retry, or redirect. Returns a secret bearer URL only to the consumer; never save/log it.")]
    public Task<string> Publish([Description("Absolute path to an existing file under a configured authorized root.")] string path,
        string channel, string capability, string task_id, CancellationToken cancellationToken)
        => Safe(async () => await publisher.Publish(path, channel, capability, task_id, cancellationToken));

    [McpServerTool(Name = "end_delivery_task", Destructive = false, OpenWorld = false), Description("Call only when this task actually ends. Releases its pinned fallback choice; does not clear a denied/pending/uncertain result or authorize re-delivery. Use a new ID only for a genuinely new task.")]
    public Task<string> End(string channel, string capability, string task_id, CancellationToken cancellationToken)
        => Safe(async () => await policy.End(channel, capability, task_id, cancellationToken));

    private static async Task<string> Safe(Func<Task<object>> action)
    {
        try { return JsonOutput.Write(await action()); }
        catch (DeliveryException exception) { return JsonOutput.Write(new { success = false, code = exception.Code, action = "stop" }); }
        catch (UnauthorizedAccessException) { return JsonOutput.Write(new { success = false, code = "permission_denied", action = "stop" }); }
        catch (OperationCanceledException) { return JsonOutput.Write(new { success = false, code = "cancelled_or_state_unavailable", action = "verify_result" }); }
        catch (Exception) { return JsonOutput.Write(new { success = false, code = "state_unavailable", action = "stop" }); }
    }
}
