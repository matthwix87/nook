using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nook.Core.Agent;
using Nook.Core.Api;

namespace Nook.App.ViewModels;

public partial class CodingItem : ObservableObject
{
    public CodingItem(string kind, string text, string title = "")
    {
        Kind = kind;
        _text = text;
        Title = title;
    }

    public string Kind { get; }  // user | assistant | tool | notice | error
    public string Title { get; }
    public bool IsUser => Kind == "user";
    public bool IsAssistant => Kind == "assistant";
    public bool IsTool => Kind == "tool";
    public bool IsNotice => Kind == "notice";
    public bool IsError => Kind == "error";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    string _text;
    public bool HasText => Text.Length > 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    string _detail = "";
    public bool HasDetail => Detail.Length > 0;
    [ObservableProperty] bool _isRunning;
    [ObservableProperty] bool _failed;
}

public sealed class DiffLineItem(DiffLine line)
{
    public string Text { get; } = line.Prefix + line.Text;
    public string OldNo { get; } = line.OldNo?.ToString() ?? "";
    public string NewNo { get; } = line.NewNo?.ToString() ?? "";
    public bool IsAdded => line.Kind == DiffKind.Added;
    public bool IsRemoved => line.Kind == DiffKind.Removed;
    public bool IsGap => line.Kind == DiffKind.Gap;
}

public partial class ApprovalViewModel : ObservableObject
{
    readonly TaskCompletionSource<ApprovalDecision> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApprovalViewModel(ApprovalRequest request)
    {
        Request = request;
        var shown = Diff.WithContext(request.Diff.ToList(), 3);
        if (shown.Count > 600) shown = shown.Take(600).Append(new DiffLine(DiffKind.Gap, $"... {shown.Count - 600} more lines not shown ...")).ToList();
        DiffLines = shown.Select(l => new DiffLineItem(l)).ToList();
        var (add, rem) = Diff.Count(request.Diff);
        Stats = request.Kind == ApprovalKind.RunCommand ? "" : $"+{add}  -{rem}";
    }

    public ApprovalRequest Request { get; }
    public string Title => Request.Title;
    public bool IsCommand => Request.Kind == ApprovalKind.RunCommand;
    public bool IsFileChange => !IsCommand;
    public string Command => Request.Command ?? "";
    public string WorkingDir => Request.WorkingDir;
    public string Stats { get; }
    public IReadOnlyList<DiffLineItem> DiffLines { get; }
    public bool NoChanges => IsFileChange && DiffLines.Count == 0;
    [ObservableProperty] string _rejectNote = "";
    public Task<ApprovalDecision> Result => _tcs.Task;

    [RelayCommand]
    void Approve() => _tcs.TrySetResult(ApprovalDecision.Yes);

    [RelayCommand]
    void Reject() => _tcs.TrySetResult(new ApprovalDecision(false, RejectNote.Trim()));

    public void Cancel() => _tcs.TrySetCanceled();
}

public sealed class ActivityItem(ActivityEntry e)
{
    public string Time { get; } = e.Time.ToString("HH:mm:ss");
    public string Text { get; } = e.Text;
    public bool IsChange => e.Kind == ActivityKind.Change;
    public bool IsCommand => e.Kind == ActivityKind.Command;
    public bool IsProblem => e.Kind is ActivityKind.Rejected or ActivityKind.Error;
}

/// <summary>
/// Coding mode: the model works in a local folder through tools. Reads run directly; writes and commands
/// wait for Approve (unless "Approve all for this session" is on). Tools always run on this computer.
/// </summary>
public partial class CodingViewModel : ObservableObject, IAgentObserver, IApprover
{
    readonly MainViewModel _main;
    AgentSession _session = new();
    AgentLoop? _loop;
    ToolBox? _tools;
    CancellationTokenSource? _runCts;
    CodingItem? _streaming;
    CodingItem? _runningTool;

    public CodingViewModel(MainViewModel main)
    {
        _main = main;
        _workspacePath = main.Services.Settings.LastWorkspace;
    }

