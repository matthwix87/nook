#!/usr/bin/env python3
"""End-to-end tests against a live proxy and a fake Ollama (tests/fake_ollama.py).

Starts its own proxy on a test port with a throwaway data dir, so it never touches data/
or the real Ollama. The proxy auto-starts the fake Ollama through OLLAMA_ON_CMD and stops
it again after the idle timeout through OLLAMA_OFF_CMD.
Run:  python3 tests/test_gateway.py
"""
import http.client
import json
import os
import re
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORT = 8797
FAKE_PORT = 11597
FAKE = f"http://127.0.0.1:{FAKE_PORT}"
BASE = f"http://127.0.0.1:{PORT}"
DATA = tempfile.mkdtemp(prefix="gateway-test-")
FAKE_SCRIPT = os.path.join(ROOT, "tests", "fake_ollama.py")
ENV = {**os.environ, "DATA_DIR": DATA, "PORT": str(PORT), "AUTO_MANAGE_OLLAMA": "1",
       "OLLAMA_URL": FAKE, "MODEL": "qwen3.8:27b",
       "OLLAMA_ON_CMD": f"sh -c 'exec {sys.executable} {FAKE_SCRIPT} {FAKE_PORT} >/dev/null 2>&1 &'",
       "OLLAMA_OFF_CMD": f"pkill -f 'fake_ollama.py {FAKE_PORT}'",
       "OLLAMA_IDLE_MINUTES": "0.25", "GUEST_REQUESTS_PER_MINUTE": "3",
       "MAX_PROJECT_BYTES": "300000", "FAKE_START_DELAY": "1.5", "ALLOWED_ORIGINS": "https://example.github.io"}

results = []


def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    print(("PASS " if cond else "FAIL ") + name + (f"  [{detail}]" if detail and not cond else ""), flush=True)


def req(method, path, body=None, auth=None, headers=None, raw=False, timeout=300):
    h = dict(headers or {})
    if auth:
        h["Authorization"] = f"Bearer {auth[0]}:{auth[1]}"
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        h["Content-Type"] = "application/json"
    r = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    try:
        resp = urllib.request.urlopen(r, timeout=timeout)
    except urllib.error.HTTPError as e:
        resp = e
    if raw:
        return resp
    text = resp.read()
    try:
        return resp.status, json.loads(text) if text else None, resp.headers
    except ValueError:
        return resp.status, text, resp.headers


def chat(auth, message, conv=None, **extra):
    """Returns (status, events) - events parsed from the SSE stream."""
    resp = req("POST", "/api/chat", {"message": message, "conversation_id": conv, **extra}, auth=auth, raw=True,
               timeout=600)
    if resp.status != 200:
        return resp.status, json.loads(resp.read())
    events = []
    buf = b""
    for chunk in iter(lambda: resp.read1(4096), b""):
        buf += chunk
        while b"\n\n" in buf:
            line, buf = buf.split(b"\n\n", 1)
            if line.startswith(b"data: "):
                events.append(json.loads(line[6:]))
    return 200, events


def reply_of(events):
    return "".join(e["text"] for e in events if e.get("type") == "delta")


def meta(events):
    return next(e for e in events if e.get("type") == "meta")


def done_of(events):
    return next((e for e in events if e.get("type") == "done"), None)


def fake(path):
    with urllib.request.urlopen(FAKE + path, timeout=5) as r:
        return json.load(r)


def est(text):
    return len(text) // 3 + 4


def ollama_up():
    try:
        urllib.request.urlopen(FAKE + "/api/tags", timeout=2)
        return True
    except Exception:
        return False


def start_server(extra_env=None):
    log = open(os.path.join(DATA, "server.log"), "a")
    p = subprocess.Popen([sys.executable, os.path.join(ROOT, "server", "server.py")],
                         env={**ENV, **(extra_env or {})}, stdout=log, stderr=subprocess.STDOUT,
                         stdin=subprocess.DEVNULL)
    for _ in range(40):
        try:
            urllib.request.urlopen(BASE + "/status", timeout=1)
            return p
        except Exception:
            time.sleep(0.25)
    raise SystemExit("server did not start")


def main():
    subprocess.run(["pkill", "-f", f"fake_ollama.py {FAKE_PORT}"], capture_output=True)
    time.sleep(0.5)
    srv = start_server()
    try:
        run_tests()
        run_feature_tests()
    finally:
        srv.terminate()
        srv.wait()

    # /status with auto-manage off and Ollama down -> not available
    subprocess.run(["pkill", "-f", f"fake_ollama.py {FAKE_PORT}"], capture_output=True)
    time.sleep(0.5)
    srv = start_server({"AUTO_MANAGE_OLLAMA": "0"})
    try:
        s, st, _ = req("GET", "/status")
        check("status (auto-manage off, Ollama down) reports unavailable",
              s == 200 and st["ollama"] is False and st["available"] is False, st)
        s, body = chat(owner_auth(), "hello")
        check("chat with auto-manage off and Ollama down -> 503 offline (no stream)",
              s == 503 and body["error"] == "offline", body)
    finally:
        srv.terminate()
        srv.wait()

    log = open(os.path.join(DATA, "server.log")).read()
    check("no keys appear in the server log", all(k not in log for k in KEYS_SEEN), "key found in log")
    run_migration_tests()
    subprocess.run(["pkill", "-f", f"fake_ollama.py {FAKE_PORT}"], capture_output=True)
    shutil.rmtree(DATA, ignore_errors=True)

    passed = sum(ok for _, ok in results)
    print(f"\n{passed}/{len(results)} passed")
    sys.exit(0 if passed == len(results) else 1)


KEYS_SEEN = []


