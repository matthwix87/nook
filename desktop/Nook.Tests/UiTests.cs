using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Nook.App;
using Nook.App.ViewModels;
using Nook.App.Views;
using Nook.Core;
using Nook.Core.Agent;
using Nook.Core.Api;
using Nook.Core.Platform;

[assembly: AvaloniaTestApplication(typeof(Nook.Tests.TestAppBuilder))]

namespace Nook.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Nook.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Headless UI tests. Screens are also rendered to desktop/screenshots/*.png for a visual check.</summary>
public class UiTests : IDisposable
{
    readonly TempDir _tmp = new();
    static readonly string ShotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../screenshots"));

    public void Dispose() => _tmp.Dispose();

    MainViewModel Vm(string? baseUrl = null)
    {
        var platform = new UnixPlatform(_tmp.Sub("config"));
        var api = new NookApi { BaseUrl = baseUrl ?? "http://127.0.0.1:1" };
        return new MainViewModel(new AppServices(platform, api, new AppSettings { BackendOverride = api.BaseUrl }) { BackendJsonUrl = "http://127.0.0.1:1/backend.json" });
    }

    static MainWindow Show(MainViewModel vm, string theme = "light", int w = 1180, int h = 760)
    {
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var win = new MainWindow { DataContext = vm, Width = w, Height = h };
        win.Show();
        Pump();
        return win;
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    static void Shot(MainWindow win, string name)
    {
        Pump();
        Directory.CreateDirectory(ShotDir);
        using var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    static void FillChat(MainViewModel vm)
    {
        vm.Username = "matt";
        vm.UpdateMeter(new Budget { DailyAllowance = 20000, DailyLeft = 14250, Topup = 5000, TotalLeft = 19250, ResetsAt = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSeconds() });
        vm.Screen = "main";
        vm.Announcement = "Nook gets a new model on Friday evening.";
        foreach (var (t, p) in new[] { ("Python list tricks", true), ("Birthday message for Jas", false), ("Explain DNS like I'm 5", false) })
            vm.Chat.Conversations.Add(new ConversationItem(new ConversationSummary { Id = t.Length, Title = t, Pinned = p ? 1 : 0 }));
        vm.Chat.Title = "Python list tricks";
        vm.Chat.ConversationId = 18;
        vm.Chat.Messages.Add(new MessageItem("user", "How do I reverse a list in Python?"));
        vm.Chat.Messages.Add(new MessageItem("assistant",
            "There are a few ways. The **simplest** is slicing:\n\n```python\nitems = [1, 2, 3]\nprint(items[::-1])  # [3, 2, 1]\n```\n\n- `items.reverse()` reverses in place\n- `reversed(items)` gives an iterator")
        { Meta = "182 tokens" });
        vm.Chat.Messages.Add(new MessageItem("user", "And a dict?"));
        vm.Chat.Messages.Add(new MessageItem("assistant") { IsPending = true, StatusText = "Waking up Nook…" });
    }

    [AvaloniaFact]
    public void Login_screen()
    {
        var vm = Vm();
        vm.Screen = "login";
        vm.LoginUsername = "matt";
        vm.LoginError = "Wrong username or key";
        var win = Show(vm);
        Shot(win, "01-login-light");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Shot(win, "02-login-dark");
    }

    [AvaloniaFact]
    public void Offline_screen()
    {
        var vm = Vm();
        vm.ShowOffline();
        Assert.Equal(MainViewModel.OfflineText, vm.OfflineDetail);
        Shot(Show(vm), "03-offline");
    }

    [AvaloniaFact]
    public async Task Boot_with_unreachable_host_shows_offline()
    {
        var vm = Vm();
        await vm.BootAsync();
        Assert.True(vm.IsOffline);
        Assert.Contains("offline temporarily", vm.OfflineDetail);
    }

    [AvaloniaFact]
    public void Chat_screen_light_and_dark()
    {
        var vm = Vm();
        FillChat(vm);
        var win = Show(vm);
        Shot(win, "04-chat-light");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        vm.BannerText = "Maintenance mode is on: only you (the owner) can chat.";
        Shot(win, "05-chat-dark");
    }

    [AvaloniaFact]
    public void Empty_chat_and_settings()
    {
        var vm = Vm();
        vm.Username = "matt";
        vm.Screen = "main";
        vm.UpdateMeter(new Budget { Unlimited = true });
        vm.IsSettingsOpen = true;
        vm.Instructions = "I'm learning C#. Keep answers short.";
        Shot(Show(vm), "06-settings");
    }

    ApprovalRequest EditRequest()
    {
        var before = "def greet(name):\n    print('hi ' + name)\n\n\ndef main():\n    greet('world')\n";
        var after = "def greet(name, loud=False):\n    msg = f'hi {name}'\n    print(msg.upper() if loud else msg)\n\n\ndef main():\n    greet('world')\n";
        return new ApprovalRequest(ApprovalKind.EditFile, "app/main.py", _tmp.Path, null, Diff.Compute(before, after), false);
    }

    [AvaloniaFact]
    public void Coding_screen_with_diff_approval()
    {
        var vm = Vm();
        FillChat(vm);
        vm.ShowCode();
        vm.Coding.OpenForTest(_tmp.Path);
        var c = vm.Coding;
        c.Items.Add(new CodingItem("user", "Add a loud option to greet()"));
        c.Items.Add(new CodingItem("tool", "", "read_file: app/main.py") { Detail = "app/main.py (6 lines)\ndef greet(name): ..." });
        c.Items.Add(new CodingItem("assistant", "I'll add a `loud` parameter that upper-cases the greeting."));
        c.Activity.Insert(0, new ActivityItem(new ActivityEntry(DateTime.Now, ActivityKind.Read, "Listed ./")));
        c.Activity.Insert(0, new ActivityItem(new ActivityEntry(DateTime.Now, ActivityKind.Read, "Read app/main.py")));
        c.Activity.Insert(0, new ActivityItem(new ActivityEntry(DateTime.Now, ActivityKind.Rejected, "Rejected command: rm -rf build")));
        c.IsRunning = true;
        c.StatusText = "Waiting for your approval…";
        c.ShowApprovalForTest(EditRequest());
        var win = Show(vm);
        Shot(win, "07-coding-diff-light");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Shot(win, "08-coding-diff-dark");
    }

    [AvaloniaFact]
    public void Coding_screen_command_approval_and_approve_all_banner()
    {
        var vm = Vm();
        FillChat(vm);
        vm.ShowCode();
        vm.Coding.OpenForTest(_tmp.Path);
        vm.Coding.ApproveAll = true;
        vm.Coding.ShowApprovalForTest(new ApprovalRequest(ApprovalKind.RunCommand, ".", _tmp.Path, "python3 -m pytest -q", Array.Empty<DiffLine>(), false));
        Shot(Show(vm), "09-coding-command");
    }

    [AvaloniaFact]
    public void Coding_screen_without_workspace()
    {
        var vm = Vm();
        FillChat(vm);
        vm.ShowCode();
        Shot(Show(vm), "10-coding-pick-folder");
    }

    [AvaloniaFact]
    public async Task Approval_card_gates_the_tool()
    {
        var vm = Vm();
        vm.Coding.OpenForTest(_tmp.Path);
        var req = EditRequest();
        var pending = ((IApprover)vm.Coding).RequestAsync(req, default);
        Pump();
        Assert.True(vm.Coding.HasApproval);
        Assert.False(pending.IsCompleted);
        vm.Coding.PendingApproval!.RejectNote = "no thanks";
        vm.Coding.PendingApproval.RejectCommand.Execute(null);
        var d = await pending;
        Pump();
        Assert.False(d.Approved);
        Assert.Equal("no thanks", d.Reason);
        Assert.False(vm.Coding.HasApproval);

        pending = ((IApprover)vm.Coding).RequestAsync(req, default);
        Pump();
        vm.Coding.PendingApproval!.ApproveCommand.Execute(null);
        Assert.True((await pending).Approved);
    }

    [AvaloniaFact]
    public void Approve_all_is_off_by_default_and_reset_on_logout()
    {
        var vm = Vm();
        Assert.False(vm.Coding.ApproveAll);
        vm.Coding.ApproveAll = true;
        vm.DoLogout("");
        Assert.False(vm.Coding.ApproveAll);
    }

    [AvaloniaFact]
    public async Task Chat_streams_from_the_gateway()
    {
        using var srv = new FakeServer
        {
            Handler = (req, _) => req.Url!.AbsolutePath switch
            {
                "/api/chat" => (200, "text/event-stream",
                    "data: {\"type\":\"meta\",\"conversation_id\":5,\"user_message_id\":1}\n\n" +
                    "data: {\"type\":\"status\",\"state\":\"ready\"}\n\n" +
                    "data: {\"type\":\"delta\",\"text\":\"Hello \"}\n\n" +
                    "data: {\"type\":\"delta\",\"text\":\"there\"}\n\n" +
                    "data: {\"type\":\"done\",\"usage\":{\"prompt\":3,\"completion\":2,\"total\":5},\"budget\":{\"unlimited\":true}}\n\n"),
                "/api/conversations" => (200, "application/json", "[{\"id\":5,\"title\":\"hi\",\"pinned\":0}]"),
                _ => (200, "application/json", "{}"),
            },
        };
        var vm = Vm(srv.Url);
        vm.Chat.Input = "hi";
        await vm.Chat.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Chat.Messages.Count);
        Assert.Equal("Hello there", vm.Chat.Messages[1].Text);
        Assert.Equal("5 tokens", vm.Chat.Messages[1].Meta);
        Assert.Equal(5, vm.Chat.ConversationId);
        Assert.Single(vm.Chat.Conversations);
        Assert.Equal("Unlimited tokens", vm.MeterLabel);
    }

    [AvaloniaFact]
    public async Task Chat_refusal_gives_the_text_back()
    {
        using var srv = new FakeServer
        {
            Handler = (req, _) => req.Url!.AbsolutePath == "/api/chat"
                ? (503, "application/json", "{\"error\":\"nook_off\",\"message\":\"Nook is switched off by the owner right now.\"}")
                : (200, "application/json", "[]"),
        };
        var vm = Vm(srv.Url);
        vm.Chat.Input = "hello";
        await vm.Chat.SendCommand.ExecuteAsync(null);
        Assert.Equal("hello", vm.Chat.Input);
        var only = Assert.Single(vm.Chat.Messages);
        Assert.True(only.IsError);
        Assert.Contains("switched off", only.Text);
    }
}
