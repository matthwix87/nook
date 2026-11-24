# Ollama chat gateway

Chat with the Ollama model on this PC from a phone, and hand friends temporary accounts.

```
phone (static site on GitHub Pages) --HTTPS--> Cloudflare quick tunnel --> proxy 127.0.0.1:8787 --> Ollama 127.0.0.1:11434
```

- `server/` - the proxy. Python 3 standard library only (no pip installs). Accounts, token budgets,
  chat history (SQLite in `data/`), streaming (SSE), admin API, Ollama auto start/stop.
- `site/` - the static PWA (plain HTML/CSS/JS, no build). Reads the current backend URL from `site/backend.json`.
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
scripts/start.sh          # proxy + tunnel, writes the new URL to site/backend.json
scripts/publish-url.sh    # commit + push backend.json so the site picks up the new URL
```

**Quick-tunnel caveat:** a free `trycloudflare.com` URL changes every time cloudflared restarts, so after
every `start.sh` you must run `publish-url.sh` (Pages takes ~1 minute to redeploy). The site fetches
`backend.json` with cache-busting on every load. For a stable URL, use a named Cloudflare tunnel with your
own domain and put that URL in `backend.json` once.

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

Push this repo to GitHub, then Settings -> Pages -> Source: **GitHub Actions**. The included workflow
`.github/workflows/pages.yml` publishes the `site/` folder on every push to `main` that touches `site/`.
The PWA can then be installed from the phone browser ("Add to Home screen").

Local dev: `cd site && python3 -m http.server 8788`, then open `http://localhost:8788/?backend=http://127.0.0.1:8787`.

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
- Request bodies are capped at 256 KB.
- The frontend never uses `innerHTML` for model or user text.
- The key is stored in the browser's localStorage so logins persist; anyone with the phone unlocked can use it.
- Anyone with the tunnel URL can reach the proxy (not Ollama); everything except `/status` needs a key.

## Tests

`python3 tests/test_gateway.py` starts its own proxy on port 8797 with a temporary data dir and runs the
end-to-end checks against the real Ollama (it stops Ollama first, lets the proxy auto-start it, and waits
for the idle auto-stop).
