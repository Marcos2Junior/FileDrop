using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FileDropMcp;

[JsonConverter(typeof(JsonStringEnumConverter<DeliveryOutcome>))]
public enum DeliveryOutcome
{
    [JsonStringEnumMemberName("technical_failure")] TechnicalFailure,
    [JsonStringEnumMemberName("delivered")] Delivered,
    [JsonStringEnumMemberName("denied")] Denied,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("pending_confirmation")] PendingConfirmation,
    [JsonStringEnumMemberName("uncertain")] Uncertain,
    [JsonStringEnumMemberName("verified_not_delivered")] VerifiedNotDelivered
}
public sealed record PolicyDecision(string Action, bool MayTryNative, bool MayPublish, DateTimeOffset? FallbackUntil, string Code);

public sealed class PolicyStore(McpOptions options, TimeProvider clock)
{
    private const int MaxStateBytes = 1024 * 1024;
    private readonly string directory = options.StateDirectory;
    private static readonly Regex Identifier = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public Task<PolicyDecision> Get(string channel, string capability, string taskId, CancellationToken ct = default)
        => Change(channel, capability, taskId, (scope, task, taskHash) =>
        {
            var stop = Blocked(scope, task);
            if (stop is not null) return stop;
            if (task.Phase is "native_in_flight" or "publishing")
                return Decision(scope, "verify_result", false, false, "result_pending");
            if (task.Pinned || scope.FallbackUntil > clock.GetUtcNow())
            {
                task.Pinned = true;
                return Decision(scope, "filedrop", false, true, "fallback_active");
            }
            if (scope.ProbeOwner != taskHash && scope.ProbeUntil > clock.GetUtcNow())
                return Decision(scope, "wait", false, false, "native_probe_in_progress");
            scope.ProbeOwner = taskHash;
            scope.ProbeUntil = clock.GetUtcNow().AddMinutes(5);
            task.Phase = "native_in_flight";
            task.Transport = "native";
            return Decision(scope, "native", true, false, "one_native_attempt");
        }, ct);

    public Task<PolicyDecision> Record(string channel, string capability, string taskId, DeliveryOutcome outcome, CancellationToken ct = default)
        => Change(channel, capability, taskId, (scope, task, taskHash) =>
        {
            if (!Enum.IsDefined(outcome)) throw new DeliveryException("invalid_outcome");
            if (!task.Active) return Decision(scope, "stop", false, false, "task_closed");
            if (task.Block is "denied" or "cancelled") return Blocked(scope, task)!;
            if (outcome == DeliveryOutcome.Delivered)
            {
                task.Block = null;
                task.Phase = "ready";
                ReleaseProbe(scope, taskHash);
                return Decision(scope, "delivered", false, false, "delivery_confirmed");
            }
            if (task.Block is "denied" or "cancelled" or "pending_confirmation")
                return Blocked(scope, task)!;
            if (outcome == DeliveryOutcome.TechnicalFailure)
            {
                if (task.Block == "uncertain") return Blocked(scope, task)!;
                if (task.Phase != "native_in_flight" || task.Transport != "native")
                    return Decision(scope, "stop", false, false, "no_native_attempt");
                Activate(scope, task);
            }
            else if (outcome == DeliveryOutcome.VerifiedNotDelivered)
            {
                if (task.Block != "uncertain" && task.Phase != "publishing")
                    return Decision(scope, "stop", false, false, "verification_not_required");
                if (task.Transport == "native") Activate(scope, task);
                else { task.Block = null; task.Phase = "ready"; task.Pinned = true; }
            }
            else
            {
                task.Block = outcome switch
                {
                    DeliveryOutcome.Denied => "denied",
                    DeliveryOutcome.Cancelled => "cancelled",
                    DeliveryOutcome.PendingConfirmation => "pending_confirmation",
                    _ => "uncertain"
                };
            }
            ReleaseProbe(scope, taskHash);
            return Blocked(scope, task) ?? Decision(scope, "filedrop", false, true, "fallback_active");
        }, ct);

    public Task<PolicyDecision> BeginPublish(string channel, string capability, string taskId, CancellationToken ct = default)
        => Change(channel, capability, taskId, (scope, task, _) =>
        {
            var stop = Blocked(scope, task);
            if (stop is not null) return stop;
            if (task.Phase is "publishing" or "native_in_flight") return Decision(scope, "verify_result", false, false, "result_pending");
            if (!task.Pinned && !(scope.FallbackUntil > clock.GetUtcNow()))
                return Decision(scope, "stop", false, false, "native_first");
            task.Pinned = true;
            task.Transport = "filedrop";
            task.Phase = "publishing"; // Persist before HTTP; process loss requires verification.
            return Decision(scope, "filedrop", false, true, "publish_reserved");
        }, ct);

