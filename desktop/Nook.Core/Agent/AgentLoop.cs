using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nook.Core.Api;
using Nook.Core.Platform;

namespace Nook.Core.Agent;

public sealed record AgentMessage(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolName = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["role"] = Role, ["content"] = Content };
        if (ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.DeepClone() },
            }).ToArray());
        if (ToolName != null) o["tool_name"] = ToolName;
        return o;
    }
}

public sealed record AgentTurn(string Content, JsonArray ToolCalls, Usage? Usage = null, Budget? Budget = null);

/// <summary>One model step: the gateway's /api/agent (or a fake in tests).</summary>
public interface IAgentGateway
{
    Task<AgentTurn> SendAsync(JsonArray messages, JsonArray tools, IAgentObserver observer, CancellationToken ct);
}

public interface IAgentObserver
{
    void OnStep(int step) { }
    void OnStatus(string state) { }
    void OnDelta(string text) { }
    void OnNotice(string message) { }
    void OnAssistantMessage(string content, IReadOnlyList<ToolCall> calls) { }
    void OnToolStarted(ToolCall call) { }
    void OnToolFinished(ToolCall call, ToolResult result) { }
    void OnUsage(Usage? usage, Budget? budget) { }
    void OnWaiting(int seconds, string reason) { }
}

public enum AgentOutcomeKind { Answered, Stopped, StepLimit, Failed }

public sealed record AgentOutcome(AgentOutcomeKind Kind, string Message = "");

/// <summary>Talks to /api/agent over the Nook API.</summary>
public sealed class NookAgentGateway(NookApi api) : IAgentGateway
{
    public async Task<AgentTurn> SendAsync(JsonArray messages, JsonArray tools, IAgentObserver observer, CancellationToken ct)
    {
        var content = new System.Text.StringBuilder();
        var calls = new JsonArray();
        await foreach (var ev in api.AgentAsync(messages, tools, ct))
        {
            switch (ev.Type)
            {
                case "status": observer.OnStatus(ev.State ?? ""); break;
                case "notice": observer.OnNotice(ev.Message ?? ""); break;
                case "delta":
                    content.Append(ev.Text);
                    observer.OnDelta(ev.Text ?? "");
                    break;
                case "tool_calls":
                    if (ev.ToolCalls is { ValueKind: JsonValueKind.Array } arr)
                        foreach (var c in arr.EnumerateArray()) calls.Add(JsonNode.Parse(c.GetRawText()));
                    break;
                case "error":
                    throw new ApiException(ev.Error == "offline" ? 0 : 502, ev.Error ?? "model", ev.Message ?? "The model returned an error.");
                case "done":
                    return new AgentTurn(content.ToString(), calls, ev.Usage, ev.Budget);
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new ApiException(0, "offline", "Connection lost before the model finished.");
    }
}

/// <summary>Turns the model's tool calls (structured, or written as text) into ToolCalls, with readable errors.</summary>
public static class ToolCallParser
{
    static readonly Regex Tagged = new(@"<tool_call>\s*(.*?)\s*(</tool_call>|$)", RegexOptions.Singleline);
    static readonly Regex Fenced = new(@"^\s*```(?:json)?\s*(\{.*\})\s*```\s*$", RegexOptions.Singleline);

    public static (List<ToolCall> Calls, List<string> Errors) FromStructured(JsonArray raw)
    {
        var calls = new List<ToolCall>();
        var errors = new List<string>();
        foreach (var item in raw)
            Add(item as JsonObject is { } o && o["function"] is JsonObject f ? f : item as JsonObject, calls, errors);
        return (calls, errors);
    }

    /// <summary>Qwen sometimes writes calls into the text as &lt;tool_call&gt;{json}&lt;/tool_call&gt; or a bare JSON object.</summary>
    public static (List<ToolCall> Calls, List<string> Errors, string Text) FromContent(string content)
    {
        var calls = new List<ToolCall>();
        var errors = new List<string>();
        if (content.Contains("<tool_call>"))
        {
            foreach (Match m in Tagged.Matches(content))
            {
                JsonNode? node = null;
                try { node = JsonNode.Parse(m.Groups[1].Value); } catch (JsonException) { }
                if (node is JsonObject o) Add(o, calls, errors);
                else errors.Add("The text inside <tool_call> was not valid JSON.");
            }
            return (calls, errors, Tagged.Replace(content, "").Trim());
        }
        var trimmed = content.Trim();
        var fence = Fenced.Match(trimmed);
        var candidate = fence.Success ? fence.Groups[1].Value : trimmed;
        if (candidate.StartsWith('{') && candidate.EndsWith('}'))
        {
            try
            {
                if (JsonNode.Parse(candidate) is JsonObject o && o["name"] is JsonValue nv &&
                    nv.TryGetValue<string>(out var n) && ToolBox.Names.Contains(n))
                {
                    Add(o, calls, errors);
                    return (calls, errors, "");
                }
            }
            catch (JsonException) { }
        }
        return (calls, errors, content);
    }

    static void Add(JsonObject? fn, List<ToolCall> calls, List<string> errors)
    {
        if (fn == null) { errors.Add("A tool call was not a JSON object."); return; }
        var name = fn["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (name.Length == 0) { errors.Add("A tool call had no \"name\"."); return; }
        var args = fn["arguments"] ?? fn["parameters"];
        JsonObject obj;
        switch (args)
        {
            case null: obj = new JsonObject(); break;
            case JsonObject jo: obj = (JsonObject)jo.DeepClone(); break;
            case JsonValue jv when jv.TryGetValue<string>(out var str):
                try
                {
                    obj = string.IsNullOrWhiteSpace(str) ? new JsonObject()
                        : JsonNode.Parse(str) as JsonObject ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    errors.Add($"The arguments for {name} were not a valid JSON object.");
                    return;
                }
                break;
            default:
                errors.Add($"The arguments for {name} must be a JSON object.");
                return;
        }
        calls.Add(new ToolCall(name, obj));
    }
}

/// <summary>
/// The coding agent: send messages + tools, run the returned tool calls locally, send results back,
/// until the model answers without tools, the step cap is hit, or the user stops it.
/// </summary>
public sealed class AgentLoop
{
    readonly IAgentGateway _gateway;
    readonly ToolBox _tools;
    readonly IAgentObserver _observer;

