using Nook.Core;
using Nook.Core.Api;
using Nook.Core.Platform;

namespace Nook.App;

/// <summary>The few shared services, passed to view models (tests build their own).</summary>
public sealed class AppServices
{
    public AppServices(IPlatform platform, NookApi api, AppSettings settings)
    {
        Platform = platform;
        Api = api;
        Settings = settings;
    }

    public IPlatform Platform { get; }
    public NookApi Api { get; }
    public AppSettings Settings { get; }
    public string BackendJsonUrl { get; set; } = NookApi.BackendJsonUrl;

    public void SaveSettings() => Settings.Save(Platform.ConfigDir);

    public static AppServices CreateDefault()
    {
        var platform = PlatformInfo.Current;
        return new AppServices(platform, new NookApi(), AppSettings.Load(platform.ConfigDir));
    }
}
