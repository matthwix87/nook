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
        string real;
        try { real = RealPath(Path.Combine(Root, path)); }
        catch (ToolException) { throw; }
        catch (Exception) { throw new ToolException($"Invalid path: {path}"); }
        if (!Contains(real)) throw new ToolException($"Access denied: \"{path}\" is outside the workspace. Use paths relative to the workspace root.");
        return real;
    }

    public bool Contains(string realPath) =>
        string.Equals(realPath, Root, Cmp) ||
        realPath.StartsWith(Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar, Cmp);

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
                var abs = Path.IsPathRooted(target) ? target : Path.Combine(cur, target);
                cur = RealPath(abs, depth + 1);
            }
            else cur = next;
        }
        return cur;
    }
}
