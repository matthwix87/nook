using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nook.Core.Api;

namespace Nook.App.ViewModels;

public partial class ConversationItem : ObservableObject
{
    public ConversationItem(ConversationSummary s)
    {
        Id = s.Id;
        _title = s.Title;
        _pinned = s.Pinned != 0;
        ProjectId = s.ProjectId;
        Snippet = s.Snippet;
    }

    public long Id { get; }
    public long? ProjectId { get; }
    public string? Snippet { get; }
    public bool HasSnippet => !string.IsNullOrEmpty(Snippet);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    bool _pinned;
    [ObservableProperty] string _title;
    public string PinLabel => Pinned ? "Unpin" : "Pin";
}

public sealed class ProjectItem(long? id, string name)
{
    public long? Id { get; } = id;
    public string Name { get; } = name;
}

public partial class MessageItem : ObservableObject
{
    public MessageItem(string role, string text = "")
    {
        Role = role;
        _text = text;
    }

    public string Role { get; }
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    string _text;
    public bool HasText => Text.Length > 0;
    /// <summary>"Thinking…" / "Waking up Nook…" until the first token arrives.</summary>
    [ObservableProperty] bool _isPending;
    [ObservableProperty] string _statusText = "Thinking…";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    string _hint = "";
    public bool HasHint => Hint.Length > 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeta))]
    string _meta = "";
    public bool HasMeta => Meta.Length > 0;
    [ObservableProperty] bool _isError;
    public ObservableCollection<string> Notices { get; } = new();
}

/// <summary>The website's chat: list, search, rename/pin/delete, projects, streaming replies.</summary>
public partial class ChatViewModel : ObservableObject
{
    readonly MainViewModel _main;
    CancellationTokenSource? _streamCts;
    CancellationTokenSource? _searchCts;
    DateTime _lastWarm = DateTime.MinValue;

