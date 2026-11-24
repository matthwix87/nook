#!/usr/bin/env python3
"""Ollama chat gateway: accounts, token budgets, chat history, streaming proxy.

Python standard library only. Binds to 127.0.0.1; reach it through a tunnel.
Usage:
  python3 server/server.py                 run (creates the owner key on first run)
  python3 server/server.py --init          only create the owner account, then exit
  python3 server/server.py --rotate-owner-key
  python3 server/server.py --nook-off | --nook-on   switch chatting off/on for everyone (stops Ollama)
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
from urllib.parse import parse_qs, urlparse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import usernames  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# ---------------------------------------------------------------- config
DEFAULT_SYSTEM_PROMPT = (
    "You are Nook, a friendly, concise assistant running privately on your owner's own computer. "
    "If asked your name or who you are, say you're Nook. You are not human and don't claim to be conscious. "
    "If sincerely asked what model you run on, say you run on an open-source language model; don't name or invent a vendor.")


def load_config():
    path = os.environ.get("GATEWAY_CONFIG", os.path.join(ROOT, "config.json"))
    with open(path, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg.setdefault("owner_username", "owner")
    # Newer settings, so an older config.json keeps working.
    for key, default in (("system_prompt", DEFAULT_SYSTEM_PROMPT), ("max_instructions_chars", 2000), ("max_file_bytes", 204800),
                         ("max_files_per_message", 4), ("max_project_bytes", 1048576)):
        cfg.setdefault(key, default)
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
CREATE TABLE IF NOT EXISTS projects (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  instructions TEXT NOT NULL DEFAULT '',
  created REAL NOT NULL,
  updated REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS project_files (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  content TEXT NOT NULL,
  size INTEGER NOT NULL,
  created REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS proj_acc ON projects(account_id);
CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS usage_events (
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  ts REAL NOT NULL,
  tokens INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS usage_acc_ts ON usage_events(account_id, ts);
CREATE INDEX IF NOT EXISTS pfile_proj ON project_files(project_id);
""")


def migrate():
    """Add columns introduced after the first release (safe to run on every start)."""
    added = {
        "accounts": [("instructions", "TEXT NOT NULL DEFAULT ''"), ("reply_style", "TEXT NOT NULL DEFAULT ''")],
        "conversations": [("pinned", "INTEGER NOT NULL DEFAULT 0"), ("project_id", "INTEGER"), ("reply_style", "TEXT")],
        "messages": [("attachments", "TEXT")],  # JSON list of {name, content} on user messages
    }
    for table, cols in added.items():
        have = {r[1] for r in db.execute(f"PRAGMA table_info({table})")}
        for name, decl in cols:
            if name not in have:
                db.execute(f"ALTER TABLE {table} ADD COLUMN {name} {decl}")
    db.execute("CREATE INDEX IF NOT EXISTS conv_proj ON conversations(project_id)")


migrate()


def flag(key, default=""):
    """Global switches (nook_off, maintenance, announcement), stored in the DB so the CLI and restarts see them."""
    row = q1("SELECT value FROM settings WHERE key=?", (key,))
    return row["value"] if row else default


