using System.Text.Json.Nodes;
using Nook.Core.Agent;
using Nook.Core.Api;
using Nook.Core.Platform;

namespace Nook.Tests;

/// <summary>Scripted gateway: returns the queued turns in order and records the messages it was sent.</summary>
public sealed class FakeGateway : IAgentGateway
{
    public Queue<Func<AgentTurn>> Turns { get; } = new();
    public List<JsonArray> Sent { get; } = new();

    public FakeGateway Then(string content, params (string Name, object Args)[] calls)
    {
        Turns.Enqueue(() => new AgentTurn(content, new JsonArray(calls.Select(c => (JsonNode)new JsonObject
        {
            ["function"] = new JsonObject
            {
                ["name"] = c.Name,
                ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(c.Args)),
            },
        }).ToArray())));
        return this;
    }

    public FakeGateway ThenRaw(string content, JsonArray calls)
    {
        Turns.Enqueue(() => new AgentTurn(content, calls));
        return this;
    }

    public FakeGateway ThenThrow(Exception e)
    {
        Turns.Enqueue(() => throw e);
        return this;
    }

    public Task<AgentTurn> SendAsync(JsonArray messages, JsonArray tools, IAgentObserver observer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Sent.Add((JsonArray)messages.DeepClone());
        if (Turns.Count == 0) return Task.FromResult(new AgentTurn("(no more turns)", new JsonArray()));
        return Task.FromResult(Turns.Dequeue()());
    }
}

public sealed class RecordingObserver : IAgentObserver
{
    public List<string> Log { get; } = new();
    public void OnNotice(string message) => Log.Add("notice:" + message);
    public void OnToolStarted(ToolCall call) => Log.Add("tool:" + call.Name);
    public void OnWaiting(int seconds, string reason) => Log.Add($"wait:{seconds}");
}

