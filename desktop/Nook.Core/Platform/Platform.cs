using System.Text.Json;

namespace Nook.Core.Platform;

/// <summary>Everything OS-specific lives behind this interface (config dir, key storage, shell).</summary>
public interface IPlatform
{
    string ConfigDir { get; }
    ISecretStore Secrets { get; }
    /// <summary>Human-readable shell name, shown to the model in coding mode.</summary>
    string ShellName { get; }
    string OsName { get; }
    /// <summary>Program + arguments that run <paramref name="command"/> through the platform shell.</summary>
    (string FileName, IReadOnlyList<string> Args) Shell(string command);
}

public static class PlatformInfo
{
    public static IPlatform Current { get; set; } =
        OperatingSystem.IsWindows() ? new WindowsPlatform() : new UnixPlatform();
}

public sealed class UnixPlatform : IPlatform
{
    public UnixPlatform(string? configDir = null)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        ConfigDir = configDir ?? Path.Combine(
            string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg,
            "nook");
        Secrets = new FileSecretStore(ConfigDir);
    }

    public string ConfigDir { get; }
    public ISecretStore Secrets { get; }
    public string ShellName => File.Exists("/bin/bash") ? "bash" : "sh";
    public string OsName => OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    public (string, IReadOnlyList<string>) Shell(string command) =>
        (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", new[] { "-c", command });
}

public sealed class WindowsPlatform : IPlatform
{
    public WindowsPlatform(string? configDir = null)
    {
        ConfigDir = configDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nook");
        // TODO(windows): replace with a DPAPI-protected store (System.Security.Cryptography.ProtectedData,
        // DataProtectionScope.CurrentUser). The plain file sits in %APPDATA%\Nook, readable by the user only by default ACLs.
        Secrets = new FileSecretStore(ConfigDir);
    }

    public string ConfigDir { get; }
    public ISecretStore Secrets { get; }
    public string ShellName => "PowerShell";
    public string OsName => "Windows";

    // TODO(windows): verify quoting/encoding with real commands (UTF-8 output: [Console]::OutputEncoding).
    public (string, IReadOnlyList<string>) Shell(string command) =>
        ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command });
}

public interface ISecretStore
{
    string? Load(string username);
    void Save(string username, string key);
    void Delete(string username);
}

/// <summary>Keys per username in a JSON file. On Unix the file is created 0600 inside a 0700 directory.</summary>
public sealed class FileSecretStore : ISecretStore
{
    readonly string _dir, _path;
    readonly object _lock = new();

    public FileSecretStore(string dir)
    {
        _dir = dir;
        _path = Path.Combine(dir, "keys.json");
    }

    Dictionary<string, string> Read()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }

    void Write(Dictionary<string, string> keys)
    {
        ConfigFiles.EnsureDir(_dir);
        ConfigFiles.WritePrivate(_path, JsonSerializer.Serialize(keys));
    }

    public string? Load(string username)
    {
        lock (_lock) return Read().TryGetValue(username.ToLowerInvariant(), out var k) ? k : null;
    }

    public void Save(string username, string key)
    {
        lock (_lock)
        {
            var keys = Read();
            keys[username.ToLowerInvariant()] = key;
            Write(keys);
        }
    }

    public void Delete(string username)
    {
        lock (_lock)
        {
            var keys = Read();
            if (keys.Remove(username.ToLowerInvariant())) Write(keys);
        }
    }
}

public static class ConfigFiles
{
    public static void EnsureDir(string dir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Atomic write; the file is only readable by the user on Unix.</summary>
    public static void WritePrivate(string path, string text)
    {
        var tmp = path + ".tmp";
        var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, opts))
        using (var w = new StreamWriter(fs)) w.Write(text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
