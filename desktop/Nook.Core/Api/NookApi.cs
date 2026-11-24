using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Nook.Core.Api;

/// <summary>HTTP client for the Nook gateway (see server/server.py). Auth: "Bearer username:key".</summary>
public sealed class NookApi
{
    public const string BackendJsonUrl = "https://matthwix87.github.io/nook/backend.json";
    public const string LocalFallback = "http://127.0.0.1:8787";

    readonly HttpClient _http;

    public NookApi(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string BaseUrl { get; set; } = LocalFallback;
    public string? Username { get; private set; }
    public string? Key { get; private set; }

    public void SetAuth(string? username, string? key)
    {
        Username = username;
        Key = key;
    }

    /// <summary>Backend URL: the manual override if set, otherwise backend.json (cache-busted), else localhost.</summary>
    public async Task<string> ResolveBackendAsync(string? overrideUrl, string backendJsonUrl = BackendJsonUrl,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(overrideUrl)) return BaseUrl = overrideUrl.Trim().TrimEnd('/');
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var sep = backendJsonUrl.Contains('?') ? "&" : "?";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{backendJsonUrl}{sep}t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var res = await _http.SendAsync(req, cts.Token);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
                if (doc.RootElement.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
                    return BaseUrl = url.TrimEnd('/');
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return BaseUrl = LocalFallback;
    }

    HttpRequestMessage Build(HttpMethod method, string path, object? body)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (Username != null && Key != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{Username}:{Key}");
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json");
        return req;
    }

    static async Task<ApiException> ErrorFrom(HttpResponseMessage res, CancellationToken ct)
    {
        string text = "";
        try { text = await res.Content.ReadAsStringAsync(ct); } catch { }
        try
        {
            var el = JsonDocument.Parse(text).RootElement.Clone();
            var code = el.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            var msg = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return new ApiException((int)res.StatusCode, code, msg.Length > 0 ? msg : $"Request failed ({(int)res.StatusCode})", el);
        }
        catch
        {
            // Not our JSON (e.g. the tunnel's own error page when the host is down).
            var code = (int)res.StatusCode >= 500 ? "offline" : "http";
            return new ApiException((int)res.StatusCode, code, code == "offline" ? "Can't reach the server" : $"Request failed ({(int)res.StatusCode})");
        }
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default,
        int timeoutSeconds = 20)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        HttpResponseMessage res;
        try
        {
            using var req = Build(method, path, body);
            res = await _http.SendAsync(req, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ApiException(0, "offline", "Can't reach the server");
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw await ErrorFrom(res, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(text, Json.Options)!;
        }
    }

    /// <summary>POST that answers with an SSE stream. Errors before the stream throw ApiException.</summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(string path, object body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            using var req = Build(HttpMethod.Post, path, body);
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new ApiException(0, "offline", "Can't reach the server");
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw await ErrorFrom(res, ct);
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    throw new ApiException(0, "offline", "Connection lost");
                }
                if (line == null) yield break;
                if (!line.StartsWith("data: ")) continue;
                StreamEvent? ev;
                try { ev = JsonSerializer.Deserialize<StreamEvent>(line.AsSpan(6), Json.Options); }
                catch (JsonException) { continue; }
                if (ev != null) yield return ev;
            }
        }
    }

    // ---- endpoints
    public Task<Status> StatusAsync(CancellationToken ct = default) => SendAsync<Status>(HttpMethod.Get, "/status", ct: ct, timeoutSeconds: 8);
    public Task<Account> MeAsync(CancellationToken ct = default) => SendAsync<Account>(HttpMethod.Get, "/api/me", ct: ct);

    public Task<List<ConversationSummary>> ConversationsAsync(string? search = null, CancellationToken ct = default) =>
        SendAsync<List<ConversationSummary>>(HttpMethod.Get,
            "/api/conversations" + (string.IsNullOrWhiteSpace(search) ? "" : "?q=" + Uri.EscapeDataString(search.Trim())), ct: ct);

    public Task<Conversation> ConversationAsync(long id, CancellationToken ct = default) =>
        SendAsync<Conversation>(HttpMethod.Get, $"/api/conversations/{id}", ct: ct);

    public Task<JsonElement> EditConversationAsync(long id, object changes, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"/api/conversations/{id}", changes, ct);

    public Task<JsonElement> DeleteConversationAsync(long id, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"/api/conversations/{id}", ct: ct);

    public Task<List<Project>> ProjectsAsync(CancellationToken ct = default) =>
        SendAsync<List<Project>>(HttpMethod.Get, "/api/projects", ct: ct);

    public Task<UserSettings> SettingsAsync(CancellationToken ct = default) =>
        SendAsync<UserSettings>(HttpMethod.Get, "/api/settings", ct: ct);

    public Task<UserSettings> SaveInstructionsAsync(string instructions, CancellationToken ct = default) =>
        SendAsync<UserSettings>(HttpMethod.Patch, "/api/settings", new { instructions }, ct);

    public async Task WarmAsync(CancellationToken ct = default)
    {
        try { await SendAsync<JsonElement>(HttpMethod.Post, "/api/warm", ct: ct, timeoutSeconds: 10); }
        catch (ApiException) { /* best effort, like the website */ }
    }

    /// <summary>New chat message. <paramref name="conversationId"/> null starts a new chat (optionally in a project).</summary>
    public IAsyncEnumerable<StreamEvent> ChatAsync(string message, long? conversationId, long? projectId, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["message"] = message, ["conversation_id"] = conversationId };
        if (conversationId == null && projectId != null) body["project_id"] = projectId;
        return StreamAsync("/api/chat", body, ct);
    }

    public IAsyncEnumerable<StreamEvent> AgentAsync(object messages, object tools, CancellationToken ct) =>
        StreamAsync("/api/agent", new { messages, tools }, ct);
}
