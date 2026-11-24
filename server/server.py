#!/usr/bin/env python3
"""Ollama chat gateway: accounts, token budgets, chat history, streaming proxy.

Python standard library only. Binds to 127.0.0.1; reach it through a tunnel.
Usage:
  python3 server/server.py                 run (creates the owner key on first run)
  python3 server/server.py --init          only create the owner account, then exit
  python3 server/server.py --rotate-owner-key
"""
import datetime
import hashlib
import hmac
import json
import os
import re
import secrets
import shlex
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from collections import defaultdict, deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import usernames  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# ---------------------------------------------------------------- config
def load_config():
    path = os.environ.get("GATEWAY_CONFIG", os.path.join(ROOT, "config.json"))
    with open(path, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg.setdefault("owner_username", "owner")
    # Any key can be overridden by an upper-cased environment variable.
    for key, default in list(cfg.items()):
        val = os.environ.get(key.upper())
        if val is None:
            continue
        if isinstance(default, bool):
            cfg[key] = val.lower() in ("1", "true", "yes", "on")
        elif isinstance(default, (int, float)):
            cfg[key] = float(val) if "." in val else int(val)
        elif isinstance(default, list):
            cfg[key] = [v.strip() for v in val.split(",") if v.strip()]
        else:
            cfg[key] = val
    return cfg


CFG = load_config()
DATA = CFG["data_dir"] if os.path.isabs(CFG["data_dir"]) else os.path.join(ROOT, CFG["data_dir"])
os.makedirs(DATA, mode=0o700, exist_ok=True)


def log(*args):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), *args, flush=True)


# ---------------------------------------------------------------- database
LOCK = threading.RLock()
db = sqlite3.connect(os.path.join(DATA, "gateway.db"), check_same_thread=False, isolation_level=None)
db.row_factory = sqlite3.Row
db.executescript("""
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS accounts (
  id INTEGER PRIMARY KEY,
  username TEXT NOT NULL UNIQUE COLLATE NOCASE,
  key_hash TEXT NOT NULL UNIQUE,
  is_owner INTEGER NOT NULL DEFAULT 0,
  daily_allowance INTEGER NOT NULL DEFAULT 0,
  daily_used INTEGER NOT NULL DEFAULT 0,
  tokens_today INTEGER NOT NULL DEFAULT 0,
  daily_day TEXT NOT NULL DEFAULT '',
  topup INTEGER NOT NULL DEFAULT 0,
  disabled_until REAL NOT NULL DEFAULT 0,
  created REAL NOT NULL,
  last_active REAL NOT NULL DEFAULT 0,
  total_tokens INTEGER NOT NULL DEFAULT 0,
  total_requests INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS conversations (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  title TEXT NOT NULL,
  created REAL NOT NULL,
  updated REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS messages (
  id INTEGER PRIMARY KEY,
  conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  role TEXT NOT NULL,
  content TEXT NOT NULL,
  created REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS conv_acc ON conversations(account_id);
CREATE INDEX IF NOT EXISTS msg_conv ON messages(conversation_id);
""")


def q(sql, args=()):
    with LOCK:
        return db.execute(sql, args).fetchall()


def q1(sql, args=()):
    rows = q(sql, args)
    return rows[0] if rows else None


def hash_key(key):
    return hashlib.sha256(key.encode()).hexdigest()


def new_key():
    return secrets.token_urlsafe(32)


def today():
    return datetime.date.today().isoformat()


def next_midnight():
    d = datetime.datetime.combine(datetime.date.today() + datetime.timedelta(days=1), datetime.time())
    return d.timestamp()


def get_account(where, args):
    """Fetch an account row, resetting the daily counters if a new day began."""
    with LOCK:
        acc = q1(f"SELECT * FROM accounts WHERE {where}", args)
        if acc and acc["daily_day"] != today():
            q("UPDATE accounts SET daily_used=0, tokens_today=0, daily_day=? WHERE id=?", (today(), acc["id"]))
            acc = q1("SELECT * FROM accounts WHERE id=?", (acc["id"],))
        return acc


def budget(acc):
    if acc["is_owner"]:
        return {"unlimited": True}
    daily_left = max(0, acc["daily_allowance"] - acc["daily_used"])
    return {
        "unlimited": False,
        "daily_allowance": acc["daily_allowance"],
        "daily_left": daily_left,
        "topup": acc["topup"],
        "total_left": daily_left + acc["topup"],
        "resets_at": next_midnight(),
    }


