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

`scripts/start.sh --local` starts only the proxy (no tunnel). `scripts/stop.sh` stops both.

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

## Ollama auto start/stop

With `auto_manage_ollama` on (default), a chat request that finds Ollama down runs `~/.local/bin/ollama-on`
and waits for it. If the proxy started Ollama, it runs `~/.local/bin/ollama-off` after
`ollama_idle_minutes` (15) with no chat. An Ollama you started yourself is never stopped by the proxy.
`/status` (no auth) reports whether Ollama and the model are up; the site shows "Currently not working"
when the proxy is unreachable or Ollama can't be used.

## Deploy the site to GitHub Pages

Push this repo to GitHub, then Settings -> Pages -> Source: **Deploy from a branch**, branch `main`, folder `/docs`.
The PWA can then be installed from the phone browser ("Add to Home screen").

Local dev: `cd docs && python3 -m http.server 8788`, then open `http://localhost:8788/?backend=http://127.0.0.1:8787`.

## Optional: systemd user service (example, not enabled)

`~/.config/systemd/user/chat-gateway.service`:
```
[Unit]
Description=Ollama chat gateway proxy
[Service]
WorkingDirectory=%h/Projects/ollama-chat-gateway
ExecStart=/usr/bin/python3 server/server.py
Restart=on-failure
[Install]
WantedBy=default.target
```
(Create the owner key first with `python3 server/server.py --init` in a terminal. The tunnel still needs
`start.sh` or its own unit.)

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

`python3 tests/test_gateway.py` starts its own proxy on port 8797 with a temporary data dir and runs the
end-to-end checks against a fake Ollama (`tests/fake_ollama.py`, port 11597) that the proxy auto-starts
and auto-stops through `OLLAMA_ON_CMD`/`OLLAMA_OFF_CMD`. It never touches `data/` or the real Ollama.
It also migrates an old-schema database and, if `data/gateway.db` exists, a copy of it in a temp dir.