    public AgentLoop(IAgentGateway gateway, ToolBox tools, IAgentObserver observer, IPlatform platform)
    {
        _gateway = gateway;
        _tools = tools;
        _observer = observer;
        Messages.Add(new AgentMessage("system", Prompts.CodingSystem(tools.Workspace, platform)));
    }

    public List<AgentMessage> Messages { get; } = new();
    public int MaxSteps { get; set; } = 25;
    public int MaxCallsPerStep { get; set; } = 8;
    public int MaxRateLimitRetries { get; set; } = 6;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    JsonArray MessagesJson() => new(Messages.Select(m => (JsonNode)m.ToJson()).ToArray());

    public async Task<AgentOutcome> RunAsync(string userText, CancellationToken ct)
    {
        Messages.Add(new AgentMessage("user", userText));
        var tools = ToolBox.Definitions();
        try
        {
            for (int step = 1; step <= MaxSteps; step++)
            {
                _observer.OnStep(step);
                var turn = await SendWithRetry(tools, ct);
                _observer.OnUsage(turn.Usage, turn.Budget);

                var (calls, errors) = ToolCallParser.FromStructured(turn.ToolCalls);
                var text = turn.Content;
                if (calls.Count == 0 && errors.Count == 0)
                    (calls, errors, text) = ToolCallParser.FromContent(turn.Content);

                if (calls.Count == 0 && errors.Count > 0)
                {
                    // Malformed: tell the model exactly what was wrong and let it try again.
                    Messages.Add(new AgentMessage("assistant", turn.Content));
                    _observer.OnAssistantMessage(text, Array.Empty<ToolCall>());
                    var fix = "Your tool call could not be used: " + string.Join(" ", errors) +
                              " Call a tool with its name and a JSON object of arguments, for example read_file with {\"path\": \"README.md\"}.";
                    Messages.Add(new AgentMessage("user", fix));
                    _observer.OnNotice("The model sent a malformed tool call; asked it to retry.");
                    continue;
                }

                if (calls.Count == 0)
                {
                    Messages.Add(new AgentMessage("assistant", turn.Content));
                    _observer.OnAssistantMessage(text, calls);
                    return new AgentOutcome(AgentOutcomeKind.Answered, text);
                }

                Messages.Add(new AgentMessage("assistant", text, calls));
                _observer.OnAssistantMessage(text, calls);
                for (int i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    if (ct.IsCancellationRequested)
                    {
                        // Keep the history consistent: every call gets a result.
                        Messages.Add(new AgentMessage("tool", "Stopped by the user before this ran.", ToolName: call.Name));
                        continue;
                    }
                    ToolResult result;
                    if (i >= MaxCallsPerStep)
                        result = new ToolResult($"Skipped: at most {MaxCallsPerStep} tool calls per step. Call it again if still needed.", true);
                    else
                    {
                        _observer.OnToolStarted(call);
                        try { result = await _tools.ExecuteAsync(call, ct); }
                        catch (OperationCanceledException) { result = new ToolResult("Stopped by the user.", true); }
                        _observer.OnToolFinished(call, result);
                    }
                    Messages.Add(new AgentMessage("tool", result.Content, ToolName: call.Name));
                }
                if (errors.Count > 0)
                    Messages.Add(new AgentMessage("user", "Some of your tool calls could not be used: " + string.Join(" ", errors)));
                ct.ThrowIfCancellationRequested();
            }
            return new AgentOutcome(AgentOutcomeKind.StepLimit,
                $"Stopped after {MaxSteps} steps without a final answer. Send a message to let it continue.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AgentOutcome(AgentOutcomeKind.Stopped, "Stopped.");
        }
        catch (ApiException e)
        {
            return new AgentOutcome(AgentOutcomeKind.Failed, e.Code == "not_found"
                ? "This Nook server doesn't support coding mode yet (no /api/agent). Update the gateway."
                : e.Message);
        }
    }

    async Task<AgentTurn> SendWithRetry(JsonArray tools, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _gateway.SendAsync(MessagesJson(), tools, _observer, ct);
            }
            catch (ApiException e) when (e.Code == "rate_limited" && attempt < MaxRateLimitRetries)
            {
                var wait = (int)Math.Clamp(e.Number("retry_after") ?? 10, 1, 120);
                _observer.OnWaiting(wait, e.Message);
                await Delay(TimeSpan.FromSeconds(wait), ct);
            }
        }
    }
}

public static class Prompts
{
    public static string CodingSystem(Workspace ws, IPlatform platform) => $"""
        You are Nook in coding mode. You work on the project in the user's workspace folder "{ws.Name}" on their {platform.OsName} computer, using tools.

        How to work:
        - Look before you change anything: use list_dir and search to find files, and read_file before you edit a file.
        - All paths are relative to the workspace root. You cannot access anything outside the workspace.
        - To change part of a file, use edit_file. Copy old_text exactly from read_file output (same spaces and indentation) and include enough lines that it appears only once. Use write_file for new files or complete rewrites.
        - run_command runs a {platform.ShellName} command in the workspace root and returns its output. Use it for builds, tests, git and similar. It has a time limit, so never start servers or anything that waits for input. Don't run destructive commands unless the user asked.
        - The user approves every file change and command. If a result says the user rejected it, don't repeat it: change your approach or ask.
        - Call one tool at a time and wait for the result. Never invent file contents or tool results.
        - If a tool returns an error, read it, fix the arguments and try again, or explain the problem.
        - When the task is done, or you need the user's input, stop calling tools and reply briefly: what you found or changed, and anything the user should check.
        """;
}
