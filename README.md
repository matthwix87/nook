# Nook - Ollama chat gateway

Chat with the Ollama model on this PC from a phone, and hand friends temporary accounts.
The assistant introduces itself as **Nook**.

```
phone (static site on GitHub Pages) --HTTPS--> Cloudflare quick tunnel --> proxy 127.0.0.1:8787 --> Ollama 127.0.0.1:11434
```

- `server/` - the proxy. Python 3 standard library only (no pip installs). Accounts, token budgets,
  chat history (SQLite in `data/`), streaming (SSE), admin API, Ollama auto start/stop.
- `docs/` - the static PWA (plain HTML/CSS/JS, no build). Reads the current backend URL from `docs/backend.json`.
- `scripts/` - `start.sh`, `stop.sh`, `publish-url.sh`, `make-icons.py`.
- `config.json` - all settings. Any key can be overridden by an upper-case env var (`PORT=9000`, `AUTO_MANAGE_OLLAMA=0`, `ALLOWED_ORIGINS=https://a.github.io,https://b.example`).
- `data/` - database, logs, pid files. Gitignored. Back it up if you care about accounts/chats.

Ollama is never exposed: the proxy binds to 127.0.0.1 and only the proxy is tunnelled.

## Setup

1. Set your GitHub Pages origin in `config.json` -> `allowed_origins`, e.g. `["https://yourname.github.io"]`
   (origin only, no path). `localhost`/`127.0.0.1` on any port are always allowed for development.
2. Install cloudflared (single binary, no sudo needed), for example:
   ```
   curl -L -o ~/.local/bin/cloudflared https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64
   chmod +x ~/.local/bin/cloudflared
   ```
3. First run, in a real terminal:
   ```
   scripts/start.sh
   ```
   On the very first run it creates the owner account and **prints the owner key once** (username `owner`).
   Save it in a password manager. If the first run's output is not a terminal, the key is written to
   `data/owner_key.txt` (mode 600) instead - read it, then delete that file.
   Lost it? `python3 server/server.py --rotate-owner-key` prints a new one.

`scripts/start.sh --local` starts only the proxy (no tunnel), `--tunnel-only` only the tunnel. `scripts/stop.sh` stops both.

## Daily use

```
scripts/start.sh          # proxy + tunnel, writes the new URL to docs/backend.json
scripts/publish-url.sh    # commit + push backend.json so the site picks up the new URL
```

**Quick-tunnel caveat:** a free `trycloudflare.com` URL changes every time cloudflared restarts, so after
every `start.sh` you must run `publish-url.sh` (Pages takes ~1 minute to redeploy). The site fetches
`backend.json` with cache-busting on every load. For a stable URL, use a named Cloudflare tunnel with your
own domain and put that URL in `backend.json` once.

## Features

- Streaming replies with a **Stop** button. Stopping closes the request to Ollama (generation stops) and
  charges only the prompt estimate plus the tokens actually produced; the partial reply is kept.
- **Edit** an earlier message (everything after it is removed and Nook answers again) and **Regenerate**
  the last reply.
- Chats: search by title and message text, rename, pin (pinned chats stay on top). New chats are titled
  from the first message.
- **Custom instructions** per account (Settings, max 2000 characters).
- **File attachments**: text/code files only (UTF-8, max 200 KB each, 4 per message). They are sent to the
  model as `<file name="...">...</file>` blocks after the message text and shown as chips. No images.
- **Projects**: name, instructions and text files (max 1 MB per project). Chats started inside a project
  get its instructions and files. Deleting a project asks whether to keep its chats (unassigned) or delete them.
- Code blocks have **Open**: a side panel (bottom sheet on phones) with Copy, Download and, for HTML, a
  Preview tab rendered in a sandboxed iframe (`sandbox="allow-scripts"`, no same-origin access).
- **Coding mode (desktop app)**: `POST /api/agent` relays one stateless model turn with tool definitions
  (the tools themselves run on the user's computer, never here). Same gates as chat (Nook off, maintenance,
  budget, guest rate limit with `retry_after`), tokens charged, context trimmed to the account's window.
  Spec: `desktop/GATEWAY_API.md`.