def account_json(acc):
    return {
        "id": acc["id"], "username": acc["username"], "is_owner": bool(acc["is_owner"]),
        "daily_allowance": acc["daily_allowance"], "daily_used": acc["daily_used"],
        "tokens_today": acc["tokens_today"], "topup": acc["topup"],
        "disabled_until": acc["disabled_until"] if acc["disabled_until"] > time.time() else 0,
        "created": acc["created"], "last_active": acc["last_active"],
        "total_tokens": acc["total_tokens"], "total_requests": acc["total_requests"],
        "budget": budget(acc),
    }


def charge(acc_id, tokens):
    """Spend the daily allowance first, then the one-time top-up balance."""
    with LOCK:
        acc = get_account("id=?", (acc_id,))
        if not acc:
            return None
        from_daily = from_topup = 0
        if not acc["is_owner"]:
            from_daily = min(tokens, max(0, acc["daily_allowance"] - acc["daily_used"]))
            from_topup = min(tokens - from_daily, acc["topup"])
        q("""UPDATE accounts SET daily_used=daily_used+?, topup=topup-?, tokens_today=tokens_today+?,
             total_tokens=total_tokens+?, total_requests=total_requests+1, last_active=? WHERE id=?""",
          (from_daily, from_topup, tokens, tokens, time.time(), acc_id))
        return get_account("id=?", (acc_id,))


def create_owner_if_missing():
    if q1("SELECT id FROM accounts WHERE is_owner=1"):
        return
    key = new_key()
    q("INSERT INTO accounts (username, key_hash, is_owner, created, daily_day) VALUES (?,?,1,?,?)",
      (CFG["owner_username"], hash_key(key), time.time(), today()))
    show_owner_key(key)


def show_owner_key(key):
    if sys.stdout.isatty():
        print("\n" + "=" * 64)
        print(f" OWNER ACCOUNT  username: {CFG['owner_username']}")
        print(f" OWNER KEY:     {key}")
        print(" This is shown ONCE. Save it in a password manager now.")
        print("=" * 64 + "\n", flush=True)
    else:
        # Not a terminal (e.g. output goes to a log file): never put the key in a log.
        path = os.path.join(DATA, "owner_key.txt")
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            f.write(f"username: {CFG['owner_username']}\nkey: {key}\n")
        log(f"Owner key written to {path} - save it, then delete that file.")


# ---------------------------------------------------------------- ollama
class Ollama:
    lock = threading.Lock()
    started_by_us = False
    last_activity = time.time()
    active = 0

    @staticmethod
    def status():
        try:
            with urllib.request.urlopen(CFG["ollama_url"] + "/api/tags", timeout=2) as r:
                names = {m.get("name") for m in json.load(r).get("models", [])}
            model = CFG["model"]
            return True, (model in names or model + ":latest" in names)
        except Exception:
            return False, False

    @staticmethod
    def run(cmd):
        args = shlex.split(cmd)
        args[0] = os.path.expanduser(args[0])
        try:
            subprocess.run(args, timeout=60, capture_output=True)
        except Exception as e:
            log("ollama command failed:", e)

    @classmethod
    def ensure(cls):
        cls.last_activity = time.time()
        if cls.status()[0]:
            return True
        if not CFG["auto_manage_ollama"]:
            return False
        with cls.lock:
            if cls.status()[0]:
                return True
            log("Ollama is down, starting it")
            cls.run(CFG["ollama_on_cmd"])
            deadline = time.time() + CFG["ollama_start_timeout_s"]
            while time.time() < deadline:
                if cls.status()[0]:
                    cls.started_by_us = True
                    cls.last_activity = time.time()
                    log("Ollama is up")
                    return True
                time.sleep(1)
            log("Ollama did not come up in time")
            return False

    @classmethod
    def idle_watcher(cls):
        """Stop Ollama after it sat idle, but only if this proxy started it."""
        while True:
            time.sleep(5)
            idle = time.time() - cls.last_activity
            if (cls.started_by_us and cls.active == 0 and CFG["auto_manage_ollama"]
                    and idle > CFG["ollama_idle_minutes"] * 60):
                with cls.lock:
                    log("Ollama idle, stopping it")
                    cls.run(CFG["ollama_off_cmd"])
                    cls.started_by_us = False


