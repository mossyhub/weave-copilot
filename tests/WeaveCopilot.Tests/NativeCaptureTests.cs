using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class NativeCaptureTests
{
    private static JsonElement Element(string text) { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
    private static JsonElement Tool(string name) => Element("""{"type":"function","function":{"name":"NAME","description":"Intent only","parameters":{"type":"object","properties":{"command":{"type":"string"}},"additionalProperties":false}}}""".Replace("NAME", name));

    [Fact]
    public async Task Native_configuration_allows_only_custom_aliases_and_denies_permissions()
    {
        var intents = new NativeToolIntents([Tool("bash"), Tool("terminal"), Tool(new string('a', 64))]);
        var config = new SessionConfig { AvailableTools = ["bash"], EnableSkills = true, EnableHostGitOperations = true, EnableFileHooks = true };
        SdkHost.ApplyExternalToolBoundary(config, true, intents);
        Assert.Equal(config.Tools!.Select(t => t.Name), config.AvailableTools);
        Assert.All(config.AvailableTools!, n => { Assert.StartsWith("hermes__", n); Assert.InRange(n.Length, 1, 64); });
        Assert.DoesNotContain("bash", config.AvailableTools!);
        Assert.Equal(3, config.AvailableTools!.Distinct().Count());
        var native = Assert.IsAssignableFrom<AIFunction>(config.Tools!.First());
        Assert.Equal(Tool("bash").GetProperty("function").GetProperty("parameters").GetRawText(), native.JsonSchema.GetRawText());
        Assert.Equal(true, native.AdditionalProperties["is_terminal"]);
        Assert.False(config.EnableSkills); Assert.False(config.EnableHostGitOperations); Assert.False(config.EnableFileHooks);
        Assert.Null(config.McpServers); Assert.Empty(config.SkillDirectories!);
        var denied = await config.OnPermissionRequest!(null!, null!);
        Assert.Equal("PermissionDecisionReject", denied.GetType().Name);
        Assert.Equal(config.AvailableTools, new NativeToolIntents([Tool("bash"), Tool("terminal"), Tool(new string('a', 64))]).Functions.Select(t => t.Name));
    }

    [Fact]
    public void Capture_deduplicates_and_conflicting_repeats_fail_closed()
    {
        var c = new NativeToolIntents([Tool("terminal")]);
        c.BeginPrompt();
        c.Record("call-1", "terminal", Element("{\"command\":\"never execute\"}"));
        c.Record("call-1", "terminal", Element("{\"command\":\"never execute\"}"));
        Assert.Single(c.Snapshot());
        Assert.Equal("terminal", c.Snapshot()[0].Name);
        Assert.Throws<InvalidOperationException>(() => c.Record("call-1", "terminal", Element("{\"command\":\"different\"}")));
        Assert.Throws<InvalidOperationException>(() => c.Snapshot());
        c.BeginPrompt(); Assert.Empty(c.Snapshot());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void Non_object_arguments_fail_closed(string args)
    {
        var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
        Assert.Throws<InvalidOperationException>(() => c.Record("id", "terminal", Element(args)));
        Assert.Throws<InvalidOperationException>(() => c.Snapshot());
    }

    [Fact]
    public void Count_and_byte_bounds_invalidate_entire_prompt_not_partial_execution()
    {
        var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
        for (var i = 0; i < 32; i++) c.Record("id" + i, "terminal", Element("{}"));
        Assert.Equal(32, c.Snapshot().Count);
        Assert.Throws<InvalidOperationException>(() => c.Record("overflow", "terminal", Element("{}")));
        Assert.Throws<InvalidOperationException>(() => c.Snapshot());
        c.BeginPrompt();
        Assert.Throws<InvalidOperationException>(() => c.Record("id", "terminal", Element("{\"value\":\"" + new string('x', 1024 * 1024) + "\"}")));
        Assert.Throws<InvalidOperationException>(() => c.Snapshot());
    }

    [Fact]
    public void Intent_wire_size_exact_boundary_is_inclusive_and_aggregate_is_bounded()
    {
        var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
        var empty = new NativeToolIntent("id", "terminal", Element("{\"x\":\"\"}"));
        var remaining = NativeToolIntents.MaxIntentBytes - 2 - System.Text.Encoding.UTF8.GetByteCount(empty.ToJson().ToJsonString());
        c.Record("id", "terminal", Element("{\"x\":\"" + new string('x', remaining) + "\"}"));
        Assert.Single(c.Snapshot());
        Assert.Throws<InvalidOperationException>(() => c.Record("extra", "terminal", Element("{}")));
        Assert.Throws<InvalidOperationException>(() => c.Snapshot());
        c.BeginPrompt();
        var half = Element("{\"x\":\"" + new string('x', NativeToolIntents.MaxIntentBytes / 2) + "\"}");
        c.Record("first", "terminal", half);
        Assert.Throws<InvalidOperationException>(() => c.Record("second", "terminal", half));
    }

    [Fact]
    public void Unknown_names_and_empty_ids_are_not_executable()
    {
        foreach (var (id, name) in new[] { ("id", "bash"), ("", "terminal") })
        {
            var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
            Assert.Throws<InvalidOperationException>(() => c.Record(id, name, Element("{}")));
            Assert.Throws<InvalidOperationException>(() => c.Snapshot());
        }
    }

    [Fact]
    public void Duplicate_objects_with_reordered_properties_are_one_intent()
    {
        var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
        c.Record("id", "terminal", Element("{\"a\":1,\"b\":2}"));
        c.Record("id", "terminal", Element("{\"b\":2,\"a\":1}"));
        Assert.Single(c.Snapshot());
    }

    [Fact]
    public void Rejected_callback_returns_terminal_safe_ack_but_prompt_remains_failed()
    {
        var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
        var ack = c.Acknowledge("id", "terminal", Element("[]"));
        Assert.Equal("intent_rejected_only", ack.GetProperty("status").GetString());
        Assert.False(ack.GetProperty("executed").GetBoolean());
        Assert.Throws<InvalidOperationException>(() => c.Snapshot());
        c.BeginPrompt();
        ack = c.Acknowledge("id", "terminal", Element("{}"));
        Assert.Equal("intent_recorded_only", ack.GetProperty("status").GetString());
        Assert.False(ack.GetProperty("executed").GetBoolean());
        Assert.Equal("outer_hermes", ack.GetProperty("execution_deferred_to").GetString());
    }

    [Fact]
    public void Cancellation_closes_capture_until_next_prompt()
    {
        var c = new NativeToolIntents([Tool("terminal")]); c.BeginPrompt();
        c.Record("id", "terminal", Element("{}")); c.CancelPrompt();
        Assert.Empty(c.Snapshot());
        Assert.Throws<InvalidOperationException>(() => c.Record("late", "terminal", Element("{}")));
        Assert.Empty(c.Snapshot());
        c.BeginPrompt(); c.Record("next", "terminal", Element("{}")); Assert.Single(c.Snapshot());
    }
}
