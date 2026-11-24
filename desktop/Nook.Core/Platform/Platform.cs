using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nook.Core.Platform;

/// <summary>Everything OS-specific lives behind this interface (config dir, key storage, shell).</summary>
public interface IPlatform
{
    string ConfigDir { get; }
    ISecretStore Secrets { get; }
    /// <summary>Human-readable shell name, shown to the model in coding mode.</summary>
    string ShellName { get; }
    /// <summary>One line for the coding prompt on how to write commands for this shell.</summary>
    string ShellHint { get; }
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
    public string ShellHint => "Use normal POSIX shell syntax.";
    public string OsName => OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    public (string, IReadOnlyList<string>) Shell(string command) =>
        (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", new[] { "-c", command });
}

public sealed class WindowsPlatform : IPlatform
{
    /// <summary>
    /// Runs before every command: no progress bars, and UTF-8 (without BOM) for PowerShell's own output, for
    /// text piped to native programs and for the console code page that native programs inherit.
    /// </summary>
    public const string Prelude =
        "$ProgressPreference='SilentlyContinue';" +
        "$OutputEncoding=New-Object System.Text.UTF8Encoding $false;" +
        "try{[Console]::OutputEncoding=$OutputEncoding}catch{}\n";

    readonly string _shellExe;

    /// <param name="pathEnv">PATH to look for pwsh.exe in (tests pass their own; default: the real PATH).</param>
    public WindowsPlatform(string? configDir = null, string? pathEnv = null)
    {
        ConfigDir = configDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nook");
        // Keys are DPAPI-protected (current user) in keys.dat; an old plain keys.json is migrated and deleted.
        Secrets = OperatingSystem.IsWindows()
            ? new FileSecretStore(ConfigDir, "keys.dat", Dpapi.Protect, Dpapi.Unprotect, legacyPlainFile: "keys.json")
            : new FileSecretStore(ConfigDir);
        var pwsh = FindOnPath("pwsh.exe", pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "");
        UsesPwsh = pwsh != null;
        _shellExe = pwsh ?? WindowsPowerShellPath();
    }

    public string ConfigDir { get; }
    public ISecretStore Secrets { get; }
    /// <summary>True when PowerShell 7+ (pwsh.exe) was found on PATH; otherwise Windows PowerShell 5.1 is used.</summary>
    public bool UsesPwsh { get; }
    public string ShellName => UsesPwsh ? "PowerShell 7 (pwsh)" : "Windows PowerShell 5.1";
    public string ShellHint => "This is PowerShell, not bash or cmd: use PowerShell syntax (Get-ChildItem, Select-String, " +
        "Get-Content, Remove-Item, $env:NAME for environment variables, `;` to chain commands" +
        (UsesPwsh ? "" : "; `&&` and `||` do not work in this version") + "). Paths use backslashes; forward slashes also work.";
    public string OsName => "Windows";

    // The command goes in as one argument after -Command. .NET quotes ArgumentList entries with the MSVCRT rules
    // that both powershell.exe and pwsh.exe use to split their command line, so the text arrives unchanged.
    // (-EncodedCommand is avoided: with redirected streams Windows PowerShell can then write errors as CLIXML.)
    public (string, IReadOnlyList<string>) Shell(string command) =>
        (_shellExe, new[] { "-NoProfile", "-NonInteractive", "-Command", Prelude + command });

    /// <summary>Full path of <paramref name="exe"/> in the first absolute PATH entry that has it (relative entries are skipped).</summary>
    public static string? FindOnPath(string exe, string pathEnv)
    {
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = dir.Trim('"');
            if (!Path.IsPathFullyQualified(d)) continue;
            try
            {
                var full = Path.Combine(d, exe);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // Full path, so a powershell.exe next to Nook.exe or in the current folder is never picked up instead.
    static string WindowsPowerShellPath()
    {
        var full = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(full) ? full : "powershell.exe";
    }
}

/// <summary>DPAPI, current-user scope: only this Windows account (on this machine or its roaming profile) can decrypt.</summary>
[SupportedOSPlatform("windows")]
static class Dpapi
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nook desktop keys v1");
    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

public interface ISecretStore
{
    string? Load(string username);
    void Save(string username, string key);
    void Delete(string username);
}

/// <summary>
/// Keys per username in a JSON file. On Unix the file is created 0600 inside a 0700 directory.
/// With <c>protect</c>/<c>unprotect</c> (DPAPI on Windows) the file holds the protected bytes instead of plain JSON,
/// and a plain <c>legacyPlainFile</c> left by an older version is merged in and deleted on first use.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    readonly string _dir, _path;
    readonly string? _legacyPath;
    readonly Func<byte[], byte[]>? _protect, _unprotect;
    readonly object _lock = new();
    bool _migrated;

    public FileSecretStore(string dir, string fileName = "keys.json",
        Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null, string? legacyPlainFile = null)
    {
        _dir = dir;
        _path = Path.Combine(dir, fileName);
        _protect = protect;
        _unprotect = unprotect;
        if (legacyPlainFile != null && legacyPlainFile != fileName) _legacyPath = Path.Combine(dir, legacyPlainFile);
    }

    static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();

    Dictionary<string, string> Read()
    {
        MigrateLegacy();
        try
        {
            if (_unprotect == null) return Parse(File.ReadAllText(_path));
            return Parse(Encoding.UTF8.GetString(_unprotect(File.ReadAllBytes(_path))));
        }
        catch { return new(); } // missing or unreadable (e.g. another Windows account): log in again
    }

    void Write(Dictionary<string, string> keys)
    {
        ConfigFiles.EnsureDir(_dir);
        var json = JsonSerializer.Serialize(keys);
        if (_protect == null) ConfigFiles.WritePrivate(_path, json);
        else ConfigFiles.WritePrivate(_path, _protect(Encoding.UTF8.GetBytes(json)));
    }

    void MigrateLegacy()
    {
        if (_migrated || _legacyPath == null) return;
        _migrated = true;
        if (!File.Exists(_legacyPath)) return;
        try
        {
            var old = Parse(File.ReadAllText(_legacyPath));
            var keys = Read();
            foreach (var (user, key) in old) keys.TryAdd(user, key);
            Write(keys);
            File.Delete(_legacyPath);
        }
        catch { /* keep the old file; try again next start */ }
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
    public static void WritePrivate(string path, string text) => WritePrivate(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>Atomic write; the file is only readable by the user on Unix (on Windows: the profile folder's ACLs).</summary>
    public static void WritePrivate(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, opts)) fs.Write(data);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
