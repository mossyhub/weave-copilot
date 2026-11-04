using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace WeaveCopilot.Sdk;

/// <summary>The real host: runs the Copilot CLI as a child process over stdio (never in-process) and adapts the SDK to the seam.</summary>
public sealed class SdkHost(string cliPath, bool hydraFusion, bool externalToolsOnly = false) : ICopilotHost
{
    public string CliPath { get; } = cliPath;
    private IReadOnlyDictionary<string, string> _fusionLevels = new Dictionary<string, string>();

    public static bool TierAcknowledged(string? model, string? current, string? pending, string requested) =>
        model == "auto" && (current == requested || pending == requested);

    public static IReadOnlyDictionary<string, string> FusionLevels(AutoTierMetadata? metadata) =>
        (metadata?.Tiers ?? []).Where(t => t.FusionTier is "standard" or "high" or "max"
            && t.Status is { Enabled: true, PickerVisible: true })
        .GroupBy(t => "fusion-" + t.FusionTier).ToDictionary(g => g.Key, g => g.First().Id);

    private readonly CopilotClient _client = new(new CopilotClientOptions
    {
        // COPILOT_HOME is also inherited by the child through the environment.
        BaseDirectory = Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } home ? home : null,
        Connection = RuntimeConnection.ForStdio(cliPath, hydraFusion ? ["--experimental"] : null),
    });

    public static string ResolveCli(string? explicitPath)
    {
        var candidate = explicitPath ?? Environment.GetEnvironmentVariable("WEAVE_COPILOT_CLI");
        if (!string.IsNullOrEmpty(candidate))
            return File.Exists(candidate) ? candidate : throw new FileNotFoundException($"Copilot CLI not found at '{candidate}'");
        if (FindOnPath(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("PATH"), File.Exists) is { } found) return found;
        throw new FileNotFoundException("Copilot CLI not found: pass --copilot-cli <path>, set WEAVE_COPILOT_CLI, or put `copilot` on PATH");
    }

    /// <summary>First `copilot` on PATH: on Windows copilot.exe, copilot.cmd or copilot.bat (npm installs a .cmd shim), per directory in PATH order.</summary>
    public static string? FindOnPath(bool windows, string? path, Func<string, bool> exists)
    {
        string[] names = windows ? ["copilot.exe", "copilot.cmd", "copilot.bat"] : ["copilot"];
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                if (Path.Combine(dir, name) is var full && exists(full)) return full;
        return null;
    }

    private readonly ExperimentalGuard? _guard = hydraFusion ? new ExperimentalGuard(ExperimentalGuard.DefaultHome()) : null;

    public Task StartAsync() { _guard?.Snapshot(); return _client.StartAsync(); }

    /// <summary>Puts the user's settings.json `experimental` value back; safe to call more than once.</summary>
    public void RestoreSettings() => _guard?.Restore();

    public async Task<string?> VersionAsync() { try { return (await _client.GetStatusAsync()).Version; } catch { return null; } }

    public async Task PingAsync() => await _client.GetStatusAsync();

    public async Task<IReadOnlyList<ModelEntry>> ListModelsAsync()
    {
        var models = await _client.ListModelsAsync();
        // The raw list carries the price tier and capability category; an older CLI lacks it, and listing never fails over it.
        var raw = new Dictionary<string, (string? Price, string? Category)>();
        try
        {
            var listing = await _client.Rpc.Models.ListAsync();
            if (hydraFusion) _fusionLevels = FusionLevels(listing.Auto);
            foreach (var m in listing.Models)
                raw[m.Id] = (m.ModelPickerPriceCategory?.Value, m.ModelPickerCategory?.Value);
        }
        catch { }
        if (hydraFusion && _fusionLevels.Count == 0)
        {
            // Some released CLIs expose V2 metadata only after a session opts into the feature.
            var probe = new SessionConfig { Model = "auto", AvailableTools = [], EnableConfigDiscovery = false,
                SkipCustomInstructions = true, OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("catalog probe")) };
            Gate(probe);
            await using var session = await _client.CreateSessionAsync(probe);
            _fusionLevels = FusionLevels((await session.Rpc.Model.ListAsync()).Auto);
            var probeId = session.SessionId;
            await session.DisposeAsync();
            try { await _client.DeleteSessionAsync(probeId); } catch { }
        }
        var result = models.Select(m =>
        {
            raw.TryGetValue(m.Id, out var r);
            return new ModelEntry(m.Id, m.Name ?? m.Id, m.Billing?.Multiplier is { } x ? $"{x:0.##}x" : null, r.Price, r.Category);
        }).ToList();
        foreach (var level in _fusionLevels.Keys)
            result.Add(new ModelEntry(level, "Copilot Fusion " + level[7..], null));
        return result;
    }

    public async Task<AuthStatus> AuthStatusAsync()
    {
        var s = await _client.GetAuthStatusAsync();
        return new AuthStatus(s.IsAuthenticated, s.AuthType, s.Host, s.Login, s.StatusMessage);
    }

    private Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> Wrap(PermissionCallback cb) =>
        (req, inv) => cb(inv.SessionId, req);

    public async Task<ICopilotSession> CreateAsync(string cwd, string? model, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null, IReadOnlyList<string>? skillDirectories = null)
    {
        var config = new SessionConfig { Model = model, WorkingDirectory = cwd, Streaming = true, OnPermissionRequest = Wrap(permission) };
        Gate(config);
        Discover(config, skillDirectories);
        config.McpServers = ToSdk(mcp);
        ApplyExternalToolBoundary(config, externalToolsOnly);
        return new SdkSession(await _client.CreateSessionAsync(config), _fusionLevels);
    }

    public async Task<ICopilotSession> ResumeAsync(string sessionId, string cwd, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null, IReadOnlyList<string>? skillDirectories = null)
    {
        var config = new ResumeSessionConfig { WorkingDirectory = cwd, Streaming = true, OnPermissionRequest = Wrap(permission) };
        Gate(config);
        Discover(config, skillDirectories);
        config.McpServers = ToSdk(mcp);
        ApplyExternalToolBoundary(config, externalToolsOnly);
        return new SdkSession(await _client.ResumeSessionAsync(sessionId, config), _fusionLevels);
    }

    /// <summary>Hermes owns tools, approvals and instructions; do not create a second ambient executor.</summary>
    public static void ApplyExternalToolBoundary(SessionConfigBase config, bool enabled)
    {
        if (!enabled) return;
        config.AvailableTools = [];
        config.EnableConfigDiscovery = false;
        config.SkipCustomInstructions = true;
        config.McpServers = null;
        config.SkillDirectories = [];
    }

    /// <summary>Without discovery a session doesn't see the folder's .github/agents and .github/skills.</summary>
    private static void Discover(SessionConfigBase config, IReadOnlyList<string>? skillDirectories)
    {
        config.EnableConfigDiscovery = true;
        config.SkillDirectories = skillDirectories is { Count: > 0 } ? [.. skillDirectories] : null;
    }

    public async Task<IReadOnlyList<AgentEntry>> ListAgentsAsync(string cwd) =>
        UserAgents((await _client.Rpc.Agents.DiscoverAsync(new AgentsDiscoverRequest { ProjectPaths = [cwd] })).Agents);

    /// <summary>The agents a user can pick: those not marked <c>userInvocable: false</c>.</summary>
    public static IReadOnlyList<AgentEntry> UserAgents(IEnumerable<AgentInfo> agents) =>
        [.. agents.Where(a => a.UserInvocable != false).Select(a => new AgentEntry(a.Name, a.DisplayName, a.Description, a.Source?.Value))];

    private static Dictionary<string, McpServerConfig>? ToSdk(IReadOnlyList<AcpMcpServer>? servers) =>
        servers is { Count: > 0 }
            ? servers.ToDictionary(s => s.Name, s => (McpServerConfig)new McpHttpServerConfig { Url = s.Url, Headers = s.Headers.ToDictionary(h => h.Key, h => h.Value) })
            : null;

    /// <summary>HydraFusion is opt-in: only with --hydrafusion do we turn on experimental mode and its single rollout flag.</summary>
    private void Gate(SessionConfigBase config)
    {
        if (!hydraFusion) return;
        config.EnableExperimentalMode = true;
        config.FeatureFlags = new Dictionary<string, bool> { ["HYDRAFUSION_ROLLOUT"] = true,
            ["DYNAMIC_AUTO_TIERS"] = true, ["HYDRAFUSION_PLAN_V2"] = true };
    }

    public async Task<string> ForkAsync(string sessionId, string? toEventId) => (await _client.Rpc.Sessions.ForkAsync(sessionId, toEventId, null)).SessionId;

    public async Task<bool> ProbeHydraFusionAsync(string cwd)
    {
        try
        {
            var config = new SessionConfig { Model = "hydrafusion", WorkingDirectory = cwd, OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("probe")) };
            Gate(config);
            await using var s = await _client.CreateSessionAsync(config);
            var id = s.SessionId;
            await s.DisposeAsync();
            try { await _client.DeleteSessionAsync(id); } catch { /* probe leftovers are harmless */ }
            return true;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync() { try { await _client.StopAsync(); } catch { } await _client.DisposeAsync(); RestoreSettings(); }

    private sealed class SdkSession(CopilotSession s, IReadOnlyDictionary<string, string> levels) : ICopilotSession
    {
        private string? _requestedTier;
        public string Id => s.SessionId;
        public IDisposable Subscribe(Action<SessionEvent> handler) => s.On(handler);
        public Task SendAsync(string prompt, bool immediate, IReadOnlyList<ImageData>? images = null, string? displayPrompt = null) =>
            s.SendAsync(new MessageOptions
            {
                Prompt = prompt,
                DisplayPrompt = displayPrompt,
                Mode = immediate ? "immediate" : null,
                Attachments = images is { Count: > 0 } ? [.. images.Select(i => (Attachment)new AttachmentBlob { Data = i.Base64, MimeType = i.MimeType, DisplayName = i.Name })] : null,
            });
        public async Task<IReadOnlyList<TodoRow>> ReadTodosAsync() =>
            (await s.Rpc.Plan.ReadSqlTodosAsync()).Rows.Select(r => new TodoRow(r.Title ?? r.Description ?? r.Id ?? "", r.Status ?? "pending")).ToList();
        public async Task<CompactResult> CompactAsync(string? instructions)
        {
            var r = await s.Rpc.History.CompactAsync(new SessionHistoryCompactRequest { Trigger = SessionHistoryCompactRequestTrigger.Manual, CustomInstructions = instructions });
            return new CompactResult(r.Success, r.TokensRemoved, r.MessagesRemoved);
        }
        public async Task<IReadOnlyList<CommandEntry>> ListCommandsAsync() =>
            (await s.Rpc.Commands.ListAsync(new SessionCommandsListRequest { IncludeBuiltins = true, IncludeSkills = true, IncludeClientCommands = true })).Commands
                .Select(c => new CommandEntry(c.Name, c.Description, c.Input?.Hint, c.Kind.Value)).ToList();
        public async Task<CommandResult> InvokeCommandAsync(string name, string? input) => await s.Rpc.Commands.InvokeAsync(name, input) switch
        {
            SlashCommandInvocationResultAgentPrompt p => new CommandResult("prompt", p.Prompt),
            SlashCommandInvocationResultText t => new CommandResult("text", t.Text),
            SlashCommandInvocationResultCompleted c => new CommandResult("text", c.Message),
            _ => new CommandResult("unsupported", null),
        };
        public Task SelectAgentAsync(string? name) => string.IsNullOrEmpty(name) ? s.Rpc.Agent.DeselectAsync() : s.Rpc.Agent.SelectAsync(name);
        public Task AbortAsync() => s.AbortAsync();
        public async Task SetModelAsync(string modelId)
        {
            if (!levels.TryGetValue(modelId, out var tier))
            {
                if (modelId.StartsWith("fusion-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Requested Fusion tier is not enabled by this account.");
                await s.SetModelAsync(modelId);
                _requestedTier = null;
                return;
            }
            await s.SetModelAsync("auto");
            var switched = await s.SetAutoTierAsync(new AutoTier(tier));
            var current = await s.Rpc.Model.GetCurrentAsync();
            if (!TierAcknowledged(current.ModelId, current.AutoTier?.Value, current.PendingAutoTier?.Value, tier))
                throw new InvalidOperationException($"Copilot tier acknowledgement differs: requested={tier}, model={current.ModelId}, current={current.AutoTier?.Value}, pending={current.PendingAutoTier?.Value}, effective={switched.EffectiveAutoTier?.Value}, status={switched.Status.Value}");
            _requestedTier = tier;
        }
        public async Task VerifyModelSelectionAsync()
        {
            if (_requestedTier is null) return;
            var current = await s.Rpc.Model.GetCurrentAsync();
            if (current.ModelId != "auto" || current.AutoTier?.Value != _requestedTier)
                throw new InvalidOperationException("Copilot did not commit the requested Fusion tier for this turn.");
        }
        public async Task<IReadOnlyList<SessionEvent>> GetEventsAsync() => await s.GetEventsAsync();
        public async Task<string?> AskAsync(string question) => (await s.Rpc.Ui.EphemeralQueryAsync(question)).Answer;
        public async Task<ShellResult> RunShellAsync(string requestId, string command)
        {
            var r = await s.Rpc.Shell.ExecuteUserRequestedAsync(requestId, command);
            return new ShellResult(r.ToolCallId, r.Success, r.ExitCode, r.Output, r.Error);
        }
        public async Task<bool> CancelTaskAsync(string id) => (await s.Rpc.Tasks.CancelAsync(id)).Cancelled;
        public async Task<TaskOutput> ReadTaskOutputAsync(string id, string? cursor)
        {
            var r = await s.Rpc.Tasks.ReadOutputAsync(id, cursor, null);
            return new TaskOutput(r.Text ?? "", r.Cursor, r.Exit is not null, r.Exit?.ExitCode is { } code ? (int)code : null);
        }
        public ValueTask DisposeAsync() => s.DisposeAsync();
    }
}