    public ObservableCollection<CodingItem> Items { get; } = new();
    public ObservableCollection<ActivityItem> Activity { get; } = new();

    [ObservableProperty] string _workspacePath;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace), nameof(NoWorkspace), nameof(WorkspaceName))]
    Workspace? _workspace;
    public bool HasWorkspace => Workspace != null;
    public bool NoWorkspace => Workspace == null;
    public string WorkspaceName => Workspace?.Name ?? "";
    [ObservableProperty] string _workspaceError = "";

    [ObservableProperty] bool _approveAll;
    partial void OnApproveAllChanged(bool value) => _session.ApproveAll = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(IsIdle))]
    bool _isRunning;
    public bool IsIdle => !IsRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    string _input = "";
    public bool CanSend => !IsRunning && HasWorkspace && Input.Trim().Length > 0;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApproval))]
    ApprovalViewModel? _pendingApproval;
    public bool HasApproval => PendingApproval != null;
    [ObservableProperty] bool _showActivity = true;
    [ObservableProperty] long _sessionTokens;
    public string SessionTokensText => SessionTokens > 0 ? $"{SessionTokens:N0} tokens this session" : "";
    partial void OnSessionTokensChanged(long value) => OnPropertyChanged(nameof(SessionTokensText));

    /// <summary>Set by the view: shows the OS folder picker and returns a local path (or null).</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public event Action? ScrollRequested;

    static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    [RelayCommand]
    async Task ChooseFolderAsync()
    {
        if (PickFolder == null) return;
        var path = await PickFolder();
        if (path != null)
        {
            WorkspacePath = path;
            OpenWorkspace();
        }
    }

    [RelayCommand]
    void OpenWorkspace()
    {
        WorkspaceError = "";
        try
        {
            var ws = new Workspace(WorkspacePath.Trim());
            StartSession(ws);
            _main.Services.Settings.LastWorkspace = ws.Root;
            _main.Services.SaveSettings();
        }
        catch (Exception e) { WorkspaceError = e.Message; }
    }

    void StartSession(Workspace ws)
    {
        _runCts?.Cancel();
        Workspace = ws;
        WorkspacePath = ws.Root;
        var approveAll = _session.ApproveAll;
        _session = new AgentSession { ApproveAll = approveAll };
        _session.ActivityAdded += e => Ui(() => Activity.Insert(0, new ActivityItem(e)));
        _tools = new ToolBox(ws, _session, this, _main.Services.Platform);
        _tools.CommandOutput += s => Ui(() =>
        {
            if (_runningTool != null && _runningTool.Detail.Length < 20000) _runningTool.Detail += s;
        });
        _loop = new AgentLoop(new NookAgentGateway(_main.Api), _tools, this, _main.Services.Platform);
        Items.Clear();
        Activity.Clear();
        SessionTokens = 0;
        Items.Add(new CodingItem("notice",
            $"Workspace: {ws.Root}\nNook can read files here freely. Every file change and command waits for your approval."));
    }

    [RelayCommand]
    void NewSession()
    {
        if (Workspace != null) StartSession(Workspace);
    }

    [RelayCommand]
    void CloseWorkspace()
    {
        _runCts?.Cancel();
        Workspace = null;
        Items.Clear();
        Activity.Clear();
    }

    /// <summary>Logout: forget everything, including approve-all.</summary>
    public void Reset()
    {
        CloseWorkspace();
        ApproveAll = false;
        _session = new AgentSession();
    }

    [RelayCommand]
    void ToggleActivity() => ShowActivity = !ShowActivity;

    [RelayCommand]
    void Stop()
    {
        _runCts?.Cancel();
        PendingApproval?.Cancel();
    }

    [RelayCommand]
    async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsRunning || _loop == null) return;
        Input = "";
        Items.Add(new CodingItem("user", text));
        ScrollRequested?.Invoke();
        IsRunning = true;
        StatusText = "Thinking…";
        var cts = _runCts = new CancellationTokenSource();
        var loop = _loop;
        AgentOutcome outcome;
        try
        {
            // Off the UI thread: tools do file and process work; callbacks marshal back.
            outcome = await Task.Run(() => loop.RunAsync(text, cts.Token));
        }
        catch (Exception e) { outcome = new AgentOutcome(AgentOutcomeKind.Failed, e.Message); }
        _streaming = null;
        IsRunning = false;
        StatusText = "";
        PendingApproval = null;
        switch (outcome.Kind)
        {
            case AgentOutcomeKind.Stopped: Items.Add(new CodingItem("notice", "Stopped.")); break;
            case AgentOutcomeKind.StepLimit: Items.Add(new CodingItem("notice", outcome.Message)); break;
            case AgentOutcomeKind.Failed:
                Items.Add(new CodingItem("error", outcome.Message));
                if (outcome.Message == "Can't reach the server" || outcome.Message.StartsWith("Connection lost"))
                    _ = _main.CheckStatusAsync();
                break;
        }
        ScrollRequested?.Invoke();
    }

    // ---- IAgentObserver (called from the agent's thread)
    public void OnStep(int step) => Ui(() =>
    {
        _streaming = null;
        StatusText = step == 1 ? "Thinking…" : $"Thinking… (step {step})";
    });

    public void OnStatus(string state) => Ui(() => StatusText = state == "starting" ? "Waking up Nook…" : "Thinking…");

    public void OnDelta(string text) => Ui(() =>
    {
        if (_streaming == null)
        {
            _streaming = new CodingItem("assistant", "");
            Items.Add(_streaming);
        }
        _streaming.Text += text;
        StatusText = "Writing…";
        ScrollRequested?.Invoke();
    });

    public void OnNotice(string message) => Ui(() => Items.Add(new CodingItem("notice", message)));

    public void OnAssistantMessage(string content, IReadOnlyList<ToolCall> calls) => Ui(() =>
    {
        // Text-written tool calls were streamed raw; show the cleaned text instead.
        if (_streaming != null)
        {
            if (string.IsNullOrWhiteSpace(content)) Items.Remove(_streaming);
            else _streaming.Text = content;
        }
        else if (!string.IsNullOrWhiteSpace(content)) Items.Add(new CodingItem("assistant", content));
        _streaming = null;
    });

    public void OnToolStarted(ToolCall call) => Ui(() =>
    {
        _runningTool = new CodingItem("tool", "", call.Summary) { IsRunning = true };
        Items.Add(_runningTool);
        StatusText = call.Name == "run_command" ? "Running command…" : $"Using {call.Name}…";
        ScrollRequested?.Invoke();
    });

    public void OnToolFinished(ToolCall call, ToolResult result) => Ui(() =>
    {
        var item = _runningTool ?? new CodingItem("tool", "", call.Summary);
        if (!Items.Contains(item)) Items.Add(item);
        item.IsRunning = false;
        item.Failed = result.IsError;
        item.Detail = result.Content.Length > 6000 ? result.Content[..6000] + "\n[...]" : result.Content;
        _runningTool = null;
        StatusText = "Thinking…";
    });

    public void OnUsage(Usage? usage, Budget? budget) => Ui(() =>
    {
        if (usage != null) SessionTokens += usage.Total;
        _main.UpdateMeter(budget);
    });

    public void OnWaiting(int seconds, string reason) => Ui(() => StatusText = $"Rate limited, retrying in {seconds} s…");

    // ---- IApprover
    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var vm = new ApprovalViewModel(request);
        Ui(() =>
        {
            PendingApproval = vm;
            StatusText = "Waiting for your approval…";
            ScrollRequested?.Invoke();
        });
        using var reg = ct.Register(vm.Cancel);
        try { return await vm.Result; }
        finally
        {
            Ui(() =>
            {
                if (PendingApproval == vm) PendingApproval = null;
                StatusText = "Thinking…";
            });
        }
    }

    /// <summary>For screenshots/tests: open a workspace without the folder picker.</summary>
    public void OpenForTest(string path)
    {
        WorkspacePath = path;
        StartSession(new Workspace(path));
    }

    public void ShowApprovalForTest(ApprovalRequest r) => PendingApproval = new ApprovalViewModel(r);
}
