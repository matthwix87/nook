namespace Nook.Core.Agent;

/// <summary>An error the model should see as a tool result (bad path, missing file, ...).</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>The folder coding mode works in. Every path a tool touches is resolved here and must stay inside.</summary>
public sealed class Workspace
{
    static readonly StringComparison Cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Workspace(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: {root}");
        Root = RealPath(root);
    }

    /// <summary>The workspace's real path (symlinks resolved).</summary>
    public string Root { get; }
    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : Root;

    /// <summary>
    /// Resolve a model-supplied path (relative to the root, or absolute) to a real path inside the workspace.
    /// Symlinks are followed (even broken ones) and the final location must still be inside, so neither
    /// "..", absolute paths nor links can escape.
    /// </summary>
    public string Resolve(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0 || path == ".") return Root;
        if (path.Contains('\0')) throw new ToolException("Invalid path.");
        if (OperatingSystem.IsWindows() && WindowsPathRules.Check(path) is { } why) throw new ToolException($"Invalid path \"{path}\": {why}");
        string real;
        try { real = RealPath(Path.Combine(Root, path)); }
        catch (ToolException) { throw; }
        catch (Exception) { throw new ToolException($"Invalid path: {path}"); }
        if (!Contains(real)) throw new ToolException($"Access denied: \"{path}\" is outside the workspace. Use paths relative to the workspace root.");
        return real;
    }

    public bool Contains(string realPath) => IsInside(Root, realPath, Cmp, Path.DirectorySeparatorChar);

    /// <summary>True if <paramref name="path"/> is <paramref name="root"/> or below it (both already canonical).</summary>
    public static bool IsInside(string root, string path, StringComparison cmp, char sep) =>
        string.Equals(path, root, cmp) || path.StartsWith(root.EndsWith(sep) ? root : root + sep, cmp);

    public string Relative(string fullPath)
    {
        var rel = Path.GetRelativePath(Root, fullPath);
        return rel == "." ? "." : rel.Replace('\\', '/');
    }

    /// <summary>Canonical path with every symlink component resolved (works for paths that don't exist yet).</summary>
    public static string RealPath(string path, int depth = 0)
    {
        if (depth > 40) throw new ToolException("Too many levels of symbolic links.");
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(cur, parts[i]);
            // LinkTarget uses lstat, so it also sees broken links (a write through one would land outside).
            var target = new FileInfo(next).LinkTarget;
            if (target != null)
            {
                if (OperatingSystem.IsWindows()) target = WindowsPathRules.NormalizeLinkTarget(target);
                var abs = Path.IsPathRooted(target) ? target : Path.Combine(cur, target);
                cur = RealPath(abs, depth + 1);
            }
            else cur = next;
        }
        return cur;
    }
}

/// <summary>
/// Extra checks for model-supplied paths on Windows. Pure string logic (no file system calls), so it is unit
/// tested on any OS. The containment check still runs afterwards on the fully resolved path.
/// </summary>
public static class WindowsPathRules
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" };

    /// <summary>Why <paramref name="path"/> is not allowed, or null if it is fine.</summary>
    public static string? Check(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith(@"\??\"))
            return @"device paths (\\?\, \\.\) are not allowed. Use paths relative to the workspace root.";
        var rest = p;
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            if (p.Length == 2 || p[2] != '\\')
                return "drive-relative paths like C:file are not allowed. Use paths relative to the workspace root.";
            rest = p[3..];
        }
        if (rest.Contains(':')) return "':' is not allowed in file names (alternate data streams).";
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") continue;
            if (part.EndsWith('.') || part.EndsWith(' ')) return "names ending in a dot or a space are not allowed on Windows.";
            if (IsReservedName(part)) return $"\"{part}\" is a reserved device name on Windows.";
        }
        return null;
    }

    /// <summary>CON, NUL, COM1, LPT1, ... also with an extension ("nul.txt") or trailing spaces.</summary>
    public static bool IsReservedName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        if (Reserved.Contains(stem)) return true;
        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    /// <summary>
    /// Link targets read from junctions can come back as \\?\C:\... or \??\C:\...; turn those into normal paths so the
    /// containment check can compare them. Anything else (e.g. \\?\Volume{...}) stays as is and is then refused.
    /// </summary>
    public static string NormalizeLinkTarget(string target)
    {
        if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + target[8..];
        if (target.StartsWith(@"\\?\") || target.StartsWith(@"\??\"))
        {
            var rest = target[4..];
            if (rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\') return rest;
        }
        return target;
    }
}
