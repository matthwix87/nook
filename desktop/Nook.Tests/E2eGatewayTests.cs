using Nook.Core.Agent;
using Nook.Core.Api;
using Nook.Core.Platform;

namespace Nook.Tests;

/// <summary>
/// Runs the real agent loop against a real gateway (server/server.py + tests/fake_ollama.py).
/// Only active when NOOK_E2E_URL / NOOK_E2E_USER / NOOK_E2E_KEY are set; run-e2e.sh sets them up.
/// </summary>
public class E2eGatewayTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Agent_loop_against_real_gateway()
    {
        var url = Environment.GetEnvironmentVariable("NOOK_E2E_URL");
        if (string.IsNullOrEmpty(url)) return; // not configured: nothing to do
        var api = new NookApi { BaseUrl = url };
        api.SetAuth(Environment.GetEnvironmentVariable("NOOK_E2E_USER"), Environment.GetEnvironmentVariable("NOOK_E2E_KEY"));
        Assert.True((await api.StatusAsync()).Backend);
        await api.MeAsync();

        File.WriteAllText(_tmp.Sub("README.md"), "HELLO_FROM_README");
        var platform = new UnixPlatform(_tmp.Sub("cfg"));
        var session = new AgentSession();
        var box = new ToolBox(new Workspace(_tmp.Path), session, new FakeApprover(false), platform);
        var obs = new RecordingObserver();
        var loop = new AgentLoop(new NookAgentGateway(api), box, obs, platform);
        // fake_ollama: "TOOL" -> read_file README.md; then answers "done: <first 20 chars of the result>".
        var outcome = await loop.RunAsync("TOOL read the readme", default);
        Assert.Equal(AgentOutcomeKind.Answered, outcome.Kind);
        Assert.Contains("tool:read_file", obs.Log);
        Assert.StartsWith("done: README.md", outcome.Message);
        Assert.Contains(session.Activity, a => a.Text == "Read README.md");
    }
}
