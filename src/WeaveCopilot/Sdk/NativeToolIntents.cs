#pragma warning disable GHCP001 // SDK native tool invocation metadata is experimental.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace WeaveCopilot.Sdk;

public sealed record NativeToolIntent(string Id, string Name, JsonElement Arguments)
{
    public JsonObject ToJson() => new() { ["id"] = Id, ["name"] = Name, ["arguments"] = JsonNode.Parse(Arguments.GetRawText()) };
}

/// <summary>Bounded in-memory intent recorder. This class has no action executor or ambient I/O.</summary>
public sealed class NativeToolIntents
{
    public const int MaxIntents = 32;
    public const int MaxIntentBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly List<NativeToolIntent> _calls = [];
    private bool _active;
    private bool _cancelled;
    private string? _failure;
    private long _bytes;
    public IReadOnlyList<AIFunctionDeclaration> Functions { get; }

    public NativeToolIntents(IReadOnlyList<JsonElement> schemas)
    {
        var tools = new List<AIFunctionDeclaration>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in HermesToolSchemas.Validate(schemas))
        {
            var f = schema.GetProperty("function");
            var name = f.GetProperty("name").GetString()!;
            _names.Add(name);
            // Short aliases cannot collide with hashed aliases: the latter alone have length 64.
            var alias = "hermes__" + (name.Length <= 55 ? name : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..56]);
            if (!aliases.Add(alias)) throw new ArgumentException("Conflicting Hermes tool aliases");
            Task<object> RecordOnly(ToolInvocation invocation, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                // Acknowledges ONLY capture/rejection, never the requested operation.
                return Task.FromResult<object>(Acknowledge(invocation.ToolCallId, name, invocation.Arguments));
            }
            var inner = CopilotTool.DefineTool((Func<ToolInvocation, CancellationToken, Task<object>>)RecordOnly,
                new CopilotToolOptions { SkipPermission = true, IsTerminal = true },
                new AIFunctionFactoryOptions { Name = alias, Description = f.TryGetProperty("description", out var d) ? d.GetString() : "Record intent for outer Hermes; does not execute." });
            tools.Add(new DynamicSchemaFunction(inner, f.GetProperty("parameters").Clone()));
        }
        Functions = tools.AsReadOnly();
    }

    private static JsonElement Acknowledgement
    {
        get
        {
            using var d = JsonDocument.Parse("""{"status":"intent_recorded_only","executed":false,"execution_deferred_to":"outer_hermes"}""");
            return d.RootElement.Clone();
        }
    }

    /// <summary>Even rejected callbacks terminate the SDK tool phase; Snapshot still fails the whole ACP prompt.</summary>
    public JsonElement Acknowledge(string id, string name, JsonElement? arguments)
    {
        try { Record(id, name, arguments); return Acknowledgement; }
        catch (InvalidOperationException)
        {
            using var d = JsonDocument.Parse("""{"status":"intent_rejected_only","executed":false,"execution_deferred_to":"outer_hermes"}""");
            return d.RootElement.Clone();
        }
    }

    public void BeginPrompt()
    {
        lock (_gate) { _calls.Clear(); _bytes = 2; _failure = null; _cancelled = false; _active = true; }
    }

    public void CancelPrompt()
    {
        lock (_gate) { _cancelled = true; _active = false; _calls.Clear(); _bytes = 2; }
    }

    public void Record(string id, string name, JsonElement? arguments)
    {
        lock (_gate)
        {
            if (!_active || _cancelled) throw new InvalidOperationException("No active Hermes intent prompt");
            if (_failure is not null) throw new InvalidOperationException(_failure);
            void Fail(string message) { _failure = message; _calls.Clear(); throw new InvalidOperationException(message); }
            if (string.IsNullOrWhiteSpace(id) || !_names.Contains(name) || arguments is not { ValueKind: JsonValueKind.Object })
                Fail("Invalid native Hermes tool intent");
            var args = arguments!.Value;
            var prior = _calls.FirstOrDefault(c => c.Id == id);
            if (prior is not null)
            {
                if (prior.Name != name || !JsonElement.DeepEquals(prior.Arguments, args)) Fail("Conflicting native tool call ID");
                return;
            }
            // Charge the complete wire envelope, including escaping, IDs, names, commas and brackets.
            var call = new NativeToolIntent(id, name, args);
            var bytes = Encoding.UTF8.GetByteCount(call.ToJson().ToJsonString()) + (_calls.Count > 0 ? 1 : 0);
            if (_calls.Count >= MaxIntents || _bytes + bytes > MaxIntentBytes) Fail("Native Hermes tool intent limit exceeded");
            _bytes += bytes;
            _calls.Add(call with { Arguments = args.Clone() });
        }
    }

    public IReadOnlyList<NativeToolIntent> Snapshot()
    {
        lock (_gate)
        {
            if (_cancelled) return [];
            if (_failure is not null) throw new InvalidOperationException(_failure);
            return _calls.ToArray();
        }
    }

    private sealed class DynamicSchemaFunction(AIFunction inner, JsonElement schema) : AIFunction
    {
        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override JsonElement JsonSchema => schema;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => inner.InvokeAsync(arguments, cancellationToken);
    }
}
