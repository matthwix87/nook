using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nook.Core.Api;

namespace Nook.App.ViewModels;

/// <summary>App shell: boot, login, offline screen, banners, token meter, settings, dialogs, toasts.</summary>
public partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Services = services;
        Chat = new ChatViewModel(this);
        Coding = new CodingViewModel(this);
        ThemeMode = services.Settings.Theme;
        LoginUsername = services.Settings.LastUser;
        BackendOverride = services.Settings.BackendOverride;
    }

    public AppServices Services { get; }
    public NookApi Api => Services.Api;
    public ChatViewModel Chat { get; }
    public CodingViewModel Coding { get; }

    // ---- screens: loading | offline | login | main
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsOffline), nameof(IsLogin), nameof(IsMain))]
    string _screen = "loading";
    public bool IsLoading => Screen == "loading";
    public bool IsOffline => Screen == "offline";
    public bool IsLogin => Screen == "login";
    public bool IsMain => Screen == "main";

    // ---- chat | code
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatMode), nameof(IsCodeMode))]
    string _mode = "chat";
    public bool IsChatMode => Mode == "chat";
    public bool IsCodeMode => Mode == "code";

    [RelayCommand]
    public void ShowChat() => Mode = "chat";

    [RelayCommand]
    public void ShowCode()
    {
        Mode = "code";
        Chat.SelectedConversation = null;
    }

    [ObservableProperty] Account? _me;
    [ObservableProperty] string _username = "";

    // ---- token meter (like the website's sidebar meter)
    [ObservableProperty] string _meterLabel = "";
    [ObservableProperty] string _meterSub = "";
    [ObservableProperty] double _meterPercent;
    [ObservableProperty] bool _meterEmpty;

    public void UpdateMeter(Budget? b)
    {
        if (b == null) return;
        if (b.Unlimited)
        {
            MeterLabel = "Unlimited tokens";
            MeterSub = "Owner account";
            MeterPercent = 100;
            MeterEmpty = false;
            return;
        }
        MeterLabel = $"{b.DailyLeft:N0} of {b.DailyAllowance:N0} daily tokens left";
        MeterPercent = b.DailyAllowance > 0 ? Math.Clamp(100.0 * b.DailyLeft / b.DailyAllowance, 0, 100) : 0;
        MeterEmpty = b.TotalLeft <= 0;
        var resets = DateTimeOffset.FromUnixTimeSeconds((long)b.ResetsAt).ToLocalTime();
        MeterSub = $"Top-up {b.Topup:N0}, resets at {resets:t}";
    }

    // ---- banners from /status
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    string _bannerText = "";
    [ObservableProperty] bool _bannerWarn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnnouncement))]
    string _announcement = "";
    public bool HasBanner => BannerText.Length > 0;
    public bool HasAnnouncement => Announcement.Length > 0;

    public void ApplyStatus(Status s)
    {
        Announcement = s.Announcement?.Trim() ?? "";
        if (s.NookOff) { BannerText = "Nook is switched off by the owner right now."; BannerWarn = true; }
        else if (s.Maintenance)
        {
            BannerText = Me?.IsOwner == true
                ? "Maintenance mode is on: only you (the owner) can chat."
                : "Nook is down for maintenance. Try again later.";
            BannerWarn = true;
        }
        else { BannerText = ""; BannerWarn = false; }
    }

    DispatcherTimer? _statusTimer;

    async Task RefreshStatusAsync()
    {
        try { ApplyStatus(await Api.StatusAsync()); }
        catch (ApiException) { /* the next request will show the offline screen if needed */ }
    }

    // ---- offline screen ("Host computer offline temporarily" + wait-and-retry like the website)
    [ObservableProperty] string _offlineTitle = "Currently not working";
    [ObservableProperty] string _offlineDetail = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotWaiting))]
    bool _isWaiting;
    public bool IsNotWaiting => !IsWaiting;
    [ObservableProperty] string _waitText = "";
    CancellationTokenSource? _waitCts;

    public const string OfflineText = "Host computer offline temporarily. Try again in a little while.";

    public void ShowOffline(string? detail = null)
    {
        _waitCts?.Cancel();
        IsWaiting = false;
        OfflineTitle = "Currently not working";
        OfflineDetail = string.IsNullOrWhiteSpace(detail) || detail == "Can't reach the server" ? OfflineText : detail;
        Screen = "offline";
    }

    [RelayCommand]
    async Task RetryAsync()
    {
        _waitCts?.Cancel();
        var cts = _waitCts = new CancellationTokenSource();
        IsWaiting = true;
        OfflineTitle = "Waiting for the host";
        OfflineDetail = "Nook opens as soon as the host's computer is reachable again.";
        var end = DateTime.UtcNow.AddMinutes(3);
        try
        {
            while (DateTime.UtcNow < end)
            {
                var left = end - DateTime.UtcNow;
                WaitText = $"Checking every few seconds - {(int)left.TotalMinutes}:{left.Seconds:00} left";
                // Re-read backend.json each time: the tunnel URL changes when the host restarts.
                await Api.ResolveBackendAsync(Services.Settings.BackendOverride, Services.BackendJsonUrl, cts.Token);
                try
                {
                    var s = await Api.StatusAsync(cts.Token);
                    if (s.Available)
                    {
                        IsWaiting = false;
                        await BootAsync();
                        return;
                    }
                }
                catch (ApiException) { }
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(1000, cts.Token);
                    left = end - DateTime.UtcNow;
                    WaitText = $"Checking every few seconds - {Math.Max(0, (int)left.TotalMinutes)}:{Math.Max(0, left.Seconds):00} left";
                }
            }
            IsWaiting = false;
            OfflineTitle = "Unavailable right now";
            OfflineDetail = "Still no answer from the host's computer. It may be switched off or restarting.";
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    void CancelWait()
    {
        _waitCts?.Cancel();
        IsWaiting = false;
        OfflineTitle = "Currently not working";
        OfflineDetail = OfflineText;
    }

    // ---- boot + login
    public async Task BootAsync()
    {
        App.ApplyTheme(ThemeMode);
        Screen = "loading";
        await Api.ResolveBackendAsync(Services.Settings.BackendOverride, Services.BackendJsonUrl);
        Status status;
        try { status = await Api.StatusAsync(); }
        catch (ApiException) { ShowOffline(); return; }
        if (!status.Available)
        {
            ShowOffline(status.Ollama && !status.ModelAvailable ? "The model isn't available on the host right now." : null);
            return;
        }
        ApplyStatus(status);

        var user = Services.Settings.LastUser;
        var key = user.Length > 0 ? Services.Platform.Secrets.Load(user) : null;
        if (key == null) { Screen = "login"; return; }
        Api.SetAuth(user, key);
        try
        {
            await EnterAsync(await Api.MeAsync());
        }
        catch (ApiException e) when (e.Code == "bad_login")
        {
            Services.Platform.Secrets.Delete(user);
            Api.SetAuth(null, null);
            LoginError = "Your login is no longer valid.";
            Screen = "login";
        }
        catch (ApiException e) { HandleError(e, loginScreen: true); }
    }

    [ObservableProperty] string _loginUsername = "";
    [ObservableProperty] string _loginKey = "";
    [ObservableProperty] string _loginError = "";
    [ObservableProperty] bool _loginBusy;
    [ObservableProperty] bool _showServerSettings;

    [RelayCommand]
    void ToggleServerSettings() => ShowServerSettings = !ShowServerSettings;

    [RelayCommand]
    async Task LoginAsync()
    {
        var user = LoginUsername.Trim();
        var key = LoginKey.Trim();
        if (user.Length == 0 || key.Length == 0) { LoginError = "Enter your username and key."; return; }
        LoginBusy = true;
        LoginError = "";
        try
        {
            if (ShowServerSettings && BackendOverride.Trim() != Services.Settings.BackendOverride)
            {
                Services.Settings.BackendOverride = BackendOverride.Trim();
                Services.SaveSettings();
                await Api.ResolveBackendAsync(Services.Settings.BackendOverride, Services.BackendJsonUrl);
            }
            Api.SetAuth(user, key);
            var me = await Api.MeAsync();
            Services.Platform.Secrets.Save(me.Username, key);
            Services.Settings.LastUser = me.Username;
            Services.SaveSettings();
            LoginKey = "";
            await EnterAsync(me);
        }
        catch (ApiException e)
        {
            Api.SetAuth(null, null);
            if (e.IsOffline) LoginError = "Can't reach Nook. The host computer may be offline.";
            else if (e.Code == "disabled") LoginError = DisabledText(e);
            else LoginError = e.Message;
        }
        finally { LoginBusy = false; }
    }

    static string DisabledText(ApiException e)
    {
        var until = e.Number("until");
        return until is { } u
            ? $"This account is temporarily disabled until {DateTimeOffset.FromUnixTimeSeconds((long)u).ToLocalTime():g}."
            : "This account is temporarily disabled.";
    }

    async Task EnterAsync(Account me)
    {
        Me = me;
        Username = me.Username;
        UpdateMeter(me.Budget);
        Screen = "main";
        Mode = "chat";
        _statusTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(2), DispatcherPriority.Background, async (_, _) =>
        {
            if (IsMain) await RefreshStatusAsync();
        });
        _statusTimer.Start();
        await Chat.LoadAsync();
    }

    public async Task RefreshMeAsync()
    {
        try
        {
            Me = await Api.MeAsync();
            UpdateMeter(Me.Budget);
        }
        catch (ApiException) { }
    }

    [RelayCommand]
    void Logout() => DoLogout("");

    public void DoLogout(string message)
    {
        if (Api.Username != null) Services.Platform.Secrets.Delete(Api.Username);
        Api.SetAuth(null, null);
        Coding.Reset();
        Chat.Clear();
        Me = null;
        IsSettingsOpen = false;
        LoginError = message;
        Screen = "login";
    }

    /// <summary>Errors that change the whole screen; others become a toast.</summary>
    public void HandleError(ApiException e, bool loginScreen = false)
    {
        if (e.IsOffline) ShowOffline();
        else if (e.Code == "disabled") { LoginError = DisabledText(e); Screen = "login"; }
        else if (e.Code == "bad_login") DoLogout("Your login is no longer valid.");
        else if (loginScreen) { LoginError = e.Message; Screen = "login"; }
        else Toast(e.Message);
    }

    /// <summary>After a failed request: check /status and show the offline screen if the host is gone.</summary>
    public async Task<bool> CheckStatusAsync()
    {
        try
        {
            var s = await Api.StatusAsync();
            ApplyStatus(s);
            if (!s.Available) { ShowOffline(); return false; }
            return true;
        }
        catch (ApiException) { ShowOffline(); return false; }
    }

    // ---- theme
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    string _themeMode = "system";
    public string ThemeLabel => ThemeMode switch { "light" => "Light", "dark" => "Dark", _ => "Auto" };

    [RelayCommand]
    void ToggleTheme()
    {
        ThemeMode = ThemeMode switch { "system" => "light", "light" => "dark", _ => "system" };
        App.ApplyTheme(ThemeMode);
        Services.Settings.Theme = ThemeMode;
        Services.SaveSettings();
    }

    // ---- settings panel
    [ObservableProperty] bool _isSettingsOpen;
    [ObservableProperty] string _backendOverride = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstructionsCount))]
    string _instructions = "";
    [ObservableProperty] int _maxInstructions = 2000;
    [ObservableProperty] string _settingsError = "";
    [ObservableProperty] bool _settingsBusy;
    public string InstructionsCount => $"{Instructions.Length} / {MaxInstructions}";
    public string BackendInfo => $"Connected to {Api.BaseUrl}";

    [RelayCommand]
    async Task OpenSettingsAsync()
    {
        SettingsError = "";
        BackendOverride = Services.Settings.BackendOverride;
        OnPropertyChanged(nameof(BackendInfo));
        IsSettingsOpen = true;
        try
        {
            var s = await Api.SettingsAsync();
            Instructions = s.Instructions;
            MaxInstructions = s.MaxInstructionsChars;
        }
        catch (ApiException e) { SettingsError = e.Message; }
    }

    [RelayCommand]
    void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    async Task SaveSettingsAsync()
    {
        SettingsBusy = true;
        SettingsError = "";
        try
        {
            if (Instructions.Length > MaxInstructions) { SettingsError = $"Instructions are too long (max {MaxInstructions} characters)."; return; }
            await Api.SaveInstructionsAsync(Instructions);
            var newBackend = BackendOverride.Trim();
            if (newBackend != Services.Settings.BackendOverride)
            {
                Services.Settings.BackendOverride = newBackend;
                Services.SaveSettings();
                IsSettingsOpen = false;
                await BootAsync();
                return;
            }
            IsSettingsOpen = false;
            Toast("Settings saved");
        }
        catch (ApiException e) { SettingsError = e.Message; }
        finally { SettingsBusy = false; }
    }

    // ---- toast
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    string _toastText = "";
    public bool HasToast => ToastText.Length > 0;
    int _toastId;

    public async void Toast(string text)
    {
        var id = ++_toastId;
        ToastText = text;
        await Task.Delay(4000);
        if (id == _toastId) ToastText = "";
    }

    // ---- simple modal dialog (confirm / text prompt)
    [ObservableProperty] DialogViewModel? _dialog;

    public async Task<string?> PromptAsync(string title, string message, string? input, string okText, bool danger = false)
    {
        var d = new DialogViewModel(title, message, input, okText, danger);
        Dialog = d;
        try { return await d.Result; }
        finally { if (Dialog == d) Dialog = null; }
    }
}

public partial class DialogViewModel : ObservableObject
{
    readonly TaskCompletionSource<string?> _tcs = new();

    public DialogViewModel(string title, string message, string? input, string okText, bool danger)
    {
        Title = title;
        Message = message;
        HasInput = input != null;
        _input = input ?? "";
        OkText = okText;
        IsDanger = danger;
    }

    public string Title { get; }
    public string Message { get; }
    public bool HasInput { get; }
    public bool HasMessage => Message.Length > 0;
    public string OkText { get; }
    public bool IsDanger { get; }
    [ObservableProperty] string _input;
    public Task<string?> Result => _tcs.Task;

    [RelayCommand]
    void Ok() => _tcs.TrySetResult(Input);

    [RelayCommand]
    void Cancel() => _tcs.TrySetResult(null);
}