    public Task<PolicyDecision> End(string channel, string capability, string taskId, CancellationToken ct = default)
        => Change(channel, capability, taskId, (scope, task, taskHash) =>
        {
            if (task.Phase is "native_in_flight" or "publishing") task.Block = "uncertain";
            task.Active = false;
            task.Pinned = false;
            ReleaseProbe(scope, taskHash);
            return Decision(scope, "stop", false, false, "task_closed");
        }, ct);

    public Task<PolicyDecision> FinishPublish(string channel, string capability, string taskId, string disposition, CancellationToken ct = default)
        => Change(channel, capability, taskId, (scope, task, _) =>
        {
            task.Phase = "ready";
            task.Block = disposition switch
            {
                "uncertain" => "uncertain",
                "denied" => "denied",
                "cancelled" => "cancelled",
                _ => null
            };
            return Blocked(scope, task) ?? Decision(scope, "filedrop", false, true, disposition);
        }, ct);

    private void Activate(Scope scope, TaskState task)
    {
        scope.FallbackUntil = clock.GetUtcNow().AddHours(24);
        task.Pinned = true;
        task.Block = null;
        task.Phase = "ready";
    }
    private static void ReleaseProbe(Scope scope, string taskHash)
    {
        if (scope.ProbeOwner == taskHash) { scope.ProbeOwner = null; scope.ProbeUntil = null; }
    }
    private static PolicyDecision Decision(Scope scope, string action, bool native, bool publish, string code)
        => new(action, native, publish, scope.FallbackUntil, code);
    private static PolicyDecision? Blocked(Scope scope, TaskState task)
    {
        if (!task.Active) return Decision(scope, "stop", false, false, "task_closed");
        return task.Block switch
        {
            "denied" => Decision(scope, "stop", false, false, "permission_denied"),
            "cancelled" => Decision(scope, "stop", false, false, "cancelled"),
            "pending_confirmation" => Decision(scope, "wait", false, false, "confirmation_pending"),
            "uncertain" => Decision(scope, "verify_result", false, false, "delivery_uncertain"),
            _ => null
        };
    }

    private async Task<PolicyDecision> Change(string channel, string capability, string taskId,
        Func<Scope, TaskState, string, PolicyDecision> action, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(capability) || string.IsNullOrEmpty(taskId) ||
            !Identifier.IsMatch(channel) || !Identifier.IsMatch(capability) || !Identifier.IsMatch(taskId))
            throw new DeliveryException("invalid_scope");
        PathGuard.NoLinks(directory);
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, "policy.lock");
        var statePath = Path.Combine(directory, "policy.json");
        PathGuard.NoLinks(lockPath);
        PathGuard.NoLinks(statePath);
        using var gate = await Lock(lockPath, ct);
        var state = new State();
        if (File.Exists(statePath))
        {
            if (new FileInfo(statePath).Length > MaxStateBytes) throw new DeliveryException("state_invalid");
            try { state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(statePath, ct)) ?? throw new DeliveryException("state_invalid"); }
            catch (JsonException) { throw new DeliveryException("state_invalid"); }
            if (state.Version != 1 || state.Scopes is null) throw new DeliveryException("state_invalid");
        }
        var scopeHash = Hash(channel.ToLowerInvariant() + "\n" + capability.ToLowerInvariant());
        var taskHash = Hash(taskId);
        if (!state.Scopes.TryGetValue(scopeHash, out var scope)) state.Scopes[scopeHash] = scope = new();
        if (scope.Tasks is null) throw new DeliveryException("state_invalid");
        foreach (var expired in scope.Tasks.Where(x => !x.Value.Active && x.Value.Block is null &&
                     x.Value.LastUpdated < clock.GetUtcNow().AddHours(-24)).Select(x => x.Key).ToArray())
            scope.Tasks.Remove(expired);
        if (!scope.Tasks.TryGetValue(taskHash, out var task)) scope.Tasks[taskHash] = task = new();
        var result = action(scope, task, taskHash);
        task.LastUpdated = clock.GetUtcNow();
        var json = JsonSerializer.SerializeToUtf8Bytes(state);
        if (json.Length > MaxStateBytes) throw new DeliveryException("state_full");
        var temporary = Path.Combine(directory, "policy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, json, ct);
            File.Move(temporary, statePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }
    private static async Task<FileStream> Lock(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33 && attempt < 80)
            { await Task.Delay(25, ct); }
        }
    }
    public sealed class State
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Scope> Scopes { get; set; } = [];
    }
    public sealed class Scope
    {
        public DateTimeOffset? FallbackUntil { get; set; }
        public string? ProbeOwner { get; set; }
        public DateTimeOffset? ProbeUntil { get; set; }
        public Dictionary<string, TaskState> Tasks { get; set; } = [];
    }
    public sealed class TaskState
    {
        public bool Active { get; set; } = true;
        public bool Pinned { get; set; }
        public string Phase { get; set; } = "ready";
        public string Transport { get; set; } = "native";
        public string? Block { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
    }
}