def run_tests():
    # ---- owner first-run key
    key_file = os.path.join(DATA, "owner_key.txt")
    check("owner key created on first run (written to data/owner_key.txt when not a TTY)", os.path.exists(key_file))
    owner_key = open(key_file).read().split("key: ")[1].strip()
    KEYS_SEEN.append(owner_key)
    check("owner key file is mode 600", oct(os.stat(key_file).st_mode & 0o777) == "0o600")
    OWNER = ("owner", owner_key)
    s, me, _ = req("POST", "/api/login", auth=OWNER)
    check("owner login", s == 200 and me["is_owner"] and me["budget"]["unlimited"])

    # ---- status while Ollama is down
    s, st, _ = req("GET", "/status")
    check("status when Ollama down", s == 200 and st["ollama"] is False and st["auto_start"] is True, st)

    # ---- username validation
    bad = ["fuck", "FuCk_you", "f.u.c.k", "fuuuuck", "sh1t", "b1tch", "a55", "BigAss", "ass_man",
           "n1gg3r", "n-i-g-g-a", "f4gg0t", "r3tard", "admin", "Admin_Bob", "root", "owner", "Owner",
           "sexy_girl", "p0rn_king", "c0ck", "hitler88", "d1ck", "cum_lord", "xxxlover",
           "real_moderator", "0fficial", "k1ke", "wh0re", "fvck", "ab", "this_name_is_way_too_long",
           "bad name", "emoji☺"]
    good = ["matt", "jas_bracelets", "cassandra", "classic.gamer", "grasshopper", "scunthorpe_fc",
            "analyst42", "assassin99", "bass-player", "dickens_fan", "sussex.lad", "titan", "Bob",
            "raccoon", "hancock", "skyscraper"]
    rejected = []
    for name in bad:
        s, body, _ = req("POST", "/api/admin/accounts", {"username": name}, auth=OWNER)
        if s == 400 and name.lower().strip("0") not in body["message"].lower():
            rejected.append(name)
    check(f"bad usernames rejected ({len(rejected)}/{len(bad)})", len(rejected) == len(bad),
          set(bad) - set(rejected))
    s, body, _ = req("POST", "/api/admin/accounts", {"username": "sh1thead"}, auth=OWNER)
    check("rejection message is generic", body["message"] == "That username isn't allowed", body)
    accepted = []
    for name in good:
        s, body, _ = req("POST", "/api/admin/accounts", {"username": name, "daily_allowance": 1}, auth=OWNER)
        if s == 201:
            accepted.append(name)
            KEYS_SEEN.append(body["key"])
            req("DELETE", f"/api/admin/accounts/{body['account']['id']}", auth=OWNER)
    check(f"legit usernames accepted ({len(accepted)}/{len(good)})", len(accepted) == len(good),
          set(good) - set(accepted))

    # ---- create guest + login
    s, body, _ = req("POST", "/api/admin/accounts", {"username": "alice_test", "daily_allowance": 5000}, auth=OWNER)
    check("create guest returns key once", s == 201 and len(body["key"]) >= 40)
    alice_id, alice_key = body["account"]["id"], body["key"]
    KEYS_SEEN.append(alice_key)
    ALICE = ("alice_test", alice_key)
    s, _, _ = req("POST", "/api/admin/accounts", {"username": "ALICE_TEST"}, auth=OWNER)
    check("duplicate username (case-insensitive) rejected", s == 409)
    s, me, _ = req("POST", "/api/login", auth=ALICE)
    check("guest login", s == 200 and not me["is_owner"])
    s, _, _ = req("POST", "/api/login", auth=("alice_test", "wrong"))
    check("wrong key rejected", s == 401)

    # ---- real streamed chat (auto-starts Ollama)
    t0 = time.time()
    s, events = chat(ALICE, "Count from 1 to 10, separated by spaces. Reply with only the numbers.")
    deltas = [e for e in events if e.get("type") == "delta"] if s == 200 else []
    done = next((e for e in events if e.get("type") == "done"), None) if s == 200 else None
    reply = "".join(e["text"] for e in deltas)
    check(f"Ollama auto-started and streamed a reply ({time.time() - t0:.0f}s, {len(deltas)} chunks)",
          s == 200 and len(deltas) >= 3 and done, f"{s} {events if s != 200 else reply}")
    print("     reply:", reply[:80].replace("\n", " "))
    used = done["usage"]["total"] if done else 0
    conv_id = meta(events)["conversation_id"] if s == 200 else None
    s, me, _ = req("GET", "/api/me", auth=ALICE)
    check(f"tokens charged ({used} tokens, prompt+completion)",
          used > 0 and me["daily_used"] == used and me["budget"]["daily_left"] == 5000 - used, me)

    # ---- follow-up in same conversation, history kept
    s, events = chat(ALICE, "What was the last number you wrote? Reply with just the number.", conv_id)
    reply = "".join(e["text"] for e in events if e.get("type") == "delta") if s == 200 else ""
    check("follow-up uses conversation history", s == 200 and "10" in reply, reply)

    # ---- history
    s, convs, _ = req("GET", "/api/conversations", auth=ALICE)
    check("list conversations", s == 200 and len(convs) == 1 and convs[0]["id"] == conv_id)
    s, conv, _ = req("GET", f"/api/conversations/{conv_id}", auth=ALICE)
    check("open conversation has 4 messages", s == 200 and [m["role"] for m in conv["messages"]] ==
          ["user", "assistant", "user", "assistant"])

    # ---- owner chat (unlimited)
    s, events = chat(OWNER, "Say hello in one word.")
    done = next((e for e in events if e.get("type") == "done"), None) if s == 200 else None
    check("owner chat works, budget unlimited", s == 200 and done and done["budget"]["unlimited"])
    owner_conv = meta(events)["conversation_id"] if s == 200 else 0
    s, _, _ = req("GET", f"/api/conversations/{owner_conv}", auth=ALICE)
    check("guest cannot open someone else's conversation", s == 404)

    # ---- budget exhaustion and top-up
    s, me, _ = req("GET", "/api/me", auth=ALICE)
    req("PATCH", f"/api/admin/accounts/{alice_id}", {"daily_allowance": me["daily_used"]}, auth=OWNER)
    s, body = chat(ALICE, "hi")
    check("exhausted budget rejected with clear error", s == 402 and body["error"] == "no_tokens", body)
    s, acc, _ = req("PATCH", f"/api/admin/accounts/{alice_id}", {"add_topup": 3000}, auth=OWNER)
    check("top-up added", s == 200 and acc["topup"] == 3000)
    s, events = chat(ALICE, "Reply with just: ok")
    done = next((e for e in events if e.get("type") == "done"), None) if s == 200 else None
    check("chat works again after top-up, charged from top-up",
          done and done["budget"]["daily_left"] == 0 and done["budget"]["topup"] == 3000 - done["usage"]["total"],
          done)

    # ---- rate limit (3/min configured)
    for _ in range(4):
        s, body = chat(ALICE, "Reply with just: ok")
        if s != 200:
            break
    check("guest rate limit", s == 429 and body["error"] == "rate_limited", body)

    # ---- delete conversation
    s, _, _ = req("DELETE", f"/api/conversations/{conv_id}", auth=ALICE)
    s2, _, _ = req("GET", f"/api/conversations/{conv_id}", auth=ALICE)
    check("delete conversation", s == 200 and s2 == 404)

    # ---- temporary disable, auto re-enable, manual re-enable
    s, acc, _ = req("POST", f"/api/admin/accounts/{alice_id}/disable", {"minutes": 0.05}, auth=OWNER)
    check("disable for 3 seconds", s == 200 and acc["disabled_until"] > time.time())
    s, body, _ = req("POST", "/api/login", auth=ALICE)
    check("disabled user gets message with end time", s == 403 and body["error"] == "disabled" and body["until"] > 0, body)
    time.sleep(3.5)
    s, _, _ = req("POST", "/api/login", auth=ALICE)
    check("auto re-enabled after the duration", s == 200)
    req("POST", f"/api/admin/accounts/{alice_id}/disable", {"minutes": 60}, auth=OWNER)
    s1, _, _ = req("POST", "/api/login", auth=ALICE)
    req("POST", f"/api/admin/accounts/{alice_id}/enable", auth=OWNER)
    s2, _, _ = req("POST", "/api/login", auth=ALICE)
    check("manual re-enable", s1 == 403 and s2 == 200)

    # ---- search
    s, found, _ = req("POST", "/api/admin/accounts/search", {"q": "alice"}, auth=OWNER)
    check("search by username", s == 200 and [a["username"] for a in found] == ["alice_test"])
    s, found, _ = req("POST", "/api/admin/accounts/search", {"q": alice_key}, auth=OWNER)
    check("search by key", s == 200 and [a["username"] for a in found] == ["alice_test"])
    s, found, _ = req("POST", "/api/admin/accounts/search", {"q": "x" + alice_key[1:]}, auth=OWNER)
    check("search by wrong key finds nothing", s == 200 and found == [])

    # ---- owner protections
    s1, _, _ = req("POST", "/api/admin/accounts/1/disable", {"minutes": 5}, auth=OWNER)
    s2, _, _ = req("DELETE", "/api/admin/accounts/1", auth=OWNER)
    s3, me, _ = req("GET", "/api/me", auth=OWNER)
    check("owner cannot be disabled or deleted", s1 == 400 and s2 == 400 and s3 == 200)

    # ---- guest cannot use admin API
    codes = [req("GET", "/api/admin/accounts", auth=ALICE)[0],
             req("POST", "/api/admin/accounts", {"username": "sneaky"}, auth=ALICE)[0],
             req("POST", "/api/admin/accounts/search", {"q": "a"}, auth=ALICE)[0],
             req("POST", "/api/admin/accounts/1/disable", {"minutes": 5}, auth=ALICE)[0],
             req("PATCH", f"/api/admin/accounts/{alice_id}", {"add_topup": 99999}, auth=ALICE)[0]]
    check("guest gets 403 on admin endpoints", codes == [403] * 5, codes)
    s, _, _ = req("GET", "/api/admin/accounts")
    check("no auth gets 401 on admin endpoints", s == 401)

    # ---- rotate key
    s, body, _ = req("POST", f"/api/admin/accounts/{alice_id}/rotate", auth=OWNER)
    KEYS_SEEN.append(body["key"])
    s1, _, _ = req("GET", "/api/me", auth=ALICE)
    s2, _, _ = req("GET", "/api/me", auth=("alice_test", body["key"]))
    check("rotate key: old key dead, new key works", s == 200 and s1 == 401 and s2 == 200)

    # ---- CORS
    s, _, h = req("GET", "/status", headers={"Origin": "https://evil.example.com"})
    check("CORS: unknown origin rejected", s == 403 and "Access-Control-Allow-Origin" not in h)
    s, _, h = req("OPTIONS", "/api/chat", headers={"Origin": "https://evil.example.com",
                                                   "Access-Control-Request-Method": "POST"})
    check("CORS: unknown origin preflight rejected", s == 403)
    s, _, h = req("OPTIONS", "/api/chat", headers={"Origin": "https://example.github.io",
                                                   "Access-Control-Request-Method": "POST"})
    check("CORS: configured origin allowed", s == 204 and h["Access-Control-Allow-Origin"] == "https://example.github.io")
    s, _, h = req("GET", "/status", headers={"Origin": "http://localhost:8788"})
    check("CORS: localhost allowed", s == 200 and h["Access-Control-Allow-Origin"] == "http://localhost:8788")

    # ---- request size limit
    r = urllib.request.Request(BASE + "/api/chat", data=b"x" * 2200000, method="POST",
                               headers={"Authorization": f"Bearer alice_test:{body['key']}"})
    try:
        code = urllib.request.urlopen(r, timeout=10).status
    except urllib.error.HTTPError as e:
        code = e.code
    except Exception:
        code = "conn-closed"
    check("oversized request rejected", code in (413, "conn-closed"), code)

    # ---- login throttling (simulated client IP via CF-Connecting-IP)
    ip = {"CF-Connecting-IP": "203.0.113.7"}
    for _ in range(10):
        req("POST", "/api/login", auth=("alice_test", "nope"), headers=ip)
    s1, _, _ = req("POST", "/api/login", auth=OWNER, headers=ip)
    s2, _, _ = req("POST", "/api/login", auth=OWNER)
    check("login lockout after 10 failures (other IPs unaffected)", s1 == 429 and s2 == 200, (s1, s2))

    # ---- delete account (and its chats)
    s, _, _ = req("DELETE", f"/api/admin/accounts/{alice_id}", auth=OWNER)
    s2, _, _ = req("GET", "/api/me", auth=("alice_test", body["key"]))
    check("delete account", s == 200 and s2 == 401)

    # ---- status while up, then idle auto-stop
    s, st, _ = req("GET", "/status")
    check("status when Ollama up", st["ollama"] is True and st["model_available"] is True, st)
    print("     waiting for idle auto-stop (idle timeout 15s)...", flush=True)
    stopped = False
    for _ in range(30):
        time.sleep(3)
        if not ollama_up():
            stopped = True
            break
    check("Ollama stopped automatically after idle timeout", stopped)
    s, st, _ = req("GET", "/status")
    check("status after auto-stop reports Ollama down", st["ollama"] is False, st)


