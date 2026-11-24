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
| `build-windows.sh` | tests, `dotnet publish -r win-x64` single file, `dist/Nook-<version>-win-x64.zip` (Nook.exe + README.txt) — runs on Linux |
| `run-e2e.sh` | runs the agent loop against a real `server/server.py` (temp data dir, fake Ollama, spare port) |

## Build and run

```
export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH
dotnet run --project Nook.App          # run from source (needs a display: X11 or XWayland)
dotnet test Nook.Tests                 # unit + headless UI tests; screenshots land in screenshots/
./build-linux.sh                       # dist/Nook-<version>-x86_64.AppImage and .tar.gz
./build-windows.sh                     # dist/Nook-<version>-win-x64.zip
./run-e2e.sh                           # client <-> real gateway check
```

Config lives in `~/.config/nook/` (`settings.json`, and `keys.json` with mode 0600 for the saved key) on Linux,
and in `%APPDATA%\Nook\` on Windows (`settings.json`, and `keys.dat`: the saved key, DPAPI-encrypted).

## Windows

Same code; OS-specific parts sit behind `IPlatform` (`Nook.Core/Platform/Platform.cs`) or `OperatingSystem.IsWindows()`.

- **Key storage**: `%APPDATA%\Nook\keys.dat`, encrypted with DPAPI (`ProtectedData`, CurrentUser scope, app-specific
  entropy). A plain `keys.json` from an older build is merged in and deleted on first use. If the file can't be
  decrypted (other account, copied to another PC) the user is just logged out.
- **run_command**: PowerShell 7 (`pwsh.exe`, first absolute PATH entry that has it) or else
  `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` (full path, so no exe planted next to Nook.exe or in
  the current folder is picked up). Arguments: `-NoProfile -NonInteractive -Command <prelude + command>`, the command
  as a single `ArgumentList` entry (.NET quotes it with the MSVCRT rules PowerShell parses with). `-EncodedCommand`
  is avoided because
  Windows PowerShell can then write stderr as CLIXML. The prelude turns off
  progress output and sets `$OutputEncoding` / `[Console]::OutputEncoding` to UTF-8 without BOM; stdout/stderr are
  read as UTF-8. Working directory = workspace root; timeout and Stop use `Process.Kill(entireProcessTree: true)`.
  The coding prompt names the shell and tells the model to use PowerShell syntax (and that `&&` doesn't work in 5.1).
- **Sandbox**: on top of the real-path containment check (case-insensitive on Windows), model-supplied paths are
  refused if they are device paths (`\\?\`, `\\.\`, `\??\`), drive-relative (`C:foo`), contain `:` after the drive
  (alternate data streams), have a component ending in `.` or space, or use a reserved device name (`CON`, `NUL`,
  `COM1`, `LPT1`, `CONIN$`, … also with an extension). Symlinks and junctions are followed via `LinkTarget`; junction
  targets in `\\?\C:\…` form are normalized before the containment check, volume-GUID targets are refused. UNC paths
  are allowed only when they are inside the workspace. Rules are in `WindowsPathRules` (pure, unit-tested on Linux).
- **Files**: editing or overwriting an existing file keeps its CRLF line endings and its UTF-8 BOM. New files are
  written as given (UTF-8, no BOM).
- **Exe**: GUI subsystem (no console window), `nook.ico` as exe/taskbar icon, `app.manifest` declaring Windows
  10/11. Single file: Avalonia's native DLLs (Skia, HarfBuzz, ANGLE) are inside Nook.exe and unpacked to `%TEMP%\.net`
  on first run.

### Verified vs not

Verified here (Linux): everything compiles for `win-x64` without platform warnings; the published exe has the GUI
subsystem, icon and manifest resources and bundles `ProtectedData`; unit tests cover the Windows path rules, the
containment check with backslashes and case-insensitivity, junction-target normalization, the PowerShell command
line, pwsh lookup, the prompt's shell hint, the protected key store + migration (with a stand-in for DPAPI) and
CRLF/BOM preservation.

**Not verified (no Windows machine, no Wine):** the app actually starting on Windows, DPAPI round-trip, PowerShell
receiving the command unchanged (quotes, newlines, non-ASCII) and UTF-8 output from both PowerShell versions, process
tree kill, junction/symlink behaviour and `LinkTarget` formats on NTFS, file/folder dialogs, SmartScreen. Not done:
code signing, an installer, auto-update. Known limits: hard links pointing outside the workspace can't be detected
(same on Linux); files in legacy ANSI code pages are read/written as UTF-8 (non-ASCII characters in them get
replaced on edit); folders with per-directory case sensitivity enabled are still compared case-insensitively.

### Manual test on a Windows PC

1. Unzip `Nook-<version>-win-x64.zip`, run Nook.exe (SmartScreen: More info, Run anyway). Check the taskbar icon.
2. Log in with "remember me"; check `%APPDATA%\Nook\keys.dat` exists and is not readable text; restart, still logged in.
3. Chat: send a message with non-ASCII (é, 日本語, emoji), Stop a streaming reply.
4. Coding mode, pick a test folder with a CRLF file that has a BOM (e.g. a Visual Studio `.cs` file):
   - "read X" (no approval asked), "add a comment to X": the diff shows, Reject with a note, then ask again and Approve;
     check in an editor that the file is still CRLF with BOM.
   - "run `Get-ChildItem`", "run `git status`", "print 'héllo 日本'": output and accents correct, exit code shown.
   - "run `Start-Sleep 600`" then Stop: the command ends at once and no `powershell`/`pwsh` is left in Task Manager;
     also let one hit the 2-minute timeout.
   - Turn on "Approve all for this session": warning banner shows, writes/commands go through without asking; off again.
   - Escape attempts, all must be refused: "read `..\..\Windows\win.ini`", "read `C:\Windows\win.ini`",
     "read `\\?\C:\Windows\win.ini`", "write `NUL`", "write `a.txt:hidden`"; and a junction inside the folder
     pointing outside (`mklink /J out C:\Windows`, then "read `out\win.ini`").