# ---------------------------------------------------------------- throttles
auth_failures = defaultdict(deque)   # ip -> timestamps of failed logins
guest_requests = defaultdict(deque)  # account id -> timestamps of chat requests
throttle_lock = threading.Lock()


def recent(dq, window):
    now = time.time()
    while dq and dq[0] < now - window:
        dq.popleft()
    return len(dq)


LOCALHOST_ORIGIN = re.compile(r"^https?://(localhost|127\.0\.0\.1)(:\d+)?$")


def origin_allowed(origin):
    return origin in CFG["allowed_origins"] or bool(LOCALHOST_ORIGIN.match(origin))


class ApiError(Exception):
    def __init__(self, code, error, message, **extra):
        super().__init__(message)
        self.code, self.body = code, {"error": error, "message": message, **extra}


# ---------------------------------------------------------------- http
class Handler(BaseHTTPRequestHandler):
    server_version = "gateway"
    sys_version = ""

    def log_message(self, fmt, *args):
        # Path only, never query strings or headers (no keys in logs).
        log(self.client_ip(), self.command, urlparse(self.path).path, *(a for a in args[1:2]))

    def client_ip(self):
        peer = self.client_address[0]
        if peer == "127.0.0.1" and self.headers.get("CF-Connecting-IP"):
            return self.headers["CF-Connecting-IP"]  # set by cloudflared
        return peer

    # ---- response helpers
    def cors_headers(self):
        origin = self.headers.get("Origin")
        if origin:
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
            self.send_header("Access-Control-Allow-Headers", "Authorization, Content-Type")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, PATCH, DELETE, OPTIONS")
            self.send_header("Access-Control-Max-Age", "600")

    def send_json(self, code, obj, cors=True):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        if cors:
            self.cors_headers()
        self.end_headers()
        self.wfile.write(data)

    def read_json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length > CFG["max_body_bytes"]:
            raise ApiError(413, "too_large", "Request too large")
        if not length:
            return {}
        try:
            body = json.loads(self.rfile.read(length))
        except ValueError:
            raise ApiError(400, "bad_json", "Invalid JSON")
        if not isinstance(body, dict):
            raise ApiError(400, "bad_json", "Expected a JSON object")
        return body

    # ---- auth
    def authenticate(self):
        ip = self.client_ip()
        with throttle_lock:
            if recent(auth_failures[ip], CFG["login_lockout_minutes"] * 60) >= CFG["login_max_failures"]:
                raise ApiError(429, "locked_out", "Too many failed logins. Try again later.")
        auth = self.headers.get("Authorization", "")
        user, _, key = auth[7:].partition(":") if auth.startswith("Bearer ") else ("", "", "")
        acc = get_account("username=?", (user,)) if user and key else None
        expected = acc["key_hash"] if acc else "0" * 64
        if not (hmac.compare_digest(hash_key(key), expected) and acc):
            with throttle_lock:
                auth_failures[ip].append(time.time())
            raise ApiError(401, "bad_login", "Wrong username or key")
        with throttle_lock:
            auth_failures.pop(ip, None)
        if acc["disabled_until"] > time.time():
            until = acc["disabled_until"]
            raise ApiError(403, "disabled", "This account is temporarily disabled.", until=until)
        return acc

    def require_owner(self):
        acc = self.authenticate()
        if not acc["is_owner"]:
            raise ApiError(403, "forbidden", "Owner only")
        return acc

    # ---- dispatch
    def do_OPTIONS(self):
        self.dispatch()

    def do_GET(self):
        self.dispatch()

    def do_POST(self):
        self.dispatch()

    def do_PATCH(self):
        self.dispatch()

    def do_DELETE(self):
        self.dispatch()

    ROUTES = [
        ("GET", r"/status", "status"),
        ("POST", r"/api/login", "me"),
        ("GET", r"/api/me", "me"),
        ("GET", r"/api/conversations", "list_convs"),
        ("GET", r"/api/conversations/(\d+)", "get_conv"),
        ("DELETE", r"/api/conversations/(\d+)", "delete_conv"),
        ("POST", r"/api/chat", "chat"),
        ("GET", r"/api/admin/accounts", "admin_list"),
        ("POST", r"/api/admin/accounts/search", "admin_search"),
        ("POST", r"/api/admin/accounts", "admin_create"),
        ("PATCH", r"/api/admin/accounts/(\d+)", "admin_edit"),
        ("DELETE", r"/api/admin/accounts/(\d+)", "admin_delete"),
        ("POST", r"/api/admin/accounts/(\d+)/disable", "admin_disable"),
        ("POST", r"/api/admin/accounts/(\d+)/enable", "admin_enable"),
        ("POST", r"/api/admin/accounts/(\d+)/rotate", "admin_rotate"),
    ]

    def dispatch(self):
        self.close_connection = True
        origin = self.headers.get("Origin")
        if origin and not origin_allowed(origin):
            return self.send_json(403, {"error": "origin", "message": "Origin not allowed"}, cors=False)
        if self.command == "OPTIONS":
            self.send_response(204)
            self.cors_headers()
            self.end_headers()
            return
        path = urlparse(self.path).path.rstrip("/") or "/"
        try:
            for method, pattern, name in self.ROUTES:
                m = re.fullmatch(pattern, path)
                if m and method == self.command:
                    return getattr(self, name)(*[int(g) for g in m.groups()])
            raise ApiError(404, "not_found", "Not found")
        except ApiError as e:
            self.send_json(e.code, e.body)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as e:
            log("error:", type(e).__name__, e)
            try:
                self.send_json(500, {"error": "server", "message": "Internal error"})
            except Exception:
                pass

    # ---- public
    def status(self):
        up, model_ok = Ollama.status()
        self.send_json(200, {
            "backend": True, "ollama": up, "model": CFG["model"], "model_available": model_ok,
            "auto_start": CFG["auto_manage_ollama"],
            "available": (up and model_ok) or (not up and CFG["auto_manage_ollama"]),
        })

    def me(self):
        acc = self.authenticate()
        self.send_json(200, account_json(acc))

    # ---- conversations
    def own_conv(self, acc, conv_id):
        conv = q1("SELECT * FROM conversations WHERE id=? AND account_id=?", (conv_id, acc["id"]))
        if not conv:
            raise ApiError(404, "not_found", "Conversation not found")
        return conv

    def list_convs(self):
        acc = self.authenticate()
        rows = q("SELECT id, title, created, updated FROM conversations WHERE account_id=? ORDER BY updated DESC",
                 (acc["id"],))
        self.send_json(200, [dict(r) for r in rows])

    def get_conv(self, conv_id):
        acc = self.authenticate()
        conv = self.own_conv(acc, conv_id)
        msgs = q("SELECT role, content, created FROM messages WHERE conversation_id=? ORDER BY id", (conv_id,))
        self.send_json(200, {**dict(conv), "messages": [dict(m) for m in msgs]})

    def delete_conv(self, conv_id):
        acc = self.authenticate()
        self.own_conv(acc, conv_id)
        q("DELETE FROM conversations WHERE id=?", (conv_id,))
        self.send_json(200, {"ok": True})

    # ---- chat
    def chat(self):
        acc = self.authenticate()
        body = self.read_json()
        owner = bool(acc["is_owner"])
        text = body.get("message")
        if not isinstance(text, str) or not text.strip():
            raise ApiError(400, "bad_request", "Message is empty")
        if not owner and len(text) > CFG["guest_max_message_chars"]:
            raise ApiError(400, "too_long", f"Message too long (max {CFG['guest_max_message_chars']} characters)")
        if not owner:
            if budget(acc)["total_left"] <= 0:
                raise ApiError(402, "no_tokens", "You're out of tokens for today. The daily allowance resets at midnight.",
                               resets_at=next_midnight())
            with throttle_lock:
                dq = guest_requests[acc["id"]]
                if recent(dq, 60) >= CFG["guest_requests_per_minute"]:
                    raise ApiError(429, "rate_limited", "Slow down a little - too many messages this minute.")
                dq.append(time.time())

        conv_id = body.get("conversation_id")
        if conv_id is not None:
            self.own_conv(acc, int(conv_id))

        Ollama.active += 1
        try:
            if not Ollama.ensure():
                raise ApiError(503, "offline", "The host's computer is offline right now.")
            now = time.time()
            if conv_id is None:
                title = " ".join(text.split())[:60]
                with LOCK:
                    conv_id = db.execute("INSERT INTO conversations (account_id, title, created, updated) VALUES (?,?,?,?)",
                                         (acc["id"], title, now, now)).lastrowid
            conv_id = int(conv_id)
            q("INSERT INTO messages (conversation_id, role, content, created) VALUES (?,?,?,?)",
              (conv_id, "user", text, now))
            self.stream_reply(acc, owner, conv_id)
        finally:
            Ollama.active -= 1
            Ollama.last_activity = time.time()

    def build_history(self, conv_id, owner):
        ctx = CFG["owner_context_tokens"] if owner else CFG["guest_context_tokens"]
        reply = CFG["owner_max_reply_tokens"] if owner else CFG["guest_max_reply_tokens"]
        room = ctx - reply
        rows = q("SELECT role, content FROM messages WHERE conversation_id=? ORDER BY id DESC", (conv_id,))
        picked = []
        for r in rows:  # newest first; rough estimate of ~3.5 chars per token
            cost = len(r["content"]) * 2 // 7 + 4
            if picked and cost > room:
                break
            room -= cost
            picked.append({"role": r["role"], "content": r["content"]})
        return picked[::-1], reply

    def stream_reply(self, acc, owner, conv_id):
        messages, max_reply = self.build_history(conv_id, owner)
        req = urllib.request.Request(
            CFG["ollama_url"] + "/api/chat",
            data=json.dumps({
                "model": CFG["model"], "messages": messages, "stream": True, "think": False,
                # Same num_ctx for everyone so Ollama never reloads the model; guests get
                # a smaller window because build_history trims their history.
                "options": {"num_ctx": CFG["owner_context_tokens"], "num_predict": max_reply},
            }).encode(),
            headers={"Content-Type": "application/json"},
        )
        try:
            resp = urllib.request.urlopen(req, timeout=600)
        except Exception as e:
            log("ollama request failed:", e)
            raise ApiError(503, "offline", "The model is not responding right now.")

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("X-Accel-Buffering", "no")
        self.cors_headers()
        self.end_headers()

        def event(obj):
            self.wfile.write(b"data: " + json.dumps(obj).encode() + b"\n\n")
            self.wfile.flush()

        reply, usage, client_gone = [], None, False
        try:
            event({"type": "meta", "conversation_id": conv_id})
            with resp:
                for line in resp:
                    if not line.strip():
                        continue
                    chunk = json.loads(line)
                    if chunk.get("error"):
                        event({"type": "error", "message": "The model returned an error."})
                        log("ollama error:", chunk["error"])
                        break
                    piece = chunk.get("message", {}).get("content", "")
                    if piece:
                        reply.append(piece)
                        event({"type": "delta", "text": piece})
                    if chunk.get("done"):
                        usage = (chunk.get("prompt_eval_count", 0), chunk.get("eval_count", 0))
                        break
        except (BrokenPipeError, ConnectionResetError):
            client_gone = True
        except Exception as e:
            log("stream error:", type(e).__name__, e)

        if usage is None:  # aborted: charge a rough estimate instead
            prompt_chars = sum(len(m["content"]) for m in messages)
            usage = (prompt_chars * 2 // 7, len("".join(reply)) * 2 // 7)
        full = "".join(reply)
        now = time.time()
        if full:
            q("INSERT INTO messages (conversation_id, role, content, created) VALUES (?,?,?,?)",
              (conv_id, "assistant", full, now))
        q("UPDATE conversations SET updated=? WHERE id=?", (now, conv_id))
        acc = charge(acc["id"], usage[0] + usage[1])
        if not client_gone and acc:
            try:
                event({"type": "done", "usage": {"prompt": usage[0], "completion": usage[1],
                                                 "total": usage[0] + usage[1]}, "budget": budget(acc)})
            except (BrokenPipeError, ConnectionResetError):
                pass

    # ---- admin
    def find_account(self, acc_id):
        acc = get_account("id=?", (acc_id,))
        if not acc:
            raise ApiError(404, "not_found", "Account not found")
        return acc

    def admin_list(self):
        self.require_owner()
        rows = q("SELECT id FROM accounts ORDER BY is_owner DESC, username")
        self.send_json(200, [account_json(get_account("id=?", (r["id"],))) for r in rows])

    def admin_search(self):
        self.require_owner()
        term = str(self.read_json().get("q", "")).strip()
        rows = q("SELECT id FROM accounts WHERE username LIKE ? OR key_hash=? ORDER BY is_owner DESC, username",
                 (f"%{term}%", hash_key(term)))
        self.send_json(200, [account_json(get_account("id=?", (r["id"],))) for r in rows])

    def admin_create(self):
        self.require_owner()
        body = self.read_json()
        name = body.get("username")
        err = usernames.check(name)
        if err:
            raise ApiError(400, "bad_username", err)
        if q1("SELECT id FROM accounts WHERE username=?", (name,)):
            raise ApiError(409, "taken", "That username is already taken")
        allowance = body.get("daily_allowance", CFG["default_daily_allowance"])
        topup = body.get("topup", 0)
        if not (isinstance(allowance, int) and isinstance(topup, int) and allowance >= 0 and topup >= 0):
            raise ApiError(400, "bad_request", "Token amounts must be whole numbers >= 0")
        key = new_key()
        with LOCK:
            acc_id = db.execute(
                "INSERT INTO accounts (username, key_hash, daily_allowance, topup, created, daily_day) VALUES (?,?,?,?,?,?)",
                (name, hash_key(key), allowance, topup, time.time(), today())).lastrowid
        log("account created:", name)
        self.send_json(201, {"account": account_json(get_account("id=?", (acc_id,))), "key": key})

    def admin_edit(self, acc_id):
        self.require_owner()
        acc = self.find_account(acc_id)
        body = self.read_json()
        if "daily_allowance" in body:
            v = body["daily_allowance"]
            if not isinstance(v, int) or v < 0:
                raise ApiError(400, "bad_request", "daily_allowance must be a whole number >= 0")
            q("UPDATE accounts SET daily_allowance=? WHERE id=?", (v, acc_id))
        if "add_topup" in body:
            v = body["add_topup"]
            if not isinstance(v, int):
                raise ApiError(400, "bad_request", "add_topup must be a whole number")
            q("UPDATE accounts SET topup=MAX(0, topup+?) WHERE id=?", (v, acc_id))
        if "reset_today" in body and body["reset_today"]:
            q("UPDATE accounts SET daily_used=0 WHERE id=?", (acc_id,))
        self.send_json(200, account_json(self.find_account(acc["id"])))

    def admin_delete(self, acc_id):
        self.require_owner()
        acc = self.find_account(acc_id)
        if acc["is_owner"]:
            raise ApiError(400, "owner", "The owner account can't be deleted")
        q("DELETE FROM accounts WHERE id=?", (acc_id,))
        log("account deleted:", acc["username"])
        self.send_json(200, {"ok": True})

    def admin_disable(self, acc_id):
        self.require_owner()
        acc = self.find_account(acc_id)
        if acc["is_owner"]:
            raise ApiError(400, "owner", "The owner account can't be disabled")
        minutes = self.read_json().get("minutes")
        if not isinstance(minutes, (int, float)) or minutes <= 0:
            raise ApiError(400, "bad_request", "minutes must be a positive number")
        q("UPDATE accounts SET disabled_until=? WHERE id=?", (time.time() + minutes * 60, acc_id))
        self.send_json(200, account_json(self.find_account(acc_id)))

    def admin_enable(self, acc_id):
        self.require_owner()
        self.find_account(acc_id)
        q("UPDATE accounts SET disabled_until=0 WHERE id=?", (acc_id,))
        self.send_json(200, account_json(self.find_account(acc_id)))

    def admin_rotate(self, acc_id):
        self.require_owner()
        acc = self.find_account(acc_id)
        key = new_key()
        q("UPDATE accounts SET key_hash=? WHERE id=?", (hash_key(key), acc_id))
        log("key rotated:", acc["username"])
        self.send_json(200, {"account": account_json(self.find_account(acc_id)), "key": key})


def main():
    if "--rotate-owner-key" in sys.argv:
        create_owner_if_missing()
        key = new_key()
        q("UPDATE accounts SET key_hash=? WHERE is_owner=1", (hash_key(key),))
        show_owner_key(key)
        return
    create_owner_if_missing()
    if "--init" in sys.argv:
        return
    threading.Thread(target=Ollama.idle_watcher, daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", CFG["port"]), Handler)
    server.daemon_threads = True
    log(f"Gateway listening on http://127.0.0.1:{CFG['port']}  model={CFG['model']} "
        f"auto_manage_ollama={CFG['auto_manage_ollama']}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
