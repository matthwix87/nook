using System.Text;
using System.Text.Json.Nodes;
using Nook.Core.Agent;
using Nook.Core.Platform;

namespace Nook.Tests;

/// <summary>Windows-specific logic that is pure enough to test on Linux (no real Windows here).</summary>
public class WindowsPathRulesTests
{
    [Theory]
    [InlineData("src/a.cs")]
    [InlineData(@"src\a.cs")]
    [InlineData(@"src\..\src\a.cs")]
    [InlineData(@"C:\proj\src\a.cs")]
    [InlineData("C:/proj/src/a.cs")]
    [InlineData(@"\\server\share\proj\a.cs")]
    [InlineData(".gitignore")]
    [InlineData("console.log")]
    [InlineData("com10.txt")]
    [InlineData("nul_file")]
    public void Allows_normal_paths(string path) => Assert.Null(WindowsPathRules.Check(path));

    [Theory]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\\.\C:\x")]
    [InlineData("//?/C:/x")]
    [InlineData("//./PhysicalDrive0")]
    [InlineData(@"\??\C:\x")]
    [InlineData("C:")]
    [InlineData("C:secret.txt")]
    [InlineData("a.txt:hidden")]
    [InlineData("a.txt::$DATA")]
    [InlineData(@"C:\proj\a.txt:stream")]
    [InlineData("NUL")]
    [InlineData("src/con")]
    [InlineData("aux.txt")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("COM\u00b9")]
    [InlineData("CONIN$")]
    [InlineData("nul .txt")]
    [InlineData("secret.txt.")]
    [InlineData("secret.txt ")]
    [InlineData(@"dir.\file")]
    [InlineData("...")]
    public void Blocks_dangerous_paths(string path) => Assert.NotNull(WindowsPathRules.Check(path));