public class AgentLoopTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly AgentSession _session = new();
    static readonly IPlatform Platform = new UnixPlatform(Path.GetTempPath());

    public AgentLoopTests()
    {
        File.WriteAllText(_tmp.Sub("app.txt"), "version = 1\n");
    }

    public void Dispose() => _tmp.Dispose();

    (AgentLoop, RecordingObserver) Loop(FakeGateway gw, IApprover approver)
    {
        var obs = new RecordingObserver();
        var box = new ToolBox(new Workspace(_tmp.Path), _session, approver, Platform);
        return (new AgentLoop(gw, box, obs, Platform) { Delay = (_, _) => Task.CompletedTask }, obs);
    }

    static JsonNode? Last(JsonArray msgs) => msgs[msgs.Count - 1];

    [Fact]
    public async Task Reads_then_answers()
    {
        var gw = new FakeGateway().Then("", ("read_file", new { path = "app.txt" })).Then("It is version 1.");
        var (loop, _) = Loop(gw, new FakeApprover(true));
        var outcome = await loop.RunAsync("What version?", default);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        Assert.Equal("It is version 1.", outcome.Message);
        Assert.Equal(2, gw.Sent.Count);
        // First request: system + user. Second: + assistant(tool_calls) + tool result.
        Assert.Equal("system", gw.Sent[0][0]!["role"]!.GetValue<string>());
        var second = gw.Sent[1];
        Assert.Equal("read_file", second[2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", Last(second)!["role"]!.GetValue<string>());
        Assert.Equal("read_file", Last(second)!["tool_name"]!.GetValue<string>());
        Assert.Contains("version = 1", Last(second)!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rejection_is_sent_back_and_file_untouched()
    {
        var gw = new FakeGateway()
            .Then("", ("edit_file", new { path = "app.txt", old_text = "version = 1", new_text = "version = 2" }))
            .Then("OK, I won't change it.");
        var approver = new FakeApprover(false, "keep it");
        var (loop, _) = Loop(gw, approver);
        var outcome = await loop.RunAsync("bump version", default);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        Assert.Single(approver.Requests);
        Assert.Equal("version = 1\n", File.ReadAllText(_tmp.Sub("app.txt")));
        var toolMsg = Last(gw.Sent[1])!["content"]!.GetValue<string>();
        Assert.Contains("rejected", toolMsg);
        Assert.Contains("keep it", toolMsg);
    }

    [Fact]
    public async Task Approved_edit_is_applied()
    {
        var gw = new FakeGateway()
            .Then("", ("edit_file", new { path = "app.txt", old_text = "version = 1", new_text = "version = 2" }))
            .Then("Done.");
        var (loop, _) = Loop(gw, new FakeApprover(true));
        await loop.RunAsync("bump version", default);
        Assert.Equal("version = 2\n", File.ReadAllText(_tmp.Sub("app.txt")));
    }

    [Fact]
    public async Task Malformed_tool_call_gets_a_correction()
    {
        var bad = new JsonArray(new JsonObject { ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{not json" } });
        var gw = new FakeGateway().ThenRaw("", bad).Then("Sorry, done.");
        var (loop, obs) = Loop(gw, new FakeApprover(true));
        var outcome = await loop.RunAsync("go", default);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        var correction = Last(gw.Sent[1])!;
        Assert.Equal("user", correction["role"]!.GetValue<string>());
        Assert.Contains("not a valid JSON object", correction["content"]!.GetValue<string>());
        Assert.Contains(obs.Log, l => l.StartsWith("notice:"));
    }

    [Fact]
    public async Task Tool_call_written_as_text_is_understood()
    {
        var gw = new FakeGateway()
            .Then("Let me look.\n<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"app.txt\"}}\n</tool_call>")
            .Then("Version 1.");
        var (loop, obs) = Loop(gw, new FakeApprover(true));
        var outcome = await loop.RunAsync("version?", default);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        Assert.Contains("tool:read_file", obs.Log);
        Assert.Equal("Let me look.", gw.Sent[1][2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bare_json_tool_call_is_understood_but_unrelated_json_is_an_answer()
    {
        var gw = new FakeGateway().Then("{\"name\": \"list_dir\", \"arguments\": {}}").Then("{\"name\": \"bob\"}");
        var (loop, obs) = Loop(gw, new FakeApprover(true));
        var outcome = await loop.RunAsync("look", default);
        Assert.Contains("tool:list_dir", obs.Log);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        Assert.Equal("{\"name\": \"bob\"}", outcome.Message);
    }

    [Fact]
    public async Task Unknown_tool_is_reported_to_the_model()
    {
        var gw = new FakeGateway().Then("", ("rm_rf", new { })).Then("ok");
        var (loop, _) = Loop(gw, new FakeApprover(true));
        await loop.RunAsync("go", default);
        Assert.Contains("Unknown tool", Last(gw.Sent[1])!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stops_at_step_limit()
    {
        var gw = new FakeGateway();
        for (int i = 0; i < 10; i++) gw.Then("", ("list_dir", new { path = "." }));
        var (loop, _) = Loop(gw, new FakeApprover(true));
        loop.MaxSteps = 3;
        var outcome = await loop.RunAsync("loop forever", default);
        Assert.Equal(AgentOutcomeKind.StepLimit, outcome.Kind);
        Assert.Equal(3, gw.Sent.Count);
    }

    [Fact]
    public async Task Waits_and_retries_when_rate_limited()
    {
        var body = System.Text.Json.JsonDocument.Parse("{\"error\":\"rate_limited\",\"retry_after\":7}").RootElement;
        var gw = new FakeGateway().ThenThrow(new ApiException(429, "rate_limited", "Slow down", body)).Then("fine");
        var (loop, obs) = Loop(gw, new FakeApprover(true));
        var outcome = await loop.RunAsync("go", default);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        Assert.Contains("wait:7", obs.Log);
    }

    [Fact]
    public async Task Api_error_fails_cleanly()
    {
        var gw = new FakeGateway().ThenThrow(new ApiException(413, "doesnt_fit", "This task is too big for the context window"));
        var (loop, _) = Loop(gw, new FakeApprover(true));
        var outcome = await loop.RunAsync("go", default);
        Assert.Equal(AgentOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("too big", outcome.Message);
    }

    [Fact]
    public async Task Stop_while_waiting_for_approval()
    {
        using var cts = new CancellationTokenSource();
        var gw = new FakeGateway().Then("", ("write_file", new { path = "x.txt", content = "1" }), ("list_dir", new { }));
        var approver = new BlockingApprover();
        var (loop, _) = Loop(gw, approver);
        var run = loop.RunAsync("go", cts.Token);
        await approver.Asked.Task;
        cts.Cancel();
        var outcome = await run;
        Assert.Equal(AgentOutcomeKind.Stopped, outcome.Kind);
        Assert.False(File.Exists(_tmp.Sub("x.txt")));
        // History stays consistent: both calls have a tool result.
        Assert.Equal(2, loop.Messages.Count(m => m.Role == "tool"));
    }

    [Fact]
    public async Task Conversation_continues_across_tasks()
    {
        var gw = new FakeGateway().Then("first").Then("second");
        var (loop, _) = Loop(gw, new FakeApprover(true));
        await loop.RunAsync("one", default);
        await loop.RunAsync("two", default);
        Assert.Equal(4, gw.Sent[1].Count); // system, user, assistant, user
    }

    sealed class BlockingApprover : IApprover
    {
        public TaskCompletionSource Asked { get; } = new();
        public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ApprovalDecision.Yes;
        }
    }
}
