using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class NativeIntentTests
{
    private static JsonObject NewParams(string tools) => (JsonObject)JsonNode.Parse("{\"cwd\":\"/test\",\"_meta\":{\"hermesToolSchemas\":" + tools + "}}")!;
    private const string Tool = """[{"type":"function","function":{"name":"terminal","description":"Run a command","parameters":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}}]""";

    [Fact]
    public async Task Session_new_forwards_original_native_schemas()
    {
        var wire = new Wire();
        var host = new FakeHost { SupportsHermesToolIntents = true };
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/new", NewParams(Tool));
        Assert.NotNull(host.Schemas);
        Assert.Equal(JsonNode.Parse(Tool)![0]!.ToJsonString(), JsonNode.Parse(host.Schemas![0].GetRawText())!.ToJsonString());
        await wire.CloseAsync();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"type\":\"web_search\"}]")]
    [InlineData("[{\"type\":\"function\",\"function\":{\"name\":\"bad name\",\"parameters\":{\"type\":\"object\"}}}]")]
    [InlineData("[{\"type\":\"function\",\"function\":{\"name\":\"x\",\"parameters\":{\"type\":\"array\"}}}]")]
    public async Task Malformed_schema_fails_before_session_creation(string tools)
    {
        var wire = new Wire();
        var host = new FakeHost { SupportsHermesToolIntents = true };
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        var result = await wire.CallAsync("session/new", NewParams(tools));
        Assert.Equal(-32602, result["error"]?["code"]?.GetValue<int>());
        Assert.Equal(0, host.Creates);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Schema_count_duplicates_and_byte_caps_fail_closed()
    {
        var original = JsonNode.Parse(Tool)![0]!;
        var cases = new List<JsonArray> { new(original.DeepClone(), original.DeepClone()) };
        var many = new JsonArray();
        for (var i = 0; i < 257; i++) { var t = original.DeepClone(); t["function"]!["name"] = "tool" + i; many.Add(t); }
        cases.Add(many);
        var large = original.DeepClone(); large["function"]!["description"] = new string('x', 2 * 1024 * 1024);
        cases.Add(new JsonArray(large));
        foreach (var tools in cases)
        {
            var wire = new Wire();
            var host = new FakeHost { SupportsHermesToolIntents = true };
            new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
            var result = await wire.CallAsync("session/new", NewParams(tools.ToJsonString()));
            Assert.Equal(-32602, result["error"]?["code"]?.GetValue<int>());
            Assert.Equal(0, host.Creates);
            await wire.CloseAsync();
        }
    }

    [Fact]
    public async Task Unsupported_host_rejects_even_empty_schemas()
    {
        var wire = new Wire(); var host = new FakeHost();
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        var init = await wire.ResultAsync("initialize", new());
        Assert.Null(init["_meta"]?["hermesToolIntentVersion"]);
        Assert.NotNull((await wire.CallAsync("session/new", NewParams("[]")))["error"]);
        Assert.Equal(0, host.Creates);
        await wire.CloseAsync();
    }

    private static JsonObject Prompt() => (JsonObject)JsonNode.Parse("""{"sessionId":"s-1","prompt":[{"type":"text","text":"record intent"}]}""")!;
    private static System.Text.Json.JsonElement Args(string value = "never execute") { using var d = System.Text.Json.JsonDocument.Parse("{\"command\":\"" + value + "\"}"); return d.RootElement.Clone(); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_acknowledges_accepted_schemas_including_empty(bool empty)
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        var result = await wire.ResultAsync("session/new", NewParams(empty ? "[]" : Tool));
        Assert.Equal(1, result["_meta"]?["hermesToolIntentVersion"]?.GetValue<int>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Intent_reply_preserves_ids_names_objects_usage_and_verifies_tier_without_tool_UI()
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        host.Session.IntentRecorder = new NativeToolIntents(HermesToolSchemas.Parse(NewParams(Tool)["_meta"], true)!);
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/new", NewParams(Tool));
        host.Session.OnSend = (s, _) => {
            s.IntentRecorder!.Record("actual-sdk-id", "terminal", Args());
            s.Raise(Ev.ToolStart("actual-sdk-id", "hermes__terminal", "{}"), Ev.ToolDone("actual-sdk-id", true, "intent_recorded_only"), Ev.Usage(10, 2), Ev.Idle());
        };
        var reply = await wire.ResultAsync("session/prompt", Prompt());
        Assert.Equal("end_turn", reply["stopReason"]?.GetValue<string>());
        Assert.Equal(1, reply["_meta"]?["hermesToolIntentVersion"]?.GetValue<int>());
        var call = reply["_meta"]?["hermesToolCalls"]?[0];
        Assert.Equal("actual-sdk-id", call?["id"]?.GetValue<string>());
        Assert.Equal("terminal", call?["name"]?.GetValue<string>());
        Assert.IsType<JsonObject>(call?["arguments"]);
        Assert.NotNull(reply["usage"]); Assert.Equal(1, host.Session.ModelVerifications);
        Assert.DoesNotContain(wire.Notifications, n => n["params"]?["update"]?["sessionUpdate"]?.GetValue<string>() is "tool_call" or "tool_call_update");
        Assert.Contains(wire.Notifications, n => n["method"]?.GetValue<string>() == "_fleet/model");
        host.Session.OnSend = (s, _) => s.Raise(Ev.Idle());
        var next = await wire.ResultAsync("session/prompt", Prompt());
        Assert.Empty((JsonArray)next["_meta"]!["hermesToolCalls"]!);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Explicit_cancel_never_returns_executable_intents()
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        host.Session.IntentRecorder = new NativeToolIntents(HermesToolSchemas.Parse(NewParams(Tool)["_meta"], true)!);
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/new", NewParams(Tool));
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Session.OnSend = (s, _) => { s.IntentRecorder!.Record("sdk-id", "terminal", Args()); recorded.SetResult(); };
        var pending = wire.ResultAsync("session/prompt", Prompt());
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        wire.Notify("session/cancel", new() { ["sessionId"] = "s-1" });
        var reply = await pending;
        Assert.Equal("cancelled", reply["stopReason"]?.GetValue<string>());
        Assert.Empty((JsonArray)reply["_meta"]!["hermesToolCalls"]!);
        Assert.True(host.Session.Aborted);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Tier_mismatch_and_poisoned_capture_never_return_calls()
    {
        foreach (var mismatch in new[] { true, false })
        {
            var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
            host.Session.IntentRecorder = new NativeToolIntents(HermesToolSchemas.Parse(NewParams(Tool)["_meta"], true)!);
            host.Session.TierMismatch = mismatch;
            new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
            await wire.ResultAsync("session/new", NewParams(Tool));
            host.Session.OnSend = (s, _) => {
                s.IntentRecorder!.Record("sdk-id", "terminal", Args());
                if (!mismatch) try { s.IntentRecorder.Record("sdk-id", "terminal", Args("conflict")); } catch (InvalidOperationException) { }
                s.Raise(Ev.Idle());
            };
            var reply = await wire.CallAsync("session/prompt", Prompt());
            Assert.NotNull(reply["error"]); Assert.Null(reply["result"]);
            await wire.CloseAsync();
        }
    }

    [Fact]
    public void Schema_boundaries_accept_256_and_exact_2MiB_and_reject_one_more_byte()
    {
        var tools = new JsonArray();
        for (var i = 0; i < 256; i++) { var t = JsonNode.Parse(Tool)![0]!.DeepClone(); t["function"]!["name"] = "t" + i; tools.Add(t); }
        Assert.Equal(256, HermesToolSchemas.Parse(new JsonObject { ["hermesToolSchemas"] = tools }, true)!.Count);
        var exact = (JsonArray)JsonNode.Parse(Tool)!;
        exact[0]!["function"]!["description"] = "";
        var remaining = HermesToolSchemas.MaxSchemaBytes - System.Text.Encoding.UTF8.GetByteCount(exact.ToJsonString());
        exact[0]!["function"]!["description"] = new string('x', remaining);
        Assert.Single(HermesToolSchemas.Parse(new JsonObject { ["hermesToolSchemas"] = exact }, true)!);
        exact[0]!["function"]!["description"] = new string('x', remaining + 1);
        Assert.Throws<ArgumentException>(() => HermesToolSchemas.Parse(new JsonObject { ["hermesToolSchemas"] = exact.DeepClone() }, true));
    }

    [Theory]
    [InlineData("{\"hermesToolIntentVersion\":2}")]
    [InlineData("{\"hermesToolUnknown\":[]}")]
    [InlineData("42")]
    public async Task Unsupported_metadata_fails_closed(string meta)
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        var result = await wire.CallAsync("session/new", new() { ["_meta"] = JsonNode.Parse(meta) });
        Assert.Equal(-32602, result["error"]?["code"]?.GetValue<int>()); Assert.Equal(0, host.Creates);
        await wire.CloseAsync();
    }

    [Theory]
    [InlineData("_fleet/shell")]
    [InlineData("session/set_config_option")]
    public async Task Hermes_mode_rejects_side_door_executors(string method)
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/new", NewParams(Tool));
        var result = await wire.CallAsync(method, new() { ["sessionId"] = "s-1", ["command"] = "never execute", ["configId"] = "agent", ["value"] = "ambient-agent" });
        Assert.NotNull(result["error"]); Assert.Empty(host.Session.Shells); Assert.Empty(host.Session.Agents);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Hermes_prompt_does_not_expand_native_commands_or_skills()
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        host.Session.Commands.Add(new("danger", "native", null, "skill"));
        host.Session.Results["danger"] = new("text", "executed");
        host.Session.OnSend = (s, _) => s.Raise(Ev.Idle());
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/new", NewParams(Tool));
        var p = Prompt(); p["prompt"]![0]!["text"] = "/danger"; p["_meta"] = new JsonObject { ["skills"] = new JsonArray("danger") };
        await wire.ResultAsync("session/prompt", p);
        Assert.Empty(host.Session.Invoked); Assert.Equal("/danger", host.Session.Sent.Single().Prompt);
        await wire.CloseAsync();
    }

    [Theory]
    [InlineData("session/load")]
    [InlineData("session/prompt")]
    public async Task Schemas_on_unsupported_methods_are_not_silently_ignored(string method)
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        host.Session.OnSend = (s, _) => s.Raise(Ev.Idle());
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/new", NewParams(Tool));
        var p = NewParams(Tool); p["sessionId"] = "s-1"; p["prompt"] = Prompt()["prompt"]!.DeepClone();
        var result = await wire.CallAsync(method, p);
        Assert.Equal(-32602, result["error"]?["code"]?.GetValue<int>());
        Assert.Empty(host.Session.Sent);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Hermes_history_does_not_label_intent_recording_as_action_success()
    {
        var wire = new Wire(); var host = new FakeHost { SupportsHermesToolIntents = true };
        host.Session.History.Add(Ev.ToolStart("old-id", "hermes__terminal", "{}"));
        host.Session.History.Add(Ev.ToolDone("old-id", true, "intent_recorded_only"));
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        await wire.ResultAsync("session/load", new() { ["sessionId"] = "s-1" });
        Assert.DoesNotContain(wire.Notifications, n => n["params"]?["update"]?["sessionUpdate"]?.GetValue<string>() is "tool_call" or "tool_call_update");
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Initialize_advertises_intents_only_for_enabled_host()
    {
        var wire = new Wire();
        var host = new FakeHost { SupportsHermesToolIntents = true };
        new AcpAgent(wire.Rpc, host, false, "test", _ => { }).Attach();
        var result = await wire.ResultAsync("initialize", new());
        Assert.Equal(1, result["_meta"]?["hermesToolIntentVersion"]?.GetValue<int>());
        await wire.CloseAsync();
    }
}