    [Theory]
    [InlineData(@"\\?\C:\proj\lib", @"C:\proj\lib")]
    [InlineData(@"\??\D:\data", @"D:\data")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"\\?\Volume{1234}\x", @"\\?\Volume{1234}\x")]
    [InlineData(@"C:\plain", @"C:\plain")]
    [InlineData(@"..\rel", @"..\rel")]
    public void Normalizes_junction_targets(string target, string expected) =>
        Assert.Equal(expected, WindowsPathRules.NormalizeLinkTarget(target));

    [Theory]
    [InlineData(@"C:\Proj", @"C:\Proj", true)]
    [InlineData(@"C:\Proj", @"c:\proj\src\a.cs", true)]
    [InlineData(@"C:\Proj", @"C:\PROJ", true)]
    [InlineData(@"C:\Proj", @"C:\Project\a.cs", false)]
    [InlineData(@"C:\Proj", @"C:\", false)]
    [InlineData(@"C:\Proj", @"D:\Proj\a.cs", false)]
    [InlineData(@"C:\", @"C:\anything", true)]
    [InlineData(@"\\server\share\proj", @"\\SERVER\share\proj\a", true)]
    [InlineData(@"\\server\share\proj", @"\\server\share\other", false)]
    [InlineData(@"C:\Proj", @"\\?\C:\Proj\a", false)]
    public void Containment_is_case_insensitive_with_backslashes(string root, string path, bool inside) =>
        Assert.Equal(inside, Workspace.IsInside(root, path, StringComparison.OrdinalIgnoreCase, '\\'));
}

public class WindowsPlatformTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Shell_runs_powershell_with_command_as_one_argument()
    {
        var p = new WindowsPlatform(_tmp.Path, pathEnv: "");
        Assert.False(p.UsesPwsh);
        var cmd = "Write-Output \"a b\"; git log --format='%h' | Select-Object -First 2";
        var (file, args) = p.Shell(cmd);
        Assert.EndsWith("powershell.exe", file);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-Command", WindowsPlatform.Prelude + cmd }, args);
        Assert.Contains("UTF8Encoding", WindowsPlatform.Prelude);
        Assert.Contains("PowerShell", p.ShellName);
        Assert.Contains("&&", p.ShellHint); // 5.1 has no && / ||: the model is told
    }

    [Fact]
    public void Prefers_pwsh_from_absolute_path_entries_only()
    {
        var dir = Directory.CreateDirectory(_tmp.Sub("pwsh7")).FullName;
        File.WriteAllText(Path.Combine(dir, "pwsh.exe"), "");
        Assert.Null(WindowsPlatform.FindOnPath("pwsh.exe", "relative;.;"));
        Assert.Equal(Path.Combine(dir, "pwsh.exe"), WindowsPlatform.FindOnPath("pwsh.exe", $"relative; \"{dir}\" ;"));
        var p = new WindowsPlatform(_tmp.Path, pathEnv: dir);
        Assert.True(p.UsesPwsh);
        Assert.Equal(Path.Combine(dir, "pwsh.exe"), p.Shell("x").Item1);
    }

    [Fact]
    public void Coding_prompt_names_the_shell()
    {
        var ws = new Workspace(_tmp.Path);
        var prompt = Prompts.CodingSystem(ws, new WindowsPlatform(_tmp.Path, pathEnv: ""));
        Assert.Contains("Windows computer", prompt);
        Assert.Contains("Windows PowerShell 5.1 command", prompt);
        Assert.Contains("not bash", prompt);
        Assert.Contains("bash command", Prompts.CodingSystem(ws, new UnixPlatform(_tmp.Path)));
    }

    // Stand-in for DPAPI: anything reversible that is clearly not plain JSON.
    static byte[] Fake(byte[] b) => b.Select(x => (byte)(x ^ 0x5A)).ToArray();

    [Fact]
    public void Protected_store_never_writes_the_key_in_plain_text()
    {
        var store = new FileSecretStore(_tmp.Path, "keys.dat", Fake, Fake, legacyPlainFile: "keys.json");
        store.Save("Alice", "sk-secret-123");
        Assert.Equal("sk-secret-123", store.Load("alice"));
        var raw = File.ReadAllBytes(_tmp.Sub("keys.dat"));
        Assert.DoesNotContain("sk-secret", Encoding.UTF8.GetString(raw));
        Assert.False(File.Exists(_tmp.Sub("keys.json")));
        store.Delete("alice");
        Assert.Null(new FileSecretStore(_tmp.Path, "keys.dat", Fake, Fake).Load("alice"));
    }

    [Fact]
    public void Plain_keys_file_is_migrated_and_deleted()
    {
        new FileSecretStore(_tmp.Path).Save("bob", "old-key"); // what older versions wrote
        Assert.True(File.Exists(_tmp.Sub("keys.json")));
        var store = new FileSecretStore(_tmp.Path, "keys.dat", Fake, Fake, legacyPlainFile: "keys.json");
        Assert.Equal("old-key", store.Load("bob"));
        Assert.False(File.Exists(_tmp.Sub("keys.json")));
        Assert.DoesNotContain("old-key", Encoding.UTF8.GetString(File.ReadAllBytes(_tmp.Sub("keys.dat"))));
        Assert.Equal("old-key", new FileSecretStore(_tmp.Path, "keys.dat", Fake, Fake).Load("bob"));
    }

    [Fact]
    public void Unreadable_protected_file_just_means_logged_out()
    {
        File.WriteAllBytes(_tmp.Sub("keys.dat"), new byte[] { 1, 2, 3 });
        var store = new FileSecretStore(_tmp.Path, "keys.dat", Fake, _ => throw new System.Security.Cryptography.CryptographicException());
        Assert.Null(store.Load("bob"));
    }
}

public class LineEndingAndBomTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    static ToolCall Call(string name, object args) =>
        new(name, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject());

    ToolBox Box() => new(new Workspace(_tmp.Path), new AgentSession(), new FakeApprover(true), new UnixPlatform(_tmp.Path));

    static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    [Fact]
    public async Task Edit_keeps_crlf_and_bom()
    {
        File.WriteAllBytes(_tmp.Sub("a.cs"), Bom.Concat(Encoding.UTF8.GetBytes("class A\r\n{\r\n    int x;\r\n}\r\n")).ToArray());
        var r = await Box().ExecuteAsync(Call("edit_file", new { path = "a.cs", old_text = "{\n    int x;", new_text = "{\n    int x;\n    int y;" }), default);
        Assert.False(r.IsError, r.Content);
        var bytes = File.ReadAllBytes(_tmp.Sub("a.cs"));
        Assert.Equal(Bom, bytes[..3]);
        Assert.Equal("class A\r\n{\r\n    int x;\r\n    int y;\r\n}\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Overwrite_keeps_crlf_and_bom_of_existing_file()
    {
        File.WriteAllBytes(_tmp.Sub("b.txt"), Bom.Concat(Encoding.UTF8.GetBytes("one\r\ntwo\r\n")).ToArray());
        var r = await Box().ExecuteAsync(Call("write_file", new { path = "b.txt", content = "uno\ndos\n" }), default);
        Assert.False(r.IsError, r.Content);
        var bytes = File.ReadAllBytes(_tmp.Sub("b.txt"));
        Assert.Equal(Bom, bytes[..3]);
        Assert.Equal("uno\r\ndos\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Lf_files_and_new_files_stay_lf_without_bom()
    {
        File.WriteAllText(_tmp.Sub("c.txt"), "one\ntwo\n");
        await Box().ExecuteAsync(Call("edit_file", new { path = "c.txt", old_text = "two", new_text = "three" }), default);
        Assert.Equal("one\nthree\n"u8.ToArray(), File.ReadAllBytes(_tmp.Sub("c.txt")));
        await Box().ExecuteAsync(Call("write_file", new { path = "d.txt", content = "x\ny\n" }), default);
        Assert.Equal("x\ny\n"u8.ToArray(), File.ReadAllBytes(_tmp.Sub("d.txt")));
    }
}
