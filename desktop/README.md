# Nook desktop app

C# / .NET 8 + Avalonia 11, one codebase for Linux and Windows.

- **Chat** like the website: login (username + key), backend found via `backend.json` (cache-busted; manual
  override in Settings or on the login screen), streaming replies with Stop, chat list with search /
  rename / pin / delete (right-click), projects, custom instructions, token meter, offline screen with
  wait-and-retry, "Waking up Nook…" / "Thinking…", Nook-off / maintenance / announcement banners,
  light/dark following the system plus a toggle.
- **Coding mode** (every account): pick a folder; the model uses `read_file`, `list_dir`, `search`,
  `write_file`, `edit_file`, `run_command`. Tools run **on this computer**, confined to the folder
  (real paths, `..`/absolute/symlink escapes blocked). Writes and commands show a diff / the exact command
  and only run on **Approve**; Reject sends the reason back to the model. "Approve all for this session"
  is off by default, shows a warning banner when on and is never saved. Activity log on the right. Max 25
  steps per task, Stop at any time (kills a running command). Needs the gateway's `POST /api/agent`
  (see `GATEWAY_API.md`).

## Layout

| Path | What |
|---|---|
| `Nook.Core/` | No UI: API client (`Api/`), coding tools + agent loop (`Agent/`), OS specifics (`Platform/`) |
| `Nook.App/` | Avalonia UI (MVVM with CommunityToolkit.Mvvm) |
| `Nook.Tests/` | xunit: sandbox, diff/edit, approval gate, agent loop (fake gateway), API (fake HTTP server), headless UI + screenshots |
| `build-linux.sh` | tests, `dotnet publish -r linux-x64` single file, AppImage + tar.gz into `dist/` |
| `run-e2e.sh` | runs the agent loop against a real `server/server.py` (temp data dir, fake Ollama, spare port) |

## Build and run

```
export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH
dotnet run --project Nook.App          # run from source (needs a display: X11 or XWayland)
dotnet test Nook.Tests                 # unit + headless UI tests; screenshots land in screenshots/
./build-linux.sh                       # dist/Nook-<version>-x86_64.AppImage and .tar.gz
./run-e2e.sh                           # client <-> real gateway check
```

Config lives in `~/.config/nook/` (`settings.json`, and `keys.json` with mode 0600 for the saved key).

## Notes for the Windows build

OS-specific code is only in `Nook.Core/Platform/Platform.cs` (`IPlatform`):
- `WindowsPlatform.Secrets` still uses the plain `FileSecretStore` in `%APPDATA%\Nook` — TODO: DPAPI
  (`ProtectedData`, CurrentUser).
- `WindowsPlatform.Shell` runs commands with `powershell.exe -NoProfile -NonInteractive -Command`; check
  quoting and UTF-8 output. The coding prompt tells the model which shell it has (`ShellName`).
- `Workspace` compares paths case-insensitively on Windows; symlinks/junctions are resolved via
  `FileSystemInfo.LinkTarget` — worth a test on NTFS.
- `CommandRunner` kills the process tree on Stop/timeout (`Process.Kill(true)`).
- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` compiles here; `Assets/nook.ico`
  is set as the exe icon. Installer/packaging, code signing and a real run on Windows are not done.