def set_flag(key, value):
    q("INSERT INTO settings (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, value))


REPLY_STYLES = {
    "": "",
    "short": "Reply style: keep answers short - a few sentences or a brief list.",
    "detailed": "Reply style: give thorough, detailed answers with examples where they help.",
    "beginner": "Reply style: explain as if the user is new to the topic - plain words, no jargon, small steps.",
}


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
        q("INSERT INTO usage_events (account_id, ts, tokens) VALUES (?,?,?)", (acc_id, time.time(), tokens))
        q("DELETE FROM usage_events WHERE ts < ?", (time.time() - 90 * 86400,))
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
    def model_loaded():
        try:
            with urllib.request.urlopen(CFG["ollama_url"] + "/api/ps", timeout=2) as r:
                names = {m.get("name") for m in json.load(r).get("models", [])}
            return CFG["model"] in names or CFG["model"] + ":latest" in names
        except Exception:
            return False

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
        if flag("nook_off"):
            return False  # never start Ollama while switched off
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

    @staticmethod
    def load_options():
        """Load-time options shared by chats and warm-ups (a different num_ctx would reload the model)."""
        return {"num_ctx": CFG["owner_context_tokens"]}

    warming = False
    warm_lock = threading.Lock()

    @classmethod
    def warm(cls):
        """Background: start Ollama if needed (same path as chat) and load the model. One at a time."""
        with cls.warm_lock:
            if cls.warming:
                return
            cls.warming = True
        cls.active += 1
        try:
            if cls.ensure():  # starts Ollama if down + auto-manage on; refreshes the idle timer
                req = urllib.request.Request(
                    CFG["ollama_url"] + "/api/chat",
                    data=json.dumps({"model": CFG["model"], "messages": [], "stream": False,
                                     "options": cls.load_options()}).encode(),
                    headers={"Content-Type": "application/json"})
                with urllib.request.urlopen(req, timeout=CFG["ollama_start_timeout_s"] + 300) as r:
                    r.read()
        except Exception as e:
            log("warm-up failed:", type(e).__name__, e)
        finally:
            cls.active -= 1
            cls.last_activity = time.time()
            cls.warming = False

    @classmethod
    def stop_now(cls):
        """Nook off: stop Ollama regardless of who started it, to free the GPU."""
        with cls.lock:
            log("Nook is switched off, stopping Ollama")
            cls.run(CFG["ollama_off_cmd"])
            cls.started_by_us = False

    @classmethod
    def idle_watcher(cls):
        """Stop Ollama after it sat idle (only if this proxy started it), or at once when Nook is off."""
        last_off = 0
        while True:
            time.sleep(2)
            if flag("nook_off"):
                if time.time() - last_off > 60 and cls.status()[0]:
                    last_off = time.time()
                    cls.stop_now()
                continue
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
warm_requests = {}                   # account id -> time of the last warm-up it triggered
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


# ---------------------------------------------------------------- text files
BINARY_EXT = set("""png jpg jpeg gif webp bmp ico tif tiff heic avif svgz psd pdf zip gz tgz bz2 xz 7z rar zst
tar jar war apk ipa exe dll so dylib bin o a obj class pyc wasm iso dmg img mp3 mp4 m4a wav ogg flac
mov avi mkv webm doc docx xls xlsx ppt pptx odt ods odp sqlite db ttf otf woff woff2 eot""".split())


def clean_file(f):
    """Validate one {name, content} text file from a request; returns (name, content, size)."""
    if not isinstance(f, dict) or not isinstance(f.get("name"), str) or not isinstance(f.get("content"), str):
        raise ApiError(400, "bad_file", "Each file needs a name and text content")
    name = re.sub(r'[\x00-\x1f"<>\\/]', "", f["name"]).strip()[:120] or "file.txt"
    ext = name.rsplit(".", 1)[-1].lower() if "." in name else ""
    content = f["content"]
    try:
        size = len(content.encode("utf-8"))
    except UnicodeEncodeError:
        raise ApiError(400, "not_text", f"{name} isn't valid UTF-8 text")
    if ext in BINARY_EXT or "\x00" in content:
        raise ApiError(400, "not_text", f"{name} isn't a text file. Only text and code files can be attached.")
    if not content.strip():
        raise ApiError(400, "empty_file", f"{name} is empty")
    if size > CFG["max_file_bytes"]:
        raise ApiError(400, "file_too_large", f"{name} is too large (max {CFG['max_file_bytes'] // 1024} KB per file)")
    controls = sum(1 for ch in content[:20000] if ord(ch) < 32 and ch not in "\t\n\r\f")
    if controls > len(content[:20000]) // 100:
        raise ApiError(400, "not_text", f"{name} looks like a binary file, not text")
    return name, content, size


def file_block(name, content):
    return f'<file name="{name}">\n{content}\n</file>'


def user_prompt(content, attachments):
    """A user message as the model sees it: the text, then each attached file as a delimited block."""
    if not attachments:
        return content
    return content + "\n\n" + "\n\n".join(file_block(a["name"], a["content"]) for a in attachments)


def est(text):
    """Rough token estimate (~3 chars per token, on the safe side for code)."""
    return len(text) // 3 + 4


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
        ("PATCH", r"/api/conversations/(\d+)", "edit_conv"),
        ("DELETE", r"/api/conversations/(\d+)", "delete_conv"),
        ("POST", r"/api/chat", "chat"),
        ("POST", r"/api/warm", "warm"),
        ("DELETE", r"/api/me", "delete_me"),
        ("POST", r"/api/me/rotate", "rotate_me"),
        ("GET", r"/api/usage", "usage"),
        ("GET", r"/api/export", "export"),
        ("GET", r"/api/admin/activity", "admin_activity"),
        ("GET", r"/api/admin/system", "admin_system"),
        ("PATCH", r"/api/admin/system", "admin_edit_system"),
        ("GET", r"/api/settings", "get_settings"),
        ("PATCH", r"/api/settings", "edit_settings"),
        ("GET", r"/api/projects", "list_projects"),
        ("POST", r"/api/projects", "create_project"),
        ("GET", r"/api/projects/(\d+)", "get_project"),
        ("PATCH", r"/api/projects/(\d+)", "edit_project"),
        ("DELETE", r"/api/projects/(\d+)", "delete_project"),
        ("POST", r"/api/projects/(\d+)/files", "add_project_file"),
        ("DELETE", r"/api/projects/(\d+)/files/(\d+)", "delete_project_file"),
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
            "nook_off": bool(flag("nook_off")), "maintenance": bool(flag("maintenance")),
            "announcement": flag("announcement"),
        })

    def check_open(self, acc):
        """Refuse chat / warm-up when Nook is off (everyone) or in maintenance (everyone but the owner)."""
        if flag("nook_off"):
            raise ApiError(503, "nook_off", "Nook is switched off by the owner right now.")
        if flag("maintenance") and not acc["is_owner"]:
            raise ApiError(503, "maintenance", "Nook is down for maintenance. Try again later.")

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
        term = (parse_qs(urlparse(self.path).query).get("q") or [""])[0].strip()[:200]
        cols = "id, title, created, updated, pinned, project_id, reply_style"
        if not term:
            rows = q(f"SELECT {cols} FROM conversations WHERE account_id=? ORDER BY pinned DESC, updated DESC",
                     (acc["id"],))
            return self.send_json(200, [dict(r) for r in rows])
        like = "%" + re.sub(r"([\\%_])", r"\\\1", term) + "%"
        rows = q(f"""SELECT {cols},
                       (SELECT content FROM messages m WHERE m.conversation_id=c.id
                          AND m.content LIKE ? ESCAPE '\\' ORDER BY m.id LIMIT 1) AS hit
                     FROM conversations c WHERE account_id=? AND (title LIKE ? ESCAPE '\\' OR EXISTS (
                       SELECT 1 FROM messages m WHERE m.conversation_id=c.id AND m.content LIKE ? ESCAPE '\\'))
                     ORDER BY pinned DESC, updated DESC LIMIT 100""", (like, acc["id"], like, like))
        out = []
        for r in rows:
            item = dict(r)
            hit = item.pop("hit")
            if hit:
                i = hit.lower().find(term.lower())
                start = max(0, i - 40)
                item["snippet"] = ("..." if start else "") + " ".join(hit[start:start + 120].split())
            out.append(item)
        self.send_json(200, out)

    def get_conv(self, conv_id):
        acc = self.authenticate()
        conv = self.own_conv(acc, conv_id)
        msgs = []
        for m in q("SELECT id, role, content, attachments, created FROM messages WHERE conversation_id=? ORDER BY id",
                   (conv_id,)):
            item = dict(m)
            files = json.loads(item.pop("attachments") or "[]")
            item["attachments"] = [{"name": f["name"], "size": len(f["content"].encode())} for f in files]
            msgs.append(item)
        self.send_json(200, {**dict(conv), "messages": msgs})

    def edit_conv(self, conv_id):
        acc = self.authenticate()
        self.own_conv(acc, conv_id)
        body = self.read_json()
        if "title" in body:
            title = " ".join(str(body["title"]).split())[:120]
            if not title:
                raise ApiError(400, "bad_request", "Title can't be empty")
            q("UPDATE conversations SET title=? WHERE id=?", (title, conv_id))
        if "pinned" in body:
            q("UPDATE conversations SET pinned=? WHERE id=?", (1 if body["pinned"] else 0, conv_id))
        if "reply_style" in body:  # null = follow the account's default
            if body["reply_style"] is not None and body["reply_style"] not in REPLY_STYLES:
                raise ApiError(400, "bad_request", "Unknown reply style")
            q("UPDATE conversations SET reply_style=? WHERE id=?", (body["reply_style"], conv_id))
        if "project_id" in body:
            pid = body["project_id"]
            if pid is not None:
                pid = self.own_project(acc, pid)["id"]
            q("UPDATE conversations SET project_id=? WHERE id=?", (pid, conv_id))
        conv = self.own_conv(acc, conv_id)
        self.send_json(200, {k: conv[k] for k in ("id", "title", "created", "updated", "pinned", "project_id", "reply_style")})

    def delete_conv(self, conv_id):
        acc = self.authenticate()
        self.own_conv(acc, conv_id)
        q("DELETE FROM conversations WHERE id=?", (conv_id,))
        self.send_json(200, {"ok": True})

    # ---- warm-up (preload the model while the user types)
    def warm(self):
        acc = self.authenticate()  # also rejects disabled accounts
        self.check_open(acc)
        if not acc["is_owner"] and budget(acc)["total_left"] <= 0:
            raise ApiError(402, "no_tokens", "You're out of tokens for today.", resets_at=next_midnight())
        with throttle_lock:
            last = warm_requests.get(acc["id"], 0)
            started = time.time() - last >= 60
            if started:
                warm_requests[acc["id"]] = time.time()
        if started:
            threading.Thread(target=Ollama.warm, daemon=True).start()
        self.send_json(202, {"ok": True, "started": started})

    # ---- my account: delete, rotate key, usage, export
    def delete_me(self):
        acc = self.authenticate()
        if acc["is_owner"]:
            raise ApiError(400, "owner", "The owner account can't be deleted")
        if str(self.read_json().get("confirm", "")).lower() != acc["username"].lower():
            raise ApiError(400, "confirm", "Type your username to confirm")
        q("DELETE FROM accounts WHERE id=?", (acc["id"],))  # chats, projects, files and usage cascade
        log("account deleted by its user:", acc["username"])
        self.send_json(200, {"ok": True})

    def rotate_me(self):
        acc = self.authenticate()
        key = new_key()
        q("UPDATE accounts SET key_hash=? WHERE id=?", (hash_key(key), acc["id"]))
        log("key rotated by its user:", acc["username"])
        self.send_json(200, {"key": key})

    def usage(self):
        acc = self.authenticate()
        start = datetime.date.today() - datetime.timedelta(days=29)
        rows = q("SELECT date(ts, 'unixepoch', 'localtime') AS day, SUM(tokens) AS tokens, COUNT(*) AS replies "
                 "FROM usage_events WHERE account_id=? AND ts>=? GROUP BY day",
                 (acc["id"], datetime.datetime.combine(start, datetime.time()).timestamp()))
        by_day = {r["day"]: r for r in rows}
        days = []
        for i in range(30):
            d = (start + datetime.timedelta(days=i)).isoformat()
            r = by_day.get(d)
            days.append({"day": d, "tokens": r["tokens"] if r else 0, "replies": r["replies"] if r else 0})
        self.send_json(200, {"days": days, "budget": budget(acc), "total_tokens": acc["total_tokens"]})

    def export(self):
        acc = self.authenticate()
        projects = {p["id"]: p for p in q("SELECT * FROM projects WHERE account_id=?", (acc["id"],))}
        convs = []
        for c in q("SELECT * FROM conversations WHERE account_id=? ORDER BY created", (acc["id"],)):
            msgs = q("SELECT role, content, attachments, created FROM messages WHERE conversation_id=? ORDER BY id",
                     (c["id"],))
            convs.append({"title": c["title"], "created": c["created"], "updated": c["updated"],
                          "project": projects[c["project_id"]]["name"] if c["project_id"] in projects else None,
                          "messages": [{"role": m["role"], "content": m["content"], "created": m["created"],
                                        "attachments": json.loads(m["attachments"] or "[]")} for m in msgs]})
        out = {"username": acc["username"], "exported": time.time(), "custom_instructions": acc["instructions"],
               "projects": [{"name": p["name"], "instructions": p["instructions"],
                             "files": [dict(f) for f in q("SELECT name, content FROM project_files WHERE project_id=?",
                                                          (p["id"],))]} for p in projects.values()],
               "conversations": convs}
        self.send_json(200, out)

    # ---- custom instructions
    def get_settings(self):
        acc = self.authenticate()
        acc = get_account("id=?", (acc["id"],))
        self.send_json(200, {"instructions": acc["instructions"], "reply_style": acc["reply_style"],
                             "max_instructions_chars": CFG["max_instructions_chars"],
                             "max_file_bytes": CFG["max_file_bytes"], "max_files_per_message": CFG["max_files_per_message"],
                             "max_project_bytes": CFG["max_project_bytes"]})

    def clean_instructions(self, value):
        if not isinstance(value, str):
            raise ApiError(400, "bad_request", "Instructions must be text")
        value = value.strip()
        if len(value) > CFG["max_instructions_chars"]:
            raise ApiError(400, "too_long", f"Instructions are too long (max {CFG['max_instructions_chars']} characters)")
        return value

    def edit_settings(self):
        acc = self.authenticate()
        body = self.read_json()
        if "instructions" in body:
            q("UPDATE accounts SET instructions=? WHERE id=?", (self.clean_instructions(body["instructions"]), acc["id"]))
        if "reply_style" in body:
            if body["reply_style"] not in REPLY_STYLES:
                raise ApiError(400, "bad_request", "Unknown reply style")
            q("UPDATE accounts SET reply_style=? WHERE id=?", (body["reply_style"], acc["id"]))
        self.get_settings()

    # ---- projects
    def own_project(self, acc, project_id):
        if not isinstance(project_id, int) or isinstance(project_id, bool):
            raise ApiError(400, "bad_request", "project_id must be a number")
        proj = q1("SELECT * FROM projects WHERE id=? AND account_id=?", (project_id, acc["id"]))
        if not proj:
            raise ApiError(404, "not_found", "Project not found")
        return proj

    def project_json(self, proj):
        files = q("SELECT id, name, size, created FROM project_files WHERE project_id=? ORDER BY id", (proj["id"],))
        return {**{k: proj[k] for k in ("id", "name", "instructions", "created", "updated")}, "files": [dict(f) for f in files],
                "total_bytes": sum(f["size"] for f in files)}

    def list_projects(self):
        acc = self.authenticate()
        rows = q("SELECT * FROM projects WHERE account_id=? ORDER BY name COLLATE NOCASE", (acc["id"],))
        self.send_json(200, [self.project_json(p) for p in rows])

    def project_name(self, value):
        name = " ".join(str(value or "").split())[:80]
        if not name:
            raise ApiError(400, "bad_request", "Give the project a name")
        return name

    def create_project(self):
        acc = self.authenticate()
        body = self.read_json()
        name = self.project_name(body.get("name"))
        instructions = self.clean_instructions(body.get("instructions", ""))
        if q1("SELECT COUNT(*) AS n FROM projects WHERE account_id=?", (acc["id"],))["n"] >= 50:
            raise ApiError(400, "too_many", "You can have at most 50 projects")
        now = time.time()
        with LOCK:
            pid = db.execute("INSERT INTO projects (account_id, name, instructions, created, updated) VALUES (?,?,?,?,?)",
                             (acc["id"], name, instructions, now, now)).lastrowid
        self.send_json(201, self.project_json(self.own_project(acc, pid)))

    def get_project(self, project_id):
        acc = self.authenticate()
        self.send_json(200, self.project_json(self.own_project(acc, project_id)))

    def edit_project(self, project_id):
        acc = self.authenticate()
        self.own_project(acc, project_id)
        body = self.read_json()
        if "name" in body:
            q("UPDATE projects SET name=? WHERE id=?", (self.project_name(body["name"]), project_id))
        if "instructions" in body:
            q("UPDATE projects SET instructions=? WHERE id=?", (self.clean_instructions(body["instructions"]), project_id))
        q("UPDATE projects SET updated=? WHERE id=?", (time.time(), project_id))
        self.send_json(200, self.project_json(self.own_project(acc, project_id)))

    def delete_project(self, project_id):
        acc = self.authenticate()
        self.own_project(acc, project_id)
        chats = self.read_json().get("chats", "keep")
        if chats not in ("keep", "delete"):
            raise ApiError(400, "bad_request", "chats must be 'keep' or 'delete'")
        with LOCK:
            if chats == "delete":
                db.execute("DELETE FROM conversations WHERE project_id=? AND account_id=?", (project_id, acc["id"]))
            else:
                db.execute("UPDATE conversations SET project_id=NULL WHERE project_id=? AND account_id=?",
                           (project_id, acc["id"]))
            db.execute("DELETE FROM projects WHERE id=?", (project_id,))  # files go with it (cascade)
        self.send_json(200, {"ok": True})

    def add_project_file(self, project_id):
        acc = self.authenticate()
        proj = self.own_project(acc, project_id)
        name, content, size = clean_file(self.read_json())
        used = q1("SELECT COALESCE(SUM(size), 0) AS n FROM project_files WHERE project_id=?", (project_id,))["n"]
        if used + size > CFG["max_project_bytes"]:
            raise ApiError(400, "project_full", f"Not enough room: projects hold up to "
                           f"{CFG['max_project_bytes'] // 1024} KB of files ({used // 1024} KB used)")
        q("INSERT INTO project_files (project_id, name, content, size, created) VALUES (?,?,?,?,?)",
          (project_id, name, content, size, time.time()))
        self.send_json(201, self.project_json(proj))

    def delete_project_file(self, project_id, file_id):
        acc = self.authenticate()
        proj = self.own_project(acc, project_id)
        if not q1("SELECT id FROM project_files WHERE id=? AND project_id=?", (file_id, project_id)):
            raise ApiError(404, "not_found", "File not found")
        q("DELETE FROM project_files WHERE id=?", (file_id,))
        self.send_json(200, self.project_json(proj))

    # ---- chat
    def chat(self):
        """New message, edit of an earlier user message (edit_message_id), or regenerate (regenerate: true)."""
        acc = self.authenticate()
        self.check_open(acc)
        body = self.read_json()
        owner = bool(acc["is_owner"])
        regenerate = bool(body.get("regenerate"))
        edit_id = body.get("edit_message_id")
        text = body.get("message")
        if not regenerate:
            if not isinstance(text, str) or not text.strip():
                raise ApiError(400, "bad_request", "Message is empty")
            if not owner and len(text) > CFG["guest_max_message_chars"]:
                raise ApiError(400, "too_long", f"Message too long (max {CFG['guest_max_message_chars']} characters)")
        files = body.get("attachments") or []
        if not isinstance(files, list) or len(files) > CFG["max_files_per_message"]:
            raise ApiError(400, "too_many_files", f"Attach at most {CFG['max_files_per_message']} files per message")
        attachments = [dict(zip(("name", "content"), clean_file(f)[:2])) for f in files]
        if not owner:
            if budget(acc)["total_left"] <= 0:
                raise ApiError(402, "no_tokens", "You're out of tokens for today. The daily allowance resets at midnight.",
                               resets_at=next_midnight())

        conv_id = body.get("conversation_id")
        conv = self.own_conv(acc, int(conv_id)) if conv_id is not None else None
        if (regenerate or edit_id is not None) and not conv:
            raise ApiError(400, "bad_request", "conversation_id is required")
        style = body.get("reply_style")
        if style is not None and style not in REPLY_STYLES:
            raise ApiError(400, "bad_request", "Unknown reply style")
        if conv:
            project_id = conv["project_id"]
        else:
            project_id = body.get("project_id")
            if project_id is not None:
                self.own_project(acc, project_id)
        project = q1("SELECT * FROM projects WHERE id=?", (project_id,)) if project_id else None

        # The conversation as it will look after this request, oldest first.
        rows = [dict(r) for r in q("SELECT id, role, content, attachments FROM messages WHERE conversation_id=? "
                                   "ORDER BY id", (conv["id"],))] if conv else []
        for r in rows:
            r["attachments"] = json.loads(r["attachments"] or "[]")
        if regenerate:
            last_user = max((i for i, r in enumerate(rows) if r["role"] == "user"), default=None)
            if last_user is None:
                raise ApiError(400, "bad_request", "Nothing to regenerate")
            convo = rows[:last_user + 1]
        elif edit_id is not None:
            idx = next((i for i, r in enumerate(rows) if r["id"] == edit_id and r["role"] == "user"), None)
            if idx is None:
                raise ApiError(404, "not_found", "Message not found")
            convo = rows[:idx] + [{**rows[idx], "content": text}]
        else:
            convo = rows + [{"role": "user", "content": text, "attachments": attachments}]
        if conv and conv["reply_style"] is not None:
            style = conv["reply_style"]
        messages, max_reply, notices = self.build_prompt(acc, owner, project, convo,
                                                         acc["reply_style"] if style is None else style)

        if not owner:
            with throttle_lock:
                dq = guest_requests[acc["id"]]
                if recent(dq, 60) >= CFG["guest_requests_per_minute"]:
                    raise ApiError(429, "rate_limited", "Slow down a little - too many messages this minute.")
                dq.append(time.time())

        Ollama.active += 1
        try:
            up = Ollama.status()[0]
            if not up and not CFG["auto_manage_ollama"]:
                raise ApiError(503, "offline", "The host's computer is offline right now.")
            # Open the stream now so the page can say "waking up" while Ollama starts / loads the model.
            starting = Ollama.warming or not up or not Ollama.model_loaded()
            self.start_stream()
            if starting:
                self.event({"type": "status", "state": "starting"})
            if not Ollama.ensure():  # waits on the same lock as a warm-up that is already starting Ollama
                return self.event({"type": "error", "error": "offline", "message": "The host's computer is offline right now."})
            now = time.time()
            with LOCK:
                if conv is None:
                    title = " ".join(text.split())
                    title = title if len(title) <= 50 else title[:50].rsplit(" ", 1)[0] + "..."
                    conv_id = db.execute("INSERT INTO conversations (account_id, title, created, updated, project_id, "
                                         "reply_style) VALUES (?,?,?,?,?,?)",
                                         (acc["id"], title, now, now, project_id, style)).lastrowid
                conv_id = int(conv_id)
                if regenerate:
                    user_id = convo[-1]["id"]
                    db.execute("DELETE FROM messages WHERE conversation_id=? AND id>?", (conv_id, user_id))
                elif edit_id is not None:
                    user_id = edit_id
                    db.execute("DELETE FROM messages WHERE conversation_id=? AND id>?", (conv_id, user_id))
                    db.execute("UPDATE messages SET content=? WHERE id=?", (text, user_id))
                else:
                    user_id = db.execute("INSERT INTO messages (conversation_id, role, content, attachments, created) "
                                         "VALUES (?,?,?,?,?)", (conv_id, "user", text,
                                                                json.dumps(attachments) if attachments else None,
                                                                now)).lastrowid
            self.stream_reply(acc, conv_id, user_id, messages, max_reply, notices)
        finally:
            Ollama.active -= 1
            Ollama.last_activity = time.time()

    def build_prompt(self, acc, owner, project, convo, style=""):
        """Fit system text, project files and history into the account's context window.

        Priority: system prompt + custom instructions + project instructions + the newest user message (must fit,
        otherwise a clear error), then project files, then older history newest-first.
        """
        ctx = CFG["owner_context_tokens"] if owner else CFG["guest_context_tokens"]
        reply = CFG["owner_max_reply_tokens"] if owner else CFG["guest_max_reply_tokens"]
        room = ctx - reply
        system = [CFG["system_prompt"]] if CFG["system_prompt"] else []  # assistant identity, always first
        if acc["instructions"]:
            system.append("The user's custom instructions:\n" + acc["instructions"])
        if REPLY_STYLES.get(style):
            system.append(REPLY_STYLES[style])
        if project:
            system.append(f'This chat is part of the project "{project["name"]}".')
            if project["instructions"]:
                system.append("Project instructions:\n" + project["instructions"])
        last = convo[-1]
        last_text = user_prompt(last["content"], last["attachments"])
        fixed = est("\n\n".join(system)) + est(last_text)
        if fixed > room:
            what = "This message and its attachments are" if last["attachments"] else "This message is"
            raise ApiError(413, "doesnt_fit", f"{what} too big for the context window: about {fixed:,} tokens, "
                           f"but this account can use about {room:,}. Remove a file or shorten the text.",
                           needed=fixed, limit=room)
        room -= fixed
        history = [{"role": r["role"], "content": user_prompt(r["content"], r["attachments"])} for r in convo[:-1]]
        notices = []
        if project:
            # Files may use the room left, but keep up to a quarter of it for recent history.
            hist_cost = sum(est(m["content"]) for m in history)
            file_room = room - min(room // 4, hist_cost)
            left_out = []
            files = q("SELECT name, content FROM project_files WHERE project_id=? ORDER BY id", (project["id"],))
            blocks = []
            for f in files:
                block = file_block(f["name"], f["content"])
                if est(block) <= file_room:
                    file_room -= est(block)
                    room -= est(block)
                    blocks.append(block)
                else:
                    left_out.append(f["name"])
            if blocks:
                system.append("Project files:\n\n" + "\n\n".join(blocks))
            if left_out:
                notices.append("Some project files didn't fit in the context window and were left out: "
                               + ", ".join(left_out))
        picked = []
        for m in reversed(history):
            cost = est(m["content"])
            if cost > room:
                break
            room -= cost
            picked.append(m)
        messages = ([{"role": "system", "content": "\n\n".join(system)}] if system else []) + picked[::-1] + \
            [{"role": "user", "content": last_text}]
        return messages, reply, notices

    def start_stream(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("X-Accel-Buffering", "no")
        self.cors_headers()
        self.end_headers()

    def event(self, obj):
        self.wfile.write(b"data: " + json.dumps(obj).encode() + b"\n\n")
        self.wfile.flush()

    def stream_reply(self, acc, conv_id, user_id, messages, max_reply, notices):
        req = urllib.request.Request(
            CFG["ollama_url"] + "/api/chat",
            data=json.dumps({
                "model": CFG["model"], "messages": messages, "stream": True, "think": False,
                # Same num_ctx for everyone so Ollama never reloads the model; guests get
                # a smaller window because build_prompt trims their history.
                "options": {**Ollama.load_options(), "num_predict": max_reply},
            }).encode(),
            headers={"Content-Type": "application/json"},
        )
        try:
            resp = urllib.request.urlopen(req, timeout=600)  # returns once Ollama has loaded the model
        except Exception as e:
            log("ollama request failed:", e)
            return self.event({"type": "error", "error": "offline", "message": "The model is not responding right now."})
        event = self.event

        reply, usage, client_gone, produced = [], None, False, 0
        # Closing resp (the with-block) drops the upstream connection, which makes Ollama stop generating.
        with resp:
            try:
                event({"type": "meta", "conversation_id": conv_id, "user_message_id": user_id})
                event({"type": "status", "state": "ready"})
                for n in notices:
                    event({"type": "notice", "message": n})
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
                        produced += 1  # Ollama streams one token per chunk
                        reply.append(piece)
                        event({"type": "delta", "text": piece})
                    if chunk.get("done"):
                        usage = (chunk.get("prompt_eval_count", 0), chunk.get("eval_count", 0))
                        break
            except (BrokenPipeError, ConnectionResetError):
                client_gone = True
            except Exception as e:
                log("stream error:", type(e).__name__, e)

        if usage is None:  # stopped or failed: prompt estimate + only the tokens actually produced
            usage = (sum(est(m["content"]) for m in messages), produced)
        full = "".join(reply)
        now = time.time()
        asst_id = None
        if full:
            with LOCK:
                asst_id = db.execute("INSERT INTO messages (conversation_id, role, content, created) VALUES (?,?,?,?)",
                                     (conv_id, "assistant", full, now)).lastrowid
        q("UPDATE conversations SET updated=? WHERE id=?", (now, conv_id))
        acc = charge(acc["id"], usage[0] + usage[1])
        if not client_gone and acc:
            try:
                event({"type": "done", "assistant_message_id": asst_id,
                       "usage": {"prompt": usage[0], "completion": usage[1], "total": usage[0] + usage[1]},
                       "budget": budget(acc)})
            except (BrokenPipeError, ConnectionResetError):
                pass

    # ---- admin
    def find_account(self, acc_id):
        acc = get_account("id=?", (acc_id,))
        if not acc:
            raise ApiError(404, "not_found", "Account not found")
        return acc

    def admin_activity(self):
        """Counts only - never chat content."""
        self.require_owner()
        now = time.time()
        day0 = datetime.datetime.combine(datetime.date.today(), datetime.time()).timestamp()
        week0 = now - 7 * 86400
        accounts = []
        for a in q("SELECT id, username, last_active FROM accounts ORDER BY is_owner DESC, username"):
            t = q1("SELECT COUNT(*) AS n, COALESCE(SUM(tokens), 0) AS tok FROM usage_events WHERE account_id=? AND ts>=?",
                   (a["id"], day0))
            w = q1("SELECT COUNT(*) AS n, COALESCE(SUM(tokens), 0) AS tok FROM usage_events WHERE account_id=? AND ts>=?",
                   (a["id"], week0))
            accounts.append({"username": a["username"], "last_active": a["last_active"],
                             "replies_today": t["n"], "tokens_today": t["tok"],
                             "replies_7d": w["n"], "tokens_7d": w["tok"]})
        hours = [0] * 24
        for r in q("SELECT CAST(strftime('%H', ts, 'unixepoch', 'localtime') AS INTEGER) AS h, COUNT(*) AS n "
                   "FROM usage_events WHERE ts>=? GROUP BY h", (week0,)):
            hours[r["h"]] = r["n"]
        self.send_json(200, {"accounts": accounts, "hours_7d": hours})

    def system_json(self):
        return {"nook_off": bool(flag("nook_off")), "maintenance": bool(flag("maintenance")),
                "announcement": flag("announcement")}

    def admin_system(self):
        self.require_owner()
        self.send_json(200, self.system_json())

    def admin_edit_system(self):
        self.require_owner()
        body = self.read_json()
        if "announcement" in body:
            text = " ".join(str(body["announcement"] or "").split())
            if len(text) > 300:
                raise ApiError(400, "too_long", "Keep the announcement under 300 characters")
            set_flag("announcement", text)
        if "maintenance" in body:
            set_flag("maintenance", "1" if body["maintenance"] else "")
        if "nook_off" in body:
            set_flag("nook_off", "1" if body["nook_off"] else "")
            log("Nook switched", "off" if body["nook_off"] else "on", "by the owner")
            if body["nook_off"] and Ollama.status()[0]:
                threading.Thread(target=Ollama.stop_now, daemon=True).start()
        self.send_json(200, self.system_json())

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
        with throttle_lock:
            guest_requests.pop(acc_id, None)  # ids can be reused by the next account
            warm_requests.pop(acc_id, None)
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
    if "--nook-off" in sys.argv or "--nook-on" in sys.argv:
        off = "--nook-off" in sys.argv
        set_flag("nook_off", "1" if off else "")
        print(f"Nook is now switched {'off' if off else 'on'} (a running gateway picks this up within seconds).")
        if off and Ollama.status()[0]:
            Ollama.stop_now()
        return
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