- **Warm-up**: when you start typing, the site calls `POST /api/warm` (at most every 5 minutes) so the
  proxy starts Ollama and loads the model with the same `num_ctx` as chats. No tokens charged; one per
  account per minute; disabled or out-of-budget accounts are refused.
- While waiting for the first token the reply shows "Thinking…", or "Waking up Nook…" when Ollama has to
  start or load the model (a hint appears after 20 s).
- **Settings** for every account: custom instructions, default reply style (Normal / Short / Detailed /
  Explain like I'm new, also switchable per chat), theme, text size and density (per device), a 30-day
  **usage** chart with allowance/refill/top-up, **export** of all chats (JSON or Markdown), **change my
  key** (the old key stops working at once) and **delete my account** (type the username; not for the owner).
- **Share** one chat as a self-contained `.html` page or `.md` file.
- **Offline**: if the host can't be reached, the app still opens with the chats, projects and settings
  this browser has cached (read-only, per account, cleared on logout; the key itself is never cached).
  A banner retries every 5 s for 3 minutes and the app refreshes itself when the host is back.
- Light/dark theme following the system, with a manual toggle. Enter sends, Shift+Enter adds a line,
  Esc closes the panel or sidebar.

### What goes into the context window

Every chat is sent to the model as one system message followed by the conversation. The system message
holds, in this order: `system_prompt` from `config.json` (the Nook identity, always first), the account's
custom instructions, the project's instructions, then project files. Everything counts against the
account's context window (guests ~8K tokens, owner 64K; estimate ~3 characters per token):

1. System prompt + instructions + the new message with its attachments must fit. If they don't, the
   message is refused with a clear "too big for the context window" error and nothing is saved or charged
   (the text and files stay in the composer).
2. Project files are added in upload order while they fit, keeping up to a quarter of the remaining room
   for recent history. Files that don't fit are left out and the chat shows a note naming them.
3. Older messages fill whatever room is left, newest first (older ones are trimmed, as before).

`system_prompt` can be changed in `config.json` (or the `SYSTEM_PROMPT` env var); keep it short, it is
counted in every guest's context. There is no UI for it.

## Accounts

Log in on the site as `owner` -> **Admin**. Create an account (username + daily token allowance + optional
top-up); the generated key is shown once with a copy button - send it to your friend. From the same page:
search by username or by pasting a key, change the daily allowance, add top-up tokens, disable for N
minutes/hours/days (auto re-enables), re-enable, rotate key, delete (removes their chats too).

Budgets: tokens = Ollama's `prompt_eval_count + eval_count` per reply (the whole conversation history
counts as prompt each turn). The **daily allowance is spent first** and resets at local midnight; once it
is used up, the **one-time top-up balance** is spent. When both are 0 the user gets a clear "out of tokens"
message. A request is allowed if anything is left, so the last reply can overshoot slightly (the overshoot
is forgiven, balances never go negative).

Guests vs owner (see `config.json`): guests get an ~8K-token context window (older messages are trimmed),
max 1024-token replies, 6 messages/minute, 8000-char messages. The owner is unlimited, gets 64K context
and 8192-token replies, and can never be disabled or deleted.

Usernames: 3-20 chars of letters, numbers, `_ . -`; reserved names and an inappropriate-word filter
(leetspeak, repeated letters, separators, camelCase handled) are rejected with a generic message.
Extend the list in `server/wordlist.txt`.

## Owner controls (Admin page)

- **Switch Nook off**: refuses chatting and warm-ups for everyone (owner included), never starts Ollama,
  and stops a running Ollama right away (with `ollama_off_cmd`, whoever started it) to free the GPU.
  Login, history, settings and Admin keep working; users see "Nook is switched off by the owner right
  now". Stored in the database, so it survives restarts. From a terminal on the PC (works whether or not
  the gateway is running; a running gateway notices within ~2 s):
  ```
  python3 server/server.py --nook-off
  python3 server/server.py --nook-on
  ```
- **Maintenance mode**: everyone except the owner is refused, with a banner.
- **Announcement**: a short message shown at the top for everyone (dismissible per device).
- **Activity**: replies and tokens per account (today / 7 days), last active, busiest hours.
  Counts only - chat content is never shown.

## Ollama auto start/stop