def owner_auth():
    return ("owner", open(os.path.join(DATA, "owner_key.txt")).read().split("key: ")[1].strip())


def new_guest(owner, name, allowance=500000):
    s, body, _ = req("POST", "/api/admin/accounts", {"username": name, "daily_allowance": allowance}, auth=owner)
    KEYS_SEEN.append(body["key"])
    return (name, body["key"])


def chat_and_stop(auth, message, after=5):
    """Start a streamed chat, read `after` deltas, then drop the connection (what the Stop button does)."""
    body = json.dumps({"message": message}).encode()
    sock = socket.create_connection(("127.0.0.1", PORT), timeout=30)
    sock.sendall(f"POST /api/chat HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {auth[0]}:{auth[1]}\r\n"
                 f"Content-Type: application/json\r\nContent-Length: {len(body)}\r\n\r\n".encode() + body)
    buf, deltas, conv = b"", 0, None
    while deltas < after:
        data = sock.recv(4096)
        if not data:
            break
        buf += data
        for m in re.finditer(rb"data: (\{.*?\})\n\n", buf):
            ev = json.loads(m.group(1))
            conv = ev.get("conversation_id", conv)
        deltas = buf.count(b'"type": "delta"')
    if not buf.startswith(b"HTTP/1.0 200") and not buf.startswith(b"HTTP/1.1 200"):
        raise SystemExit("chat_and_stop: " + buf[:200].decode(errors="replace"))
    sock.shutdown(socket.SHUT_RDWR)
    sock.close()
    return conv