    public ChatViewModel(MainViewModel main)
    {
        _main = main;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    NookApi Api => _main.Api;

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<ProjectItem> Projects { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();

    [ObservableProperty] ConversationItem? _selectedConversation;
    [ObservableProperty] ProjectItem? _selectedProject;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    long? _conversationId;
    [ObservableProperty] string _title = "New chat";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    string _input = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(IsNotStreaming))]
    bool _isStreaming;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] bool _hasNoChats;
    public bool IsNotStreaming => !IsStreaming;
    public bool HasProjects => Projects.Count > 1;
    public bool CanSend => !IsStreaming && Input.Trim().Length > 0;
    public bool IsEmpty => Messages.Count == 0;
    public string Greeting => DateTime.Now.Hour switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" }
                              + (_main.Username.Length > 0 ? $", {_main.Username}" : "");
    public string ProjectTag => SelectedProject?.Id != null ? SelectedProject.Name : "";
    public bool HasProjectTag => ProjectTag.Length > 0;

    /// <summary>Raised when the message list should scroll to the bottom.</summary>
    public event Action? ScrollRequested;

    public void Clear()
    {
        _streamCts?.Cancel();
        Conversations.Clear();
        Projects.Clear();
        Messages.Clear();
        ConversationId = null;
        Title = "New chat";
        Input = "";
    }

    public async Task LoadAsync()
    {
        OnPropertyChanged(nameof(Greeting));
        await Task.WhenAll(LoadConversationsAsync(), LoadProjectsAsync());
        NewChat();
    }

    public async Task LoadConversationsAsync()
    {
        try
        {
            var list = await Api.ConversationsAsync(SearchText);
            if (SelectedProject?.Id is { } pid) list = list.Where(c => c.ProjectId == pid).ToList();
            Conversations.Clear();
            foreach (var c in list) Conversations.Add(new ConversationItem(c));
            HasNoChats = Conversations.Count == 0;
            if (ConversationId is { } id) SetSelectedQuietly(Conversations.FirstOrDefault(c => c.Id == id));
        }
        catch (ApiException e) { _main.HandleError(e); }
    }

    async Task LoadProjectsAsync()
    {
        try
        {
            var list = await Api.ProjectsAsync();
            Projects.Clear();
            Projects.Add(new ProjectItem(null, "All chats"));
            foreach (var p in list) Projects.Add(new ProjectItem(p.Id, p.Name));
#pragma warning disable MVVMTK0034 // set without the change handler (no reload loop)
            _selectedProject = Projects[0];
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(SelectedProject));
            OnPropertyChanged(nameof(HasProjects));
        }
        catch (ApiException e) { _main.HandleError(e); }
    }

    bool _quiet;
    void SetSelectedQuietly(ConversationItem? item)
    {
        _quiet = true;
        SelectedConversation = item;
        _quiet = false;
    }

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        if (_quiet || value == null) return;
        _main.ShowChat();
        _ = OpenAsync(value.Id);
    }

    partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        OnPropertyChanged(nameof(ProjectTag));
        OnPropertyChanged(nameof(HasProjectTag));
        NewChat();
        _ = LoadConversationsAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(async t =>
        {
            if (!t.IsCanceled) await LoadConversationsAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    partial void OnInputChanged(string value)
    {
        // Warm-up while typing, at most every 5 minutes (like the website).
        if (value.Length > 0 && DateTime.UtcNow - _lastWarm > TimeSpan.FromMinutes(5))
        {
            _lastWarm = DateTime.UtcNow;
            _ = Api.WarmAsync();
        }
    }

    async Task OpenAsync(long id)
    {
        if (IsStreaming) _streamCts?.Cancel();
        try
        {
            var c = await Api.ConversationAsync(id);
            ConversationId = c.Id;
            Title = c.Title;
            Messages.Clear();
            foreach (var m in c.Messages)
            {
                var item = new MessageItem(m.Role, m.Content);
                if (m.Attachments.Count > 0)
                    item.Notices.Add("Attached: " + string.Join(", ", m.Attachments.Select(a => a.Name)));
                Messages.Add(item);
            }
            OnPropertyChanged(nameof(IsEmpty));
            ScrollRequested?.Invoke();
        }
        catch (ApiException e) { _main.HandleError(e); }
    }

    [RelayCommand]
    public void NewChat()
    {
        if (IsStreaming) _streamCts?.Cancel();
        _main.ShowChat();
        ConversationId = null;
        Title = "New chat";
        Messages.Clear();
        SetSelectedQuietly(null);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Greeting));
    }

    [RelayCommand]
    void UseSuggestion(string text) => Input = text;

    [RelayCommand]
    void Stop() => _streamCts?.Cancel();

    [RelayCommand]
    async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsStreaming) return;
        var user = new MessageItem("user", text);
        var reply = new MessageItem("assistant") { IsPending = true };
        Messages.Add(user);
        Messages.Add(reply);
        OnPropertyChanged(nameof(IsEmpty));
        Input = "";
        IsStreaming = true;
        ScrollRequested?.Invoke();
        var cts = _streamCts = new CancellationTokenSource();
        var hint = Task.Delay(20000, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && reply.IsPending) reply.Hint = "The model is loading, this can take up to a minute.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
        var isNew = ConversationId == null;
        bool stopped = false;
        try
        {
            await foreach (var ev in Api.ChatAsync(text, ConversationId, SelectedProject?.Id, cts.Token))
            {
                switch (ev.Type)
                {
                    case "meta" when ev.ConversationId is { } cid:
                        ConversationId = cid;
                        if (isNew) Title = text.Length <= 50 ? text : text[..50];
                        break;
                    case "status":
                        reply.StatusText = ev.State == "starting" ? "Waking up Nook…" : "Thinking…";
                        break;
                    case "notice":
                        reply.Notices.Add(ev.Message ?? "");
                        break;
                    case "delta":
                        reply.IsPending = false;
                        reply.Hint = "";
                        reply.Text += ev.Text;
                        ScrollRequested?.Invoke();
                        break;
                    case "done":
                        if (ev.Usage != null) reply.Meta = $"{ev.Usage.Total:N0} tokens";
                        _main.UpdateMeter(ev.Budget);
                        break;
                    case "error":
                        reply.IsPending = false;
                        reply.IsError = true;
                        reply.Text = ev.Message ?? "Something went wrong.";
                        if (ev.Error == "offline") _ = _main.CheckStatusAsync();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            reply.IsPending = false;
            reply.Meta = "Stopped";
            if (reply.Text.Length == 0) reply.Text = "Stopped before any reply.";
        }
        catch (ApiException e)
        {
            reply.IsPending = false;
            if (e.IsOffline || e.Code is "disabled" or "bad_login")
            {
                Messages.Remove(reply);
                if (reply.Text.Length == 0) { Messages.Remove(user); Input = text; }
                if (e.IsOffline && !await _main.CheckStatusAsync()) return;
                if (!e.IsOffline) { _main.HandleError(e); return; }
                _main.Toast("Connection lost - try again.");
            }
            else
            {
                // Refused before anything was saved (too long, out of tokens, Nook off...): give the text back.
                Messages.Remove(user);
                Input = text;
                reply.IsError = true;
                reply.Text = e.Message;
            }
        }
        finally
        {
            cts.Cancel();
            IsStreaming = false;
            OnPropertyChanged(nameof(IsEmpty));
            await LoadConversationsAsync();
            if (stopped) _ = Task.Delay(1000).ContinueWith(_ => _main.RefreshMeAsync(), TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    [RelayCommand]
    async Task RenameAsync(ConversationItem? c)
    {
        if (c == null) return;
        var title = await _main.PromptAsync("Rename chat", "", c.Title, "Rename");
        if (string.IsNullOrWhiteSpace(title)) return;
        try
        {
            await Api.EditConversationAsync(c.Id, new { title = title.Trim() });
            c.Title = title.Trim();
            if (c.Id == ConversationId) Title = c.Title;
        }
        catch (ApiException e) { _main.HandleError(e); }
    }

    [RelayCommand]
    async Task TogglePinAsync(ConversationItem? c)
    {
        if (c == null) return;
        try
        {
            await Api.EditConversationAsync(c.Id, new { pinned = !c.Pinned });
            await LoadConversationsAsync();
        }
        catch (ApiException e) { _main.HandleError(e); }
    }

    [RelayCommand]
    async Task DeleteAsync(ConversationItem? c)
    {
        if (c == null) return;
        var ok = await _main.PromptAsync("Delete chat?", $"\"{c.Title}\" will be deleted for good.", null, "Delete", danger: true);
        if (ok == null) return;
        try
        {
            await Api.DeleteConversationAsync(c.Id);
            if (c.Id == ConversationId) NewChat();
            await LoadConversationsAsync();
        }
        catch (ApiException e) { _main.HandleError(e); }
    }
}
