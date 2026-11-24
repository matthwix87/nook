#!/usr/bin/env python3
"""End-to-end test against a live proxy and the real Ollama.

Starts its own proxy on a test port with a throwaway data dir, so it never
touches data/. Needs Ollama installed; with AUTO_MANAGE it starts Ollama via
ollama-on and checks that the idle timer stops it again.
Run on the host:  python3 tests/test_gateway.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORT = 8797
BASE = f"http://127.0.0.1:{PORT}"
DATA = tempfile.mkdtemp(prefix="gateway-test-")
ENV = {**os.environ, "DATA_DIR": DATA, "PORT": str(PORT), "AUTO_MANAGE_OLLAMA": "1",
       "OLLAMA_IDLE_MINUTES": "0.5", "GUEST_REQUESTS_PER_MINUTE": "3",
       "ALLOWED_ORIGINS": "https://example.github.io"}

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


def chat(auth, message, conv=None):
    """Returns (status, events) - events parsed from the SSE stream."""
    resp = req("POST", "/api/chat", {"message": message, "conversation_id": conv}, auth=auth, raw=True, timeout=600)
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


def ollama_up():
    try:
        urllib.request.urlopen("http://127.0.0.1:11434/api/tags", timeout=2)
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
    if ollama_up():
        print("note: Ollama already running; stopping it first so the down/up path is tested")
        subprocess.run([os.path.expanduser("~/.local/bin/ollama-off")], capture_output=True)
        time.sleep(2)

    srv = start_server()
    try:
        run_tests()
    finally:
        srv.terminate()
        srv.wait()

    # /status with auto-manage off and Ollama down -> not available
    srv = start_server({"AUTO_MANAGE_OLLAMA": "0"})
    try:
        s, st, _ = req("GET", "/status")
        check("status (auto-manage off, Ollama down) reports unavailable",
              s == 200 and st["ollama"] is False and st["available"] is False, st)
    finally:
        srv.terminate()
        srv.wait()

    log = open(os.path.join(DATA, "server.log")).read()
    check("no keys appear in the server log", all(k not in log for k in KEYS_SEEN), "key found in log")
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
    conv_id = events[0]["conversation_id"] if s == 200 else None
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
    owner_conv = events[0]["conversation_id"] if s == 200 else 0
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
    r = urllib.request.Request(BASE + "/api/chat", data=b"x" * 300000, method="POST",
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
    print("     waiting for idle auto-stop (idle timeout 30s)...", flush=True)
    stopped = False
    for _ in range(30):
        time.sleep(3)
        if not ollama_up():
            stopped = True
            break
    check("Ollama stopped automatically after idle timeout", stopped)
    s, st, _ = req("GET", "/status")
    check("status after auto-stop reports Ollama down", st["ollama"] is False, st)


if __name__ == "__main__":
    main()
