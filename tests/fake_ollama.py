#!/usr/bin/env python3
"""Tiny stand-in for Ollama used by the tests: /api/tags and streaming /api/chat.

Usage: python3 tests/fake_ollama.py PORT
- Replies "ok 1 2 3 ... " one token per chunk (10 tokens by default).
- If the last user message contains "SLOW", streams 300 tokens 30 ms apart (for stop tests).
- If it asks for "last number", replies "10" (history test).
- GET /_last returns the last /api/chat request body; GET /_stats returns {"cancelled": n, "sent": n}.
"""
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODEL = "qwen3.8:27b"
STATE = {"last": None, "cancelled": 0, "sent": 0}


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, obj):
        data = json.dumps(obj).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/api/tags":
            return self.send({"models": [{"name": MODEL}]})
        if self.path == "/_last":
            return self.send(STATE["last"])
        if self.path == "/_stats":
            return self.send({"cancelled": STATE["cancelled"], "sent": STATE["sent"]})
        self.send_error(404)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        STATE["last"] = body
        last = next((m["content"] for m in reversed(body["messages"]) if m["role"] == "user"), "")
        if "SLOW" in last:
            tokens, delay = [f"t{i} " for i in range(300)], 0.03
        elif "last number" in last:
            tokens, delay = ["10"], 0
        else:
            tokens, delay = [f"{i} " for i in range(1, 11)], 0
        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson")
        self.end_headers()
        sent = 0
        try:
            for t in tokens:
                self.wfile.write(json.dumps({"message": {"role": "assistant", "content": t}, "done": False}).encode() + b"\n")
                self.wfile.flush()
                sent += 1
                time.sleep(delay)
            prompt = sum(len(m["content"]) for m in body["messages"]) // 4
            self.wfile.write(json.dumps({"done": True, "prompt_eval_count": prompt, "eval_count": len(tokens)}).encode() + b"\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            STATE["cancelled"] += 1
        STATE["sent"] = sent


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H)
    srv.daemon_threads = True
    srv.serve_forever()