With `auto_manage_ollama` on (default), a chat request that finds Ollama down runs `~/.local/bin/ollama-on`
and waits for it. If the proxy started Ollama, it runs `~/.local/bin/ollama-off` after
`ollama_idle_minutes` (15) with no chat. An Ollama you started yourself is never stopped by the proxy.
`/status` (no auth) reports whether Ollama and the model are up; the site shows "Currently not working"
when the proxy is unreachable or Ollama can't be used.

## Deploy the site to GitHub Pages

On every frontend release, bump `CACHE` in `docs/sw.js` **and** the `?v=N` on `style.css`/`app.js` in
`docs/index.html`, so phones never mix a new page with old cached scripts or styles.

Push this repo to GitHub, then Settings -> Pages -> Source: **Deploy from a branch**, branch `main`, folder `/docs`.
The PWA can then be installed from the phone browser ("Add to Home screen").

Local dev: `cd docs && python3 -m http.server 8788`, then open `http://localhost:8788/?backend=http://127.0.0.1:8787`.

## Start automatically at login (systemd --user)

```
scripts/install-autostart.sh      # writes ~/.config/systemd/user/nook.service, enables and starts it
scripts/uninstall-autostart.sh    # stops (this also stops the gateway) and removes it
journalctl --user -u nook         # service log;  data/autostart.log has the script's own log
```

At login the service runs `scripts/autostart.sh`: it waits for the network, starts the proxy, starts the
tunnel (up to 5 tries, 15 s apart, in case the network isn't ready yet), then runs `publish-url.sh`.
If the push fails (no network or git credentials), that is logged to `data/autostart.log` and the
gateway keeps running; run `scripts/publish-url.sh` by hand later. `systemctl --user stop nook` runs
`stop.sh`. Create the owner key once in a terminal first (`python3 server/server.py --init`).

- Start at boot without logging in (optional): `loginctl enable-linger $USER`.
- The quick-tunnel URL changes on every start, so every start publishes (commits and pushes)
  `docs/backend.json`; Pages needs about a minute to pick it up.
- If the tunnel dies later while the PC stays on, it is **not** restarted automatically (out of scope);
  run `scripts/stop.sh && scripts/start.sh && scripts/publish-url.sh`, or restart the service.
- `start.sh` and `stop.sh` ignore stale pid files left by a crash (they check the process command line).

When the site can't reach the host, its "Try again" button keeps checking every 5 seconds for up to
3 minutes (re-reading `backend.json` each time, since the URL changes on restart) and opens Nook as soon
as the host answers. It can't switch on a PC that is off.

## Security notes

- Keys are 256-bit random; only their SHA-256 is stored. Key checks use constant-time comparison.
  Keys never appear in logs (only method + path are logged; admin search sends the key in a POST body).
- Failed logins are throttled per client IP (10 failures -> 15 min lockout). Behind the tunnel the real IP
  comes from cloudflared's `CF-Connecting-IP` header.
- CORS allows only the configured origins plus localhost; other browser origins get 403.
- Request bodies are capped at 2 MB (room for a few attached files).
- The frontend never uses `innerHTML` for model or user text (only for its own constant SVG icons).
- Every chat, project, file and instruction query is scoped to the logged-in account.
- The key is stored in the browser's localStorage so logins persist; anyone with the phone unlocked can use it.
- Anyone with the tunnel URL can reach the proxy (not Ollama); everything except `/status` needs a key.

## Database upgrades

New columns and tables (instructions, pinned, project_id, attachments, projects, project_files) are added
automatically on startup; existing accounts, keys, budgets and chats are kept. Restart the gateway to
pick up a new version.

## Tests

`python3 tests/test_gateway.py` starts its own proxy on port 8797 (`TEST_PORT`/`TEST_FAKE_PORT` override the ports) with a temporary data dir and runs the
end-to-end checks against a fake Ollama (`tests/fake_ollama.py`, port 11597) that the proxy auto-starts
and auto-stops through `OLLAMA_ON_CMD`/`OLLAMA_OFF_CMD`. It never touches `data/` or the real Ollama.
It also migrates an old-schema database and, if `data/gateway.db` exists, a copy of it in a temp dir.
