using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Nook.Core.Agent;
using Nook.Core.Api;

namespace Nook.Tests;

/// <summary>A tiny HTTP server answering with canned responses, standing in for the gateway (and backend.json).</summary>
public sealed class FakeServer : IDisposable
{
    readonly HttpListener _listener = new();
    public string Url { get; }
    public List<(string Method, string Path, string Auth, string Body)> Requests { get; } = new();
    public Func<HttpListenerRequest, string, (int Status, string ContentType, string Body)> Handler { get; set; } =
        (_, _) => (404, "application/json", "{\"error\":\"not_found\",\"message\":\"Not found\"}");

    public FakeServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
            lock (Requests) Requests.Add((ctx.Request.HttpMethod, ctx.Request.Url!.PathAndQuery, ctx.Request.Headers["Authorization"] ?? "", body));
            var (status, type, text) = Handler(ctx.Request, body);
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

public class ApiTests
{
    static string Sse(params string[] events) => string.Concat(events.Select(e => $"data: {e}\n\n"));

    [Fact]
    public async Task Resolves_backend_from_backend_json_with_cache_buster()
    {
        using var srv = new FakeServer { Handler = (_, _) => (200, "application/json", "{\"url\": \"https://x.trycloudflare.com/\"}") };
        var api = new NookApi();
        var url = await api.ResolveBackendAsync(null, srv.Url + "/backend.json");
        Assert.Equal("https://x.trycloudflare.com", url);
        Assert.Matches(@"^/backend\.json\?t=\d+$", srv.Requests[0].Path);
    }

    [Fact]
    public async Task Override_wins_and_failure_falls_back_to_localhost()
    {
        var api = new NookApi();
        Assert.Equal("http://my.host:1", await api.ResolveBackendAsync(" http://my.host:1/ "));
        using var srv = new FakeServer { Handler = (_, _) => (500, "text/plain", "boom") };
        Assert.Equal(NookApi.LocalFallback, await api.ResolveBackendAsync("", srv.Url + "/backend.json"));
    }

    [Fact]
    public async Task Sends_bearer_auth_and_parses_errors()
    {
        using var srv = new FakeServer
        {
            Handler = (_, _) => (403, "application/json", "{\"error\":\"disabled\",\"message\":\"This account is temporarily disabled.\",\"until\":123}"),
        };
        var api = new NookApi { BaseUrl = srv.Url };
        api.SetAuth("alice", "k3y");
        var e = await Assert.ThrowsAsync<ApiException>(() => api.MeAsync());
        Assert.Equal("disabled", e.Code);
        Assert.Equal(123, e.Number("until"));
        Assert.Equal("Bearer alice:k3y", srv.Requests[0].Auth);
    }

    [Fact]
    public async Task Unreachable_server_is_offline()
    {
        var api = new NookApi { BaseUrl = "http://127.0.0.1:1" };
        var e = await Assert.ThrowsAsync<ApiException>(() => api.StatusAsync());
        Assert.True(e.IsOffline);
    }

    [Fact]
    public async Task Agent_gateway_streams_text_and_tool_calls()
    {
        using var srv = new FakeServer
        {
            Handler = (req, body) => req.Url!.AbsolutePath == "/api/agent"
                ? (200, "text/event-stream", Sse(
                    "{\"type\":\"status\",\"state\":\"starting\"}",
                    "{\"type\":\"status\",\"state\":\"ready\"}",
                    "{\"type\":\"notice\",\"message\":\"Older steps were left out\"}",
                    "{\"type\":\"delta\",\"text\":\"Look\"}",
                    "{\"type\":\"delta\",\"text\":\"ing.\"}",
                    "{\"type\":\"tool_calls\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}}}]}",
                    "{\"type\":\"done\",\"usage\":{\"prompt\":10,\"completion\":5,\"total\":15},\"budget\":{\"unlimited\":true},\"trimmed\":2}"))
                : (404, "application/json", "{}"),
        };
        var api = new NookApi { BaseUrl = srv.Url };
        api.SetAuth("bob", "key");
        var obs = new CollectingObserver();
        var msgs = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" });
        var turn = await new NookAgentGateway(api).SendAsync(msgs, ToolBox.Definitions(), obs, default);
        Assert.Equal("Looking.", turn.Content);
        Assert.Single(turn.ToolCalls);
        Assert.Equal(15, turn.Usage!.Total);
        Assert.True(turn.Budget!.Unlimited);
        Assert.Equal(new[] { "starting", "ready" }, obs.States);
        Assert.Single(obs.Notices);
        // Request body: messages + tool schemas.
        var sent = JsonNode.Parse(srv.Requests[0].Body)!;
        Assert.Equal("hi", sent["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(6, sent["tools"]!.AsArray().Count);
        Assert.Equal("Bearer bob:key", srv.Requests[0].Auth);
    }

    [Fact]
    public async Task Agent_gateway_surfaces_http_and_stream_errors()
    {
        using var srv = new FakeServer
        {
            Handler = (_, _) => (429, "application/json", "{\"error\":\"rate_limited\",\"message\":\"Slow down\",\"retry_after\":12}"),
        };
        var api = new NookApi { BaseUrl = srv.Url };
        var gw = new NookAgentGateway(api);
        var e = await Assert.ThrowsAsync<ApiException>(() => gw.SendAsync(new JsonArray(), new JsonArray(), new CollectingObserver(), default));
        Assert.Equal("rate_limited", e.Code);
        Assert.Equal(12, e.Number("retry_after"));

        srv.Handler = (_, _) => (200, "text/event-stream", Sse("{\"type\":\"error\",\"error\":\"offline\",\"message\":\"The model is not responding right now.\"}"));
        e = await Assert.ThrowsAsync<ApiException>(() => gw.SendAsync(new JsonArray(), new JsonArray(), new CollectingObserver(), default));
        Assert.True(e.IsOffline);

        srv.Handler = (_, _) => (200, "text/event-stream", Sse("{\"type\":\"delta\",\"text\":\"half\"}"));
        e = await Assert.ThrowsAsync<ApiException>(() => gw.SendAsync(new JsonArray(), new JsonArray(), new CollectingObserver(), default));
        Assert.Contains("Connection lost", e.Message);
    }

    [Fact]
    public async Task Chat_stream_events_are_parsed()
    {
        using var srv = new FakeServer
        {
            Handler = (_, _) => (200, "text/event-stream", Sse(
                "{\"type\":\"meta\",\"conversation_id\":7,\"user_message_id\":3}",
                "{\"type\":\"delta\",\"text\":\"hi\"}",
                "{\"type\":\"done\",\"assistant_message_id\":4,\"usage\":{\"prompt\":1,\"completion\":1,\"total\":2},\"budget\":{\"unlimited\":false,\"daily_left\":5,\"daily_allowance\":10}}")),
        };
        var api = new NookApi { BaseUrl = srv.Url };
        var events = new List<StreamEvent>();
        await foreach (var ev in api.ChatAsync("hello", null, 2, default)) events.Add(ev);
        Assert.Equal(7, events[0].ConversationId);
        Assert.Equal("hi", events[1].Text);
        Assert.Equal(5, events[2].Budget!.DailyLeft);
        Assert.Contains("\"project_id\":2", srv.Requests[0].Body);
    }

    sealed class CollectingObserver : IAgentObserver
    {
        public List<string> States { get; } = new();
        public List<string> Notices { get; } = new();
        public void OnStatus(string state) => States.Add(state);
        public void OnNotice(string message) => Notices.Add(message);
    }
}