def wait_for(cond, seconds):
    for _ in range(int(seconds * 4)):
        if cond():
            return True
        time.sleep(0.25)
    return False


def kill_fake():
    subprocess.run(["pkill", "-f", f"fake_ollama.py {FAKE_PORT}"], capture_output=True)
    wait_for(lambda: not ollama_up(), 5)


def run_warm_tests(OWNER, BOB, CAROL):
    check("warm: precondition - Ollama is down (idle-stopped)", not ollama_up())
    s, body, _ = req("POST", "/api/warm")
    check("warm: unauthenticated rejected", s == 401)
    t0 = time.time()
    s, body, _ = req("POST", "/api/warm", auth=BOB)
    check("warm: returns 202 immediately", s == 202 and body["started"] and time.time() - t0 < 1, (s, body))
    loaded = wait_for(lambda: ollama_up() and fake("/_stats")["loads"], 20)
    check("warm: starts Ollama when down and loads the model", loaded)
    s2, body2, _ = req("POST", "/api/warm", auth=BOB)
    check("warm: second call within 60s is a no-op", s2 == 202 and body2["started"] is False, body2)
    me = req("GET", "/api/me", auth=BOB)[1]
    check("warm: charges no tokens", me["daily_used"] == 0 and me["total_tokens"] == 0, me)
    # Idle timeout is 15s; a second warm-up at ~10s must keep it alive at ~22s.
    time.sleep(10)
    req("POST", "/api/warm", auth=CAROL)
    time.sleep(12)
    check("warm: refreshes the idle timer (Ollama not stopped)", ollama_up())
    chat(OWNER, "hello")
    loads = fake("/_stats")["loads"]
    check("warm: load uses the same num_ctx as chats",
          loads and all(o == {"num_ctx": fake("/_last")["options"]["num_ctx"]} for o in loads), loads)
    # status events: "starting" while Ollama starts / loads, then "ready" before the first token
    kill_fake()
    s, ev = chat(OWNER, "status please")
    types = [e["type"] + (":" + e["state"] if e["type"] == "status" else "") for e in ev]
    first_delta = types.index("delta") if "delta" in types else 99
    check("status: Ollama down -> starting first, ready before first token",
          s == 200 and types[0] == "status:starting" and "status:ready" in types[:first_delta] and done_of(ev), types)
    s, ev = chat(OWNER, "status again")
    types = [e["type"] + (":" + e["state"] if e["type"] == "status" else "") for e in ev]
    check("status: model already loaded -> no starting, just ready", "status:starting" not in types
          and types.index("status:ready") < types.index("delta"), types)
    kill_fake()
    req("POST", "/api/warm", auth=OWNER)
    time.sleep(0.3)
    s, ev = chat(OWNER, "sent while warming up")
    n = subprocess.run(["pgrep", "-fc", f"fake_ollama.py {FAKE_PORT}"], capture_output=True, text=True).stdout.strip()
    check("status: chat during a warm-up start says starting, waits for it, one Ollama start",
          s == 200 and ev[0].get("state") == "starting" and done_of(ev) and n == "1", (ev[:2], n))

    s, b, _ = req("POST", "/api/admin/accounts", {"username": "broke_test", "daily_allowance": 0}, auth=OWNER)
    KEYS_SEEN.append(b["key"])
    s1, _, _ = req("POST", "/api/warm", auth=("broke_test", b["key"]))
    req("POST", f"/api/admin/accounts/{b['account']['id']}/disable", {"minutes": 5}, auth=OWNER)
    s2, body, _ = req("POST", "/api/warm", auth=("broke_test", b["key"]))
    check("warm: no-budget and disabled accounts rejected", s1 == 402 and s2 == 403 and body["error"] == "disabled", (s1, s2))


