using Nook.Core.Agent;

namespace Nook.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("nook-test-").FullName;
    public string Sub(string rel) => System.IO.Path.Combine(Path, rel);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class WorkspaceTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly string _root, _outside;
    readonly Workspace _ws;

    public WorkspaceTests()
    {
        _root = _tmp.Sub("ws");
        _outside = _tmp.Sub("outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(System.IO.Path.Combine(_outside, "secret.txt"), "secret");
        Directory.CreateDirectory(System.IO.Path.Combine(_root, "src"));
        File.WriteAllText(System.IO.Path.Combine(_root, "src", "a.txt"), "hello");
        _ws = new Workspace(_root);
    }

    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("src/a.txt")]
    [InlineData("./src/../src/a.txt")]
    [InlineData("new/dir/file.txt")]
    [InlineData(".")]
    public void Resolves_paths_inside(string rel)
    {
        Assert.True(_ws.Contains(_ws.Resolve(rel)));
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("src/../../outside/secret.txt")]
    [InlineData("..")]
    public void Blocks_dotdot_escape(string rel)
    {
        var e = Assert.Throws<ToolException>(() => _ws.Resolve(rel));
        Assert.Contains("outside the workspace", e.Message);
    }

    [Fact]
    public void Blocks_absolute_path_outside()
    {
        Assert.Throws<ToolException>(() => _ws.Resolve(System.IO.Path.Combine(_outside, "secret.txt")));
        Assert.Throws<ToolException>(() => _ws.Resolve("/etc/passwd"));
    }

    [Fact]
    public void Allows_absolute_path_inside()
    {
        Assert.EndsWith("a.txt", _ws.Resolve(System.IO.Path.Combine(_root, "src", "a.txt")));
    }

    [Fact]
    public void Blocks_symlinked_directory_escape()
    {
        Directory.CreateSymbolicLink(System.IO.Path.Combine(_root, "link"), _outside);
        Assert.Throws<ToolException>(() => _ws.Resolve("link/secret.txt"));
        Assert.Throws<ToolException>(() => _ws.Resolve("link"));
    }

    [Fact]
    public void Blocks_symlinked_file_escape()
    {
        File.CreateSymbolicLink(System.IO.Path.Combine(_root, "s.txt"), System.IO.Path.Combine(_outside, "secret.txt"));
        Assert.Throws<ToolException>(() => _ws.Resolve("s.txt"));
    }

    [Fact]
    public void Blocks_broken_symlink_pointing_outside()
    {
        // Writing through a dangling link would create the file outside.
        File.CreateSymbolicLink(System.IO.Path.Combine(_root, "dangling.txt"), System.IO.Path.Combine(_outside, "new.txt"));
        Assert.Throws<ToolException>(() => _ws.Resolve("dangling.txt"));
    }

    [Fact]
    public void Blocks_relative_symlink_chain_escape()
    {
        Directory.CreateSymbolicLink(System.IO.Path.Combine(_root, "src", "up"), "../..");
        Assert.Throws<ToolException>(() => _ws.Resolve("src/up/outside/secret.txt"));
    }

    [Fact]
    public void Allows_symlink_that_stays_inside()
    {
        Directory.CreateSymbolicLink(System.IO.Path.Combine(_root, "alias"), System.IO.Path.Combine(_root, "src"));
        Assert.EndsWith(System.IO.Path.Combine("src", "a.txt"), _ws.Resolve("alias/a.txt"));
    }

    [Fact]
    public void Workspace_root_given_through_a_symlink_works()
    {
        var link = _tmp.Sub("wslink");
        Directory.CreateSymbolicLink(link, _root);
        var ws = new Workspace(link);
        Assert.Equal(_ws.Root, ws.Root);
        Assert.True(ws.Contains(ws.Resolve("src/a.txt")));
    }

    [Fact]
    public void Prefix_sibling_is_not_inside()
    {
        Directory.CreateDirectory(_root + "2");
        Assert.Throws<ToolException>(() => _ws.Resolve(_root + "2/x.txt"));
    }
}
