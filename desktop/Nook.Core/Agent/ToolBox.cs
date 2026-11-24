using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nook.Core.Platform;

namespace Nook.Core.Agent;

public sealed record ToolCall(string Name, JsonObject Args)
{
    public string? Str(string key) => Args.TryGetPropertyValue(key, out var v) && v is JsonValue jv
        ? (jv.TryGetValue<string>(out var s) ? s : jv.ToJsonString()) : null;

    public int? Int(string key)
    {
        if (!Args.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<double>(out var d)) return (int)d;
        return jv.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }

    public bool Bool(string key) => Args.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
        ((jv.TryGetValue<bool>(out var b) && b) || (jv.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase)));

    public string Summary => Name switch
    {
        "run_command" => $"run_command: {Str("command")}",
        "search" => $"search: \"{Str("pattern")}\"" + (Str("path") is { Length: > 0 } p ? $" in {p}" : ""),
        _ => $"{Name}: {Str("path") ?? "."}",
    };
}

public sealed record ToolResult(string Content, bool IsError = false);

public enum ApprovalKind { WriteFile, EditFile, RunCommand }

/// <summary>What the user is asked to approve. For file changes, Diff holds before/after lines.</summary>
public sealed record ApprovalRequest(ApprovalKind Kind, string Path, string WorkingDir, string? Command,
    IReadOnlyList<DiffLine> Diff, bool IsNewFile, string? Explanation = null)
{
    public string Title => Kind switch
    {
        ApprovalKind.RunCommand => "Run command",
        ApprovalKind.WriteFile => IsNewFile ? $"Create {Path}" : $"Overwrite {Path}",
        _ => $"Edit {Path}",
    };
}

public sealed record ApprovalDecision(bool Approved, string? Reason = null)
{
    public static readonly ApprovalDecision Yes = new(true);
}

public interface IApprover
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

public enum ActivityKind { Read, Change, Command, Rejected, Error }

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Text);

/// <summary>Per-session state: approve-all (off by default, never persisted) and the activity log.</summary>
public sealed class AgentSession
{
    public bool ApproveAll { get; set; }
    public List<ActivityEntry> Activity { get; } = new();
    public event Action<ActivityEntry>? ActivityAdded;

    public void Log(ActivityKind kind, string text)
    {
        var e = new ActivityEntry(DateTime.Now, kind, text);
        lock (Activity) Activity.Add(e);
        ActivityAdded?.Invoke(e);
    }
}

/// <summary>The coding tools. Read-only tools run directly; writes and commands go through the approval gate.</summary>
public sealed class ToolBox
{
    public const int MaxResultChars = 12000;
    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__", "dist", "build", "target", ".idea", ".vs" };

    readonly IApprover _approver;
    readonly IPlatform _platform;

    public ToolBox(Workspace workspace, AgentSession session, IApprover approver, IPlatform platform)
    {
        Workspace = workspace;
        Session = session;
        _approver = approver;
        _platform = platform;
    }

    public Workspace Workspace { get; }
    public AgentSession Session { get; }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxCommandOutput { get; set; } = 20000;
    /// <summary>Live output of a running command (for the UI).</summary>
    public event Action<string>? CommandOutput;

    public static readonly string[] Names = { "read_file", "list_dir", "search", "write_file", "edit_file", "run_command" };