def run_feature_tests():
    OWNER = owner_auth()
    BOB = new_guest(OWNER, "bob_test")
    CAROL = new_guest(OWNER, "carol_test")
    run_warm_tests(OWNER, BOB, CAROL)

    # ---- A1 stop: client disconnect stops Ollama and charges only produced tokens
    before = req("GET", "/api/me", auth=BOB)[1]["daily_used"]
    conv = chat_and_stop(BOB, "SLOW please count")
    time.sleep(1.5)
    stats = fake("/_stats")
    sent_messages = fake("/_last")["messages"]
    s, c, _ = req("GET", f"/api/conversations/{conv}", auth=BOB)
    saved = c["messages"][-1]["content"] if s == 200 else ""
    produced = len(saved.split())
    charged = req("GET", "/api/me", auth=BOB)[1]["daily_used"] - before
    check("stop: upstream Ollama request closed early", stats["cancelled"] >= 1 and stats["sent"] < 100, stats)
    check(f"stop: partial reply saved ({produced} tokens)", c["messages"][-1]["role"] == "assistant" and 0 < produced < 100,
          c["messages"])
    check("stop: charged prompt estimate + produced tokens only",
          charged == sum(est(m["content"]) for m in sent_messages) + produced, (charged, produced))

    # ---- A2 edit + regenerate
    s, ev = chat(OWNER, "first question")
    conv = meta(ev)["conversation_id"]
    u1 = meta(ev)["user_message_id"]
    chat(OWNER, "second question", conv)
    s, c, _ = req("GET", f"/api/conversations/{conv}", auth=OWNER)
    check("messages carry ids", len(c["messages"]) == 4 and c["messages"][0]["id"] == u1)
    s, ev = chat(OWNER, "first question, edited", conv, edit_message_id=u1)
    s, c, _ = req("GET", f"/api/conversations/{conv}", auth=OWNER)
    check("edit: conversation truncated after edited message and re-answered",
          [m["role"] for m in c["messages"]] == ["user", "assistant"] and c["messages"][0]["id"] == u1
          and c["messages"][0]["content"] == "first question, edited", c["messages"])
    check("edit: model saw only the edited history",
          [m["content"] for m in fake("/_last")["messages"] if m["role"] != "system"] == ["first question, edited"])
    old_asst = c["messages"][1]["created"]
    s, ev = chat(OWNER, None, conv, regenerate=True)
    s, c, _ = req("GET", f"/api/conversations/{conv}", auth=OWNER)
    check("regenerate: last reply replaced, user message kept",
          s == 200 and len(c["messages"]) == 2 and c["messages"][1]["created"] != old_asst
          and done_of(ev)["assistant_message_id"] == c["messages"][1]["id"], c["messages"])
    s1, _ = chat(CAROL, None, conv, regenerate=True)
    s2, _ = chat(CAROL, "hijack", conv, edit_message_id=u1)
    check("edit/regenerate of another account's chat rejected", s1 == 404 and s2 == 404, (s1, s2))

    # ---- A3 search, rename, pin, auto-title
    s, ev = chat(OWNER, "Tell me about the zebra123 migration patterns of the savanna and many other things here")
    zconv = meta(ev)["conversation_id"]
    s, convs, _ = req("GET", "/api/conversations", auth=OWNER)
    title = next(x["title"] for x in convs if x["id"] == zconv)
    check("auto-title from first message, truncated", title.startswith("Tell me about the zebra123") and
          title.endswith("...") and len(title) <= 53, title)
    s, found, _ = req("GET", "/api/conversations?q=ZEBRA123", auth=OWNER)
    check("search finds message text (case-insensitive) with snippet",
          [x["id"] for x in found] == [zconv] and "zebra123" in found[0].get("snippet", ""), found)
    s, r, _ = req("PATCH", f"/api/conversations/{conv}", {"title": "Giraffe notes"}, auth=OWNER)
    s, found, _ = req("GET", "/api/conversations?q=giraffe", auth=OWNER)
    check("rename + search by title", r["title"] == "Giraffe notes" and [x["id"] for x in found] == [conv])
    s, found, _ = req("GET", "/api/conversations?q=%25", auth=OWNER)
    check("search treats % literally", s == 200 and found == [], found)
    req("PATCH", f"/api/conversations/{conv}", {"pinned": True}, auth=OWNER)
    s, convs, _ = req("GET", "/api/conversations", auth=OWNER)
    check("pinned chat floats to the top", convs[0]["id"] == conv and convs[0]["pinned"] == 1)
    s, found, _ = req("GET", "/api/conversations?q=zebra123", auth=CAROL)
    s2, _, _ = req("PATCH", f"/api/conversations/{conv}", {"title": "pwned"}, auth=CAROL)
    s3, _, _ = req("DELETE", f"/api/conversations/{conv}", auth=CAROL)
    check("other account can't search, rename or delete my chats", found == [] and s2 == 404 and s3 == 404)

    # ---- A4 custom instructions
    s, st, _ = req("PATCH", "/api/settings", {"instructions": "Always answer in French."}, auth=OWNER)
    check("custom instructions saved", s == 200 and st["instructions"] == "Always answer in French.")
    chat(OWNER, "hello")
    msgs = fake("/_last")["messages"]
    check("custom instructions injected as system message",
          msgs[0]["role"] == "system" and "Always answer in French." in msgs[0]["content"], msgs[0])
    s, body, _ = req("PATCH", "/api/settings", {"instructions": "x" * 2001}, auth=OWNER)
    check("custom instructions capped at 2000 chars", s == 400 and body["error"] == "too_long")
    s, st, _ = req("GET", "/api/settings", auth=CAROL)
    check("instructions are per account", st["instructions"] == "")
    chat(CAROL, "hello")
    guest_msgs = fake("/_last")["messages"]
    check("Nook system prompt is the first system message for owner and guest",
          all(m[0]["role"] == "system" and m[0]["content"].startswith("You are Nook") for m in (msgs, guest_msgs))
          and [x["role"] for x in guest_msgs].count("system") == 1
          and msgs[0]["content"].index("You are Nook") < msgs[0]["content"].index("Always answer in French."),
          (msgs[0], guest_msgs[0]))

    # ---- A5 file attachments
    good = {"name": "hello.py", "content": "print('hi')\n"}
    bad_cases = {
        "binary extension rejected": ([{"name": "cat.png", "content": "abc"}], "not_text"),
        "NUL byte content rejected": ([{"name": "x.txt", "content": "ab\x00cd"}], "not_text"),
        "control-char content rejected": ([{"name": "x.dat", "content": "\x01\x02\x03\x04" * 50}], "not_text"),
        "oversized file rejected (>200KB)": ([{"name": "big.txt", "content": "a" * 204801}], "file_too_large"),
        "more than 4 files rejected": ([good] * 5, "too_many_files"),
        "empty file rejected": ([{"name": "e.txt", "content": "  "}], "empty_file"),
    }
    for name, (files, code) in bad_cases.items():
        s, body = chat(OWNER, "see file", attachments=files)
        check("attach: " + name, s == 400 and body["error"] == code, body)
    s, ev = chat(BOB, "what does this do?", attachments=[good])
    last = fake("/_last")["messages"][-1]["content"]
    check("attach: file sent as delimited block", s == 200 and '<file name="hello.py">\nprint(\'hi\')\n\n</file>' in last
          and last.startswith("what does this do?"), last)
    s, c, _ = req("GET", f"/api/conversations/{ev[0]['conversation_id']}", auth=BOB)
    check("attach: stored and listed as chips (name + size, no content)",
          c["messages"][0]["attachments"] == [{"name": "hello.py", "size": 12}] and c["messages"][0]["content"] ==
          "what does this do?", c["messages"][0])
    convs_before = len(req("GET", "/api/conversations", auth=BOB)[1])
    used_before = req("GET", "/api/me", auth=BOB)[1]["daily_used"]
    s, body = chat(BOB, "summarize", attachments=[{"name": "log.txt", "content": "line of text\n" * 6000}])
    check("attach: too big for guest context -> clear doesnt_fit error",
          s == 413 and body["error"] == "doesnt_fit" and "Remove a file" in body["message"], body)
    check("attach: rejected message not saved, nothing charged",
          len(req("GET", "/api/conversations", auth=BOB)[1]) == convs_before
          and req("GET", "/api/me", auth=BOB)[1]["daily_used"] == used_before)
    s, body = chat(OWNER, "summarize", attachments=[{"name": f"f{i}.txt", "content": "z" * 200000} for i in range(4)])
    check("attach: 4x200KB doesn't fit the owner window either", s == 413 and body["error"] == "doesnt_fit", body)
    # instructions count against context: fits without them, not with them
    big = [{"name": "a.txt", "content": "a" * 20500}]
    s1, _ = chat(CAROL, "x", attachments=big)
    req("PATCH", "/api/settings", {"instructions": "b" * 2000}, auth=CAROL)
    s2, body = chat(CAROL, "x", attachments=big)
    check("custom instructions are counted against the context", s1 == 200 and s2 == 413, (s1, s2, body))
    req("PATCH", "/api/settings", {"instructions": ""}, auth=CAROL)

    # ---- B projects
    s, p, _ = req("POST", "/api/projects", {"name": "Proj A", "instructions": "Use British spelling."}, auth=OWNER)
    pid = p["id"]
    check("create project", s == 201 and p["name"] == "Proj A" and p["files"] == [])
    s, p, _ = req("POST", f"/api/projects/{pid}/files", {"name": "notes.md", "content": "PROJECT_FACT_42"}, auth=OWNER)
    check("add project file", s == 201 and p["files"][0]["name"] == "notes.md" and p["total_bytes"] == 15)
    s, body, _ = req("POST", f"/api/projects/{pid}/files", {"name": "x.zip", "content": "PK"}, auth=OWNER)
    check("project file: binary rejected", s == 400 and body["error"] == "not_text")
    req("POST", f"/api/projects/{pid}/files", {"name": "big1.txt", "content": "q" * 150000}, auth=OWNER)
    s, body, _ = req("POST", f"/api/projects/{pid}/files", {"name": "big2.txt", "content": "q" * 150000}, auth=OWNER)
    check("project total size cap enforced", s == 400 and body["error"] == "project_full", body)
    s, ev = chat(OWNER, "what is the fact?", project_id=pid)
    pconv = meta(ev)["conversation_id"]
    sysmsg = fake("/_last")["messages"][0]["content"]
    check("project instructions + files included after custom instructions",
          "Use British spelling." in sysmsg and "PROJECT_FACT_42" in sysmsg and "big1.txt" in sysmsg
          and sysmsg.index("Always answer in French.") < sysmsg.index("Use British spelling.")
          < sysmsg.index("PROJECT_FACT_42"), sysmsg[:300])
    s, convs, _ = req("GET", "/api/conversations", auth=OWNER)
    check("chat belongs to the project", next(x for x in convs if x["id"] == pconv)["project_id"] == pid)
    chat(OWNER, "follow up", pconv)
    check("follow-up in project chat still gets project context", "PROJECT_FACT_42" in fake("/_last")["messages"][0]["content"])
    s, p, _ = req("PATCH", f"/api/projects/{pid}", {"name": "Proj B", "instructions": "Be brief."}, auth=OWNER)
    s2, p2, _ = req("GET", f"/api/projects/{pid}", auth=OWNER)
    check("edit project", p2["name"] == "Proj B" and p2["instructions"] == "Be brief.")
    s, p, _ = req("DELETE", f"/api/projects/{pid}/files/{p2['files'][1]['id']}", auth=OWNER)
    check("delete project file", s == 200 and [f["name"] for f in p["files"]] == ["notes.md"])

    # guests: project files that don't fit are left out with a notice
    s, gp, _ = req("POST", "/api/projects", {"name": "Guest proj"}, auth=BOB)
    req("POST", f"/api/projects/{gp['id']}/files", {"name": "small.txt", "content": "SMALL_FILE"}, auth=BOB)
    req("POST", f"/api/projects/{gp['id']}/files", {"name": "huge.txt", "content": "h" * 100000}, auth=BOB)
    s, ev = chat(BOB, "hi", project_id=gp["id"])
    notes = [e["message"] for e in ev if e.get("type") == "notice"]
    sysmsg = fake("/_last")["messages"][0]["content"]
    check("guest: oversized project file left out with a notice, small file kept",
          s == 200 and notes and "huge.txt" in notes[0] and "SMALL_FILE" in sysmsg and "hhhh" not in sysmsg, notes)

    # isolation
    codes = [req("GET", f"/api/projects/{pid}", auth=BOB)[0],
             req("PATCH", f"/api/projects/{pid}", {"name": "pwn"}, auth=BOB)[0],
             req("DELETE", f"/api/projects/{pid}", auth=BOB)[0],
             req("POST", f"/api/projects/{pid}/files", {"name": "a.txt", "content": "a"}, auth=BOB)[0],
             req("DELETE", f"/api/projects/{pid}/files/{p['files'][0]['id']}", auth=BOB)[0],
             chat(BOB, "hi", project_id=pid)[0],
             req("PATCH", f"/api/conversations/{ev[0]['conversation_id']}", {"project_id": pid}, auth=BOB)[0],
             req("DELETE", f"/api/projects/{gp['id']}/files/{p['files'][0]['id']}", auth=BOB)[0]]
    s, plist, _ = req("GET", "/api/projects", auth=BOB)
    check("another account's projects/files are invisible and untouchable", codes == [404] * 8
          and [x["name"] for x in plist] == ["Guest proj"], codes)

    # move chat between projects, delete project keep/delete chats
    s, c, _ = req("PATCH", f"/api/conversations/{zconv}", {"project_id": pid}, auth=OWNER)
    check("move chat into project", c["project_id"] == pid)
    s, _, _ = req("DELETE", f"/api/projects/{pid}", {"chats": "keep"}, auth=OWNER)
    s2, convs, _ = req("GET", "/api/conversations", auth=OWNER)
    kept = [x for x in convs if x["id"] in (zconv, pconv)]
    check("delete project, keep chats (unassigned)", s == 200 and len(kept) == 2 and all(x["project_id"] is None for x in kept)
          and req("GET", f"/api/projects/{pid}", auth=OWNER)[0] == 404)
    s, p, _ = req("POST", "/api/projects", {"name": "Temp"}, auth=OWNER)
    req("PATCH", f"/api/conversations/{zconv}", {"project_id": p["id"]}, auth=OWNER)
    s, _, _ = req("DELETE", f"/api/projects/{p['id']}", {"chats": "delete"}, auth=OWNER)
    check("delete project with its chats", s == 200 and req("GET", f"/api/conversations/{zconv}", auth=OWNER)[0] == 404)
    s, body, _ = req("POST", "/api/projects", {"name": "  "}, auth=OWNER)
    check("project needs a name", s == 400)


