using System.Text.Json.Nodes;
using Nook.Core.Agent;
using Nook.Core.Platform;

namespace Nook.Tests;

/// <summary>Approver that answers with a fixed decision and records what it was asked.</summary>
public sealed class FakeApprover(bool approve, string? reason = null) : IApprover
{
    public List<ApprovalRequest> Requests { get; } = new();
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(new ApprovalDecision(approve, reason));
    }
}

public class ToolBoxTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly Workspace _ws;
    readonly AgentSession _session = new();
    static readonly IPlatform Platform = new UnixPlatform(Path.GetTempPath());

    public ToolBoxTests()
    {
        File.WriteAllText(_tmp.Sub("main.py"), "import sys\nprint('hi')\n");
        Directory.CreateDirectory(_tmp.Sub("pkg"));
        File.WriteAllText(_tmp.Sub("pkg/util.py"), "def helper():\n    return 42\n");
        _ws = new Workspace(_tmp.Path);
    }

    public void Dispose() => _tmp.Dispose();

    ToolBox Box(IApprover approver) => new(_ws, _session, approver, Platform);

    static ToolCall Call(string name, object args) =>
        new(name, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject());

    [Fact]
    public async Task Read_list_search_need_no_approval()
    {
        var approver = new FakeApprover(false);
        var box = Box(approver);
        var r = await box.ExecuteAsync(Call("read_file", new { path = "main.py" }), default);
        Assert.False(r.IsError);
        Assert.Contains("print('hi')", r.Content);
        r = await box.ExecuteAsync(Call("list_dir", new { path = "." }), default);
        Assert.Contains("pkg/", r.Content);
        Assert.Contains("main.py", r.Content);
        r = await box.ExecuteAsync(Call("search", new { pattern = "HELPER" }), default);
        Assert.Contains("pkg/util.py:1: def helper():", r.Content);
        Assert.Empty(approver.Requests);
        Assert.Equal(3, _session.Activity.Count);
    }

    [Fact]
    public async Task Read_outside_workspace_is_an_error_result()
    {
        var r = await Box(new FakeApprover(true)).ExecuteAsync(Call("read_file", new { path = "../../etc/passwd" }), default);
        Assert.True(r.IsError);
        Assert.Contains("outside the workspace", r.Content);
    }

    [Fact]
    public async Task Rejected_write_does_not_touch_disk()
    {
        var approver = new FakeApprover(false, "not now");
        var r = await Box(approver).ExecuteAsync(Call("write_file", new { path = "new.txt", content = "x" }), default);
        Assert.True(r.IsError);
        Assert.Contains("rejected", r.Content);
        Assert.Contains("not now", r.Content);
        Assert.False(File.Exists(_tmp.Sub("new.txt")));
        Assert.Single(approver.Requests);
        Assert.True(approver.Requests[0].IsNewFile);
        Assert.Equal(ActivityKind.Rejected, _session.Activity[^1].Kind);
    }

    [Fact]
    public async Task Approved_write_creates_file_and_dirs()
    {
        var approver = new FakeApprover(true);
        var r = await Box(approver).ExecuteAsync(Call("write_file", new { path = "a/b/new.txt", content = "hello\n" }), default);
        Assert.False(r.IsError, r.Content);
        Assert.Equal("hello\n", File.ReadAllText(_tmp.Sub("a/b/new.txt")));
        Assert.Equal(DiffKind.Added, approver.Requests[0].Diff[0].Kind);
    }

    [Fact]
    public async Task Rejected_edit_leaves_file_unchanged_and_shows_diff()
    {
        var approver = new FakeApprover(false);
        var r = await Box(approver).ExecuteAsync(Call("edit_file", new { path = "main.py", old_text = "print('hi')", new_text = "print('bye')" }), default);
        Assert.True(r.IsError);
        Assert.Equal("import sys\nprint('hi')\n", File.ReadAllText(_tmp.Sub("main.py")));
        var diff = approver.Requests[0].Diff;
        Assert.Contains(diff, l => l.Kind == DiffKind.Removed && l.Text == "print('hi')");
        Assert.Contains(diff, l => l.Kind == DiffKind.Added && l.Text == "print('bye')");
    }

    [Fact]
    public async Task Approved_edit_applies()
    {
        var r = await Box(new FakeApprover(true)).ExecuteAsync(Call("edit_file", new { path = "main.py", old_text = "print('hi')", new_text = "print('bye')" }), default);
        Assert.False(r.IsError, r.Content);
        Assert.Equal("import sys\nprint('bye')\n", File.ReadAllText(_tmp.Sub("main.py")));
    }

    [Fact]
    public async Task Bad_edit_fails_before_asking()
    {
        var approver = new FakeApprover(true);
        var r = await Box(approver).ExecuteAsync(Call("edit_file", new { path = "main.py", old_text = "nope", new_text = "x" }), default);
        Assert.True(r.IsError);
        Assert.Contains("not found", r.Content);
        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task Rejected_command_does_not_run()
    {
        var approver = new FakeApprover(false);
        var r = await Box(approver).ExecuteAsync(Call("run_command", new { command = "touch ran.txt" }), default);
        Assert.True(r.IsError);
        Assert.False(File.Exists(_tmp.Sub("ran.txt")));
        Assert.Equal("touch ran.txt", approver.Requests[0].Command);
        Assert.Equal(_ws.Root, approver.Requests[0].WorkingDir);
    }

    [Fact]
    public async Task Approved_command_runs_in_workspace_and_captures_output()
    {
        var r = await Box(new FakeApprover(true)).ExecuteAsync(Call("run_command", new { command = "pwd; echo out; echo err >&2; exit 3" }), default);
        Assert.Contains(_ws.Root, r.Content);
        Assert.Contains("out", r.Content);
        Assert.Contains("err", r.Content);
        Assert.Contains("exit code 3", r.Content);
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Approve_all_skips_the_approver()
    {
        var approver = new FakeApprover(false);
        _session.ApproveAll = true;
        var box = Box(approver);
        await box.ExecuteAsync(Call("write_file", new { path = "x.txt", content = "1" }), default);
        await box.ExecuteAsync(Call("run_command", new { command = "touch ran.txt" }), default);
        Assert.Empty(approver.Requests);
        Assert.True(File.Exists(_tmp.Sub("x.txt")));
        Assert.True(File.Exists(_tmp.Sub("ran.txt")));
    }

    [Fact]
    public void Approve_all_is_off_by_default()
    {
        Assert.False(new AgentSession().ApproveAll);
    }

    [Fact]
    public async Task Command_timeout_and_output_cap()
    {
        var box = Box(new FakeApprover(true));
        box.CommandTimeout = TimeSpan.FromSeconds(1);
        var r = await box.ExecuteAsync(Call("run_command", new { command = "sleep 30" }), default);
        Assert.Contains("timed out", r.Content);

        box.MaxCommandOutput = 100;
        r = await box.ExecuteAsync(Call("run_command", new { command = "seq 1 10000" }), default);
        Assert.Contains("output cut at 100", r.Content);
        Assert.True(r.Content.Length < 400);
    }

    [Fact]
    public async Task Command_can_be_stopped()
    {
        using var cts = new CancellationTokenSource(500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Box(new FakeApprover(true)).ExecuteAsync(Call("run_command", new { command = "sleep 30" }), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Missing_argument_and_unknown_tool_are_explained()
    {
        var box = Box(new FakeApprover(true));
        var r = await box.ExecuteAsync(Call("read_file", new { }), default);
        Assert.Contains("needs the \"path\" argument", r.Content);
        r = await box.ExecuteAsync(Call("delete_everything", new { }), default);
        Assert.Contains("Unknown tool", r.Content);
    }

    [Fact]
    public async Task Long_files_are_paged()
    {
        File.WriteAllText(_tmp.Sub("big.txt"), string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i} " + new string('x', 40))));
        var r = await Box(new FakeApprover(true)).ExecuteAsync(Call("read_file", new { path = "big.txt" }), default);
        Assert.Contains("start_line=", r.Content);
        Assert.True(r.Content.Length < ToolBox.MaxResultChars + 500);
    }
}