    static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    };

    static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static JsonArray Definitions() => new(
        Tool("read_file", "Read a text file from the workspace. Returns its lines. Use start_line to read further in long files.",
            new JsonObject { ["path"] = P("string", "File path relative to the workspace root"),
                ["start_line"] = P("integer", "First line to return (1-based, optional)") }, "path"),
        Tool("list_dir", "List the files and folders in a workspace folder.",
            new JsonObject { ["path"] = P("string", "Folder path relative to the workspace root; \".\" for the root") }),
        Tool("search", "Search file contents (case-insensitive) and file names in the workspace. Returns matching lines as path:line: text.",
            new JsonObject { ["pattern"] = P("string", "Text to look for"),
                ["path"] = P("string", "Folder to search in (optional, default the whole workspace)"),
                ["regex"] = P("boolean", "Treat pattern as a regular expression (optional)") }, "pattern"),
        Tool("write_file", "Create a new file or replace a whole file. The user must approve it.",
            new JsonObject { ["path"] = P("string", "File path relative to the workspace root"),
                ["content"] = P("string", "The complete new file content") }, "path", "content"),
        Tool("edit_file", "Replace one exact piece of text in a file. old_text must appear exactly once in the file. The user must approve it.",
            new JsonObject { ["path"] = P("string", "File path relative to the workspace root"),
                ["old_text"] = P("string", "Exact existing text to replace, copied from read_file, with enough lines to be unique"),
                ["new_text"] = P("string", "Replacement text") }, "path", "old_text", "new_text"),
        Tool("run_command", "Run a shell command in the workspace root and return its output. The user must approve it.",
            new JsonObject { ["command"] = P("string", "The command line to run") }, "command"));

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "read_file" => ReadFile(call),
                "list_dir" => ListDir(call),
                "search" => Search(call, ct),
                "write_file" => await WriteFileAsync(call, ct),
                "edit_file" => await EditFileAsync(call, ct),
                "run_command" => await RunCommandAsync(call, ct),
                _ => new ToolResult($"Unknown tool \"{call.Name}\". Available tools: {string.Join(", ", Names)}.", true),
            };
        }
        catch (ToolException e)
        {
            Session.Log(ActivityKind.Error, $"{call.Name}: {e.Message}");
            return new ToolResult("Error: " + e.Message, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Session.Log(ActivityKind.Error, $"{call.Name}: {e.Message}");
            return new ToolResult("Error: " + e.Message, true);
        }
    }

    static string Required(ToolCall call, string key) =>
        call.Str(key) is { } v && (v.Length > 0 || key is "content" or "new_text")
            ? v : throw new ToolException($"{call.Name} needs the \"{key}\" argument.");

    public static string Truncate(string text, int max = MaxResultChars) =>
        text.Length <= max ? text : text[..max] + $"\n[... truncated, {text.Length - max} more characters]";

    static bool LooksBinary(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[8192];
        var n = fs.Read(buf, 0, buf.Length);
        return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
    }

    string ExistingFile(ToolCall call, string path)
    {
        var full = Workspace.Resolve(path);
        if (Directory.Exists(full)) throw new ToolException($"{path} is a folder, not a file. Use list_dir.");
        if (!File.Exists(full)) throw new ToolException($"File not found: {path}. Use list_dir or search to find the right path.");
        if (new FileInfo(full).Length > 2_000_000) throw new ToolException($"{path} is too large to open (over 2 MB).");
        if (LooksBinary(full)) throw new ToolException($"{path} is a binary file.");
        return full;
    }

    ToolResult ReadFile(ToolCall call)
    {
        var path = Required(call, "path");
        var full = ExistingFile(call, path);
        var lines = File.ReadAllText(full).Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1] == "") lines = lines[..^1];
        var start = Math.Max(1, call.Int("start_line") ?? 1);
        var sb = new StringBuilder();
        int i = start - 1;
        for (; i < lines.Length && sb.Length < MaxResultChars; i++) sb.Append(lines[i]).Append('\n');
        var header = $"{Workspace.Relative(full)} ({lines.Length} lines)";
        if (start > 1 || i < lines.Length) header += $", showing lines {start}-{i}";
        if (i < lines.Length) sb.Append($"[... more lines follow; call read_file with start_line={i + 1}]\n");
        Session.Log(ActivityKind.Read, $"Read {Workspace.Relative(full)}");
        return new ToolResult(header + "\n" + sb);
    }

    ToolResult ListDir(ToolCall call)
    {
        var path = call.Str("path");
        var full = Workspace.Resolve(path);
        if (!Directory.Exists(full)) throw new ToolException($"Folder not found: {path}");
        var dir = new DirectoryInfo(full);
        var entries = dir.EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder($"{Workspace.Relative(full)}/ ({entries.Count} entries)\n");
        foreach (var e in entries.Take(300))
            sb.Append(e switch
            {
                DirectoryInfo => e.Name + "/",
                FileInfo f => $"{e.Name}  ({f.Length:N0} bytes)",
                _ => e.Name,
            }).Append(e.LinkTarget != null ? "  -> link" : "").Append('\n');
        if (entries.Count > 300) sb.Append($"[... {entries.Count - 300} more]\n");
        Session.Log(ActivityKind.Read, $"Listed {Workspace.Relative(full)}/");
        return new ToolResult(Truncate(sb.ToString()));
    }

    ToolResult Search(ToolCall call, CancellationToken ct)
    {
        var pattern = Required(call, "pattern");
        var root = Workspace.Resolve(call.Str("path"));
        if (!Directory.Exists(root)) throw new ToolException($"Folder not found: {call.Str("path")}");
        Regex rx;
        try
        {
            rx = new Regex(call.Bool("regex") ? pattern : Regex.Escape(pattern), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e) { throw new ToolException($"Bad regular expression: {e.Message}"); }

        var hits = new List<string>();
        int files = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0 && hits.Count < 100 && files < 5000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList(); }
            catch (Exception) { continue; }
            foreach (var e in entries)
            {
                if (e.LinkTarget != null) continue; // never follow links while walking
                if (e is DirectoryInfo d) { if (!SkipDirs.Contains(d.Name)) stack.Push(d); continue; }
                if (e is not FileInfo f) continue;
                files++;
                var rel = Workspace.Relative(f.FullName);
                if (rx.IsMatch(f.Name)) hits.Add($"{rel}  (file name matches)");
                if (f.Length > 1_000_000) continue;
                try
                {
                    if (LooksBinary(f.FullName)) continue;
                    int n = 0;
                    foreach (var line in File.ReadLines(f.FullName))
                    {
                        n++;
                        if (!rx.IsMatch(line)) continue;
                        var t = line.Trim();
                        hits.Add($"{rel}:{n}: {(t.Length > 200 ? t[..200] + "..." : t)}");
                        if (hits.Count >= 100) break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
                if (hits.Count >= 100) break;
            }
        }
        Session.Log(ActivityKind.Read, $"Searched \"{pattern}\"");
        if (hits.Count == 0) return new ToolResult($"No matches for \"{pattern}\".");
        var outp = string.Join("\n", hits);
        if (hits.Count >= 100) outp += "\n[... stopped at 100 matches; narrow the search]";
        return new ToolResult(Truncate(outp));
    }

    async Task<ApprovalDecision> Approve(ApprovalRequest req, CancellationToken ct) =>
        Session.ApproveAll ? ApprovalDecision.Yes : await _approver.RequestAsync(req, ct);

    static ToolResult Rejected(string what, ApprovalDecision d) =>
        new($"The user rejected {what}." + (string.IsNullOrWhiteSpace(d.Reason) ? "" : $" Their note: {d.Reason}") +
            " Do not retry the same thing; adjust your approach or ask the user.", true);

    async Task<ToolResult> WriteFileAsync(ToolCall call, CancellationToken ct)
    {
        var path = Required(call, "path");
        var content = Required(call, "content");
        var full = Workspace.Resolve(path);
        if (Directory.Exists(full)) throw new ToolException($"{path} is a folder.");
        var isNew = !File.Exists(full);
        var before = isNew ? "" : File.ReadAllText(ExistingFile(call, path));
        var rel = Workspace.Relative(full);
        var diff = Diff.Compute(before, content);
        var d = await Approve(new ApprovalRequest(ApprovalKind.WriteFile, rel, Workspace.Root, null, diff, isNew), ct);
        if (!d.Approved)
        {
            Session.Log(ActivityKind.Rejected, $"Rejected write to {rel}");
            return Rejected($"writing {rel}", d);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Re-resolve right before writing so a link created in the meantime can't redirect the write.
        full = Workspace.Resolve(path);
        await File.WriteAllTextAsync(full, content, CancellationToken.None);
        var (add, rem) = Diff.Count(diff);
        Session.Log(ActivityKind.Change, $"{(isNew ? "Created" : "Wrote")} {rel} (+{add} -{rem})");
        return new ToolResult($"{(isNew ? "Created" : "Wrote")} {rel} ({Diff.Lines(content).Length} lines).");
    }

    async Task<ToolResult> EditFileAsync(ToolCall call, CancellationToken ct)
    {
        var path = Required(call, "path");
        var oldText = Required(call, "old_text");
        var newText = Required(call, "new_text");
        var full = ExistingFile(call, path);
        var rel = Workspace.Relative(full);
        var before = File.ReadAllText(full);
        var after = ApplyEdit(before, oldText, newText, rel);
        var diff = Diff.Compute(before, after);
        var d = await Approve(new ApprovalRequest(ApprovalKind.EditFile, rel, Workspace.Root, null, diff, false), ct);
        if (!d.Approved)
        {
            Session.Log(ActivityKind.Rejected, $"Rejected edit to {rel}");
            return Rejected($"the edit to {rel}", d);
        }
        if (File.ReadAllText(full) != before) throw new ToolException($"{rel} changed while waiting for approval. Read it again.");
        await File.WriteAllTextAsync(Workspace.Resolve(path), after, CancellationToken.None);
        var (add, rem) = Diff.Count(diff);
        Session.Log(ActivityKind.Change, $"Edited {rel} (+{add} -{rem})");
        return new ToolResult($"Edited {rel}: replaced {Diff.Lines(oldText).Length} line(s) with {Diff.Lines(newText).Length} line(s).");
    }

    /// <summary>Search-and-replace of exactly one occurrence. Tolerates \n vs \r\n differences.</summary>
    public static string ApplyEdit(string text, string oldText, string newText, string name = "the file")
    {
        if (oldText.Length == 0) throw new ToolException("old_text is empty. Copy the exact text to replace from read_file.");
        var crlf = text.Contains("\r\n");
        if (crlf)
        {
            oldText = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        var first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            throw new ToolException($"old_text was not found in {name}. It must match the file exactly (spaces and indentation too). Read the file again and copy the text.");
        if (text.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
            throw new ToolException($"old_text appears more than once in {name}. Include more surrounding lines so it is unique.");
        return text[..first] + newText + text[(first + oldText.Length)..];
    }

    async Task<ToolResult> RunCommandAsync(ToolCall call, CancellationToken ct)
    {
        var command = Required(call, "command").Trim();
        var cwd = Workspace.Root;
        var d = await Approve(new ApprovalRequest(ApprovalKind.RunCommand, ".", cwd, command, Array.Empty<DiffLine>(), false), ct);
        if (!d.Approved)
        {
            Session.Log(ActivityKind.Rejected, $"Rejected command: {command}");
            return Rejected($"running `{command}`", d);
        }
        var r = await CommandRunner.RunAsync(_platform, command, cwd, CommandTimeout, MaxCommandOutput,
            s => CommandOutput?.Invoke(s), ct);
        var status = r.TimedOut ? $"timed out after {CommandTimeout.TotalSeconds:0} s and was stopped"
            : r.Stopped ? "stopped by the user" : $"exit code {r.ExitCode}";
        Session.Log(ActivityKind.Command, $"Ran `{command}` ({status})");
        var body = r.Output.Length == 0 ? "(no output)" : r.Output;
        if (r.Truncated) body += $"\n[... output cut at {MaxCommandOutput} characters]";
        ct.ThrowIfCancellationRequested();
        return new ToolResult($"Command `{command}` {status}.\n{Truncate(body)}", r.ExitCode is not 0);
    }
}