OLD_SCHEMA = """
CREATE TABLE accounts (id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
  key_hash TEXT NOT NULL UNIQUE, is_owner INTEGER NOT NULL DEFAULT 0, daily_allowance INTEGER NOT NULL DEFAULT 0,
  daily_used INTEGER NOT NULL DEFAULT 0, tokens_today INTEGER NOT NULL DEFAULT 0, daily_day TEXT NOT NULL DEFAULT '',
  topup INTEGER NOT NULL DEFAULT 0, disabled_until REAL NOT NULL DEFAULT 0, created REAL NOT NULL,
  last_active REAL NOT NULL DEFAULT 0, total_tokens INTEGER NOT NULL DEFAULT 0, total_requests INTEGER NOT NULL DEFAULT 0);
CREATE TABLE conversations (id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  title TEXT NOT NULL, created REAL NOT NULL, updated REAL NOT NULL);
CREATE TABLE messages (id INTEGER PRIMARY KEY, conversation_id INTEGER NOT NULL REFERENCES conversations(id)
  ON DELETE CASCADE, role TEXT NOT NULL, content TEXT NOT NULL, created REAL NOT NULL);
"""


def migration_case(label, data_dir, make_old):
    """Open an old-schema DB with the new server; old accounts/chats must keep working."""
    import hashlib
    path = os.path.join(data_dir, "gateway.db")
    con = sqlite3.connect(path)
    if make_old:
        con.executescript(OLD_SCHEMA)
    key = "migration-test-key-" + os.urandom(8).hex()
    KEYS_SEEN.append(key)
    now = time.time()
    con.execute("INSERT INTO accounts (username, key_hash, daily_allowance, daily_used, topup, created, daily_day) "
                "VALUES ('legacy_user', ?, 9000, 100, 50, ?, date('now','localtime'))",
                (hashlib.sha256(key.encode()).hexdigest(), now))
    if make_old:  # an owner so the server doesn't create one
        con.execute("INSERT INTO accounts (username, key_hash, is_owner, created) VALUES ('owner', 'x', 1, ?)", (now,))
    acc = con.execute("SELECT id FROM accounts WHERE username='legacy_user'").fetchone()[0]
    cid = con.execute("INSERT INTO conversations (account_id, title, created, updated) VALUES (?, 'Old chat', ?, ?)",
                      (acc, now, now)).lastrowid
    con.execute("INSERT INTO messages (conversation_id, role, content, created) VALUES (?, 'user', 'old question', ?)", (cid, now))
    con.execute("INSERT INTO messages (conversation_id, role, content, created) VALUES (?, 'assistant', 'old answer', ?)", (cid, now))
    con.commit()
    snap = lambda c: (c.execute("SELECT id, username, key_hash, is_owner, daily_allowance, topup FROM accounts ORDER BY id").fetchall(),
                      c.execute("SELECT COUNT(*) FROM conversations").fetchone(), c.execute("SELECT COUNT(*) FROM messages").fetchone())
    before = snap(con)
    con.close()

    port = 8798
    log = open(os.path.join(DATA, "migration.log"), "a")
    p = subprocess.Popen([sys.executable, os.path.join(ROOT, "server", "server.py")],
                         env={**ENV, "DATA_DIR": data_dir, "PORT": str(port)}, stdout=log, stderr=subprocess.STDOUT,
                         stdin=subprocess.DEVNULL)
    global BASE
    saved_base, BASE = BASE, f"http://127.0.0.1:{port}"
    try:
        for _ in range(40):
            try:
                urllib.request.urlopen(BASE + "/status", timeout=1)
                break
            except Exception:
                time.sleep(0.25)
        auth = ("legacy_user", key)
        s, me, _ = req("GET", "/api/me", auth=auth)
        check(f"migration ({label}): old account logs in, budget intact",
              s == 200 and me["daily_allowance"] == 9000 and me["daily_used"] == 100 and me["topup"] == 50, me)
        s, convs, _ = req("GET", "/api/conversations", auth=auth)
        s2, c, _ = req("GET", f"/api/conversations/{cid}", auth=auth)
        check(f"migration ({label}): old chat listed and opens",
              [x["title"] for x in convs] == ["Old chat"] and convs[0]["pinned"] == 0 and convs[0]["project_id"] is None
              and [m["content"] for m in c["messages"]] == ["old question", "old answer"]
              and c["messages"][0]["attachments"] == [], (convs, c))
        s, ev = chat(auth, "continue", cid)
        check(f"migration ({label}): can continue old chat", s == 200 and done_of(ev), ev)
        s, _, _ = req("PATCH", "/api/settings", {"instructions": "hi"}, auth=auth)
        s2, _, _ = req("POST", "/api/projects", {"name": "P"}, auth=auth)
        check(f"migration ({label}): new features work on migrated DB", s == 200 and s2 == 201)
    finally:
        BASE = saved_base
        p.terminate()
        p.wait()
    con = sqlite3.connect(path)
    after = snap(con)
    cols = {r[1] for r in con.execute("PRAGMA table_info(conversations)")}
    con.close()
    check(f"migration ({label}): existing accounts/keys unchanged, rows kept",
          after[0] == before[0] and after[1][0] == before[1][0] and after[2][0] == before[2][0] + 2
          and {"pinned", "project_id"} <= cols)


def run_migration_tests():
    d = tempfile.mkdtemp(prefix="gateway-mig-", dir=DATA)
    migration_case("synthetic old schema", d, True)
    real = os.path.join(ROOT, "data", "gateway.db")
    if os.path.exists(real):
        d = tempfile.mkdtemp(prefix="gateway-mig-real-", dir=DATA)
        for suffix in ("", "-wal", "-shm"):
            if os.path.exists(real + suffix):
                shutil.copy2(real + suffix, os.path.join(d, "gateway.db" + suffix))
        migration_case("copy of data/gateway.db", d, False)
    else:
        print("     (no data/gateway.db - skipped migrating a copy of the real DB)")


if __name__ == "__main__":
    main()
