# Gateway API for coding mode: `POST /api/agent`

The desktop app's coding mode runs tools (read/list/search files, edit files, run commands) **on the
user's own computer**. The gateway never sees the workspace except through the text the client sends.
It only relays a model turn: it takes the full message list plus tool definitions, asks Ollama, and
streams back text and tool calls. Nothing is stored server-side (stateless): no conversation rows.

## Request

```
POST /api/agent
Authorization: Bearer <username>:<key>
Content-Type: application/json

{
  "messages": [
    {"role": "system",    "content": "coding-mode instructions from the client"},
    {"role": "user",      "content": "Add a --verbose flag to main.py"},
    {"role": "assistant", "content": "", "tool_calls": [
        {"function": {"name": "read_file", "arguments": {"path": "main.py"}}}]},
    {"role": "tool",      "tool_name": "read_file", "content": "1| import sys\n..."}
  ],
  "tools": [
    {"type": "function", "function": {"name": "read_file", "description": "...",
      "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}}
  ]
}
```

Validation (400 `bad_request` with a clear message if violated):
- `messages`: non-empty list, at most 400 items. Each item is an object with `role` in
  `system | user | assistant | tool` and `content` a string (may be empty for assistant messages that
  only carry `tool_calls`). The last message must be `user` or `tool`.
- `assistant.tool_calls` (optional): list of `{"function": {"name": str, "arguments": object}}`.
- `tool.tool_name` (optional): string.
- `tools`: list of at most 32 objects of the shape above (`type` = `function`, `function.name` string,
  `function.parameters` object). May be empty.
- Body size: the existing `max_body_bytes` (2 MB) cap applies.

## What the server does

Same gates as `/api/chat`, in this order:
1. `authenticate()` (401 `bad_login`, 403 `disabled`, 429 `locked_out`).
2. `check_open()` (503 `nook_off` for everyone, 503 `maintenance` for non-owners).
3. Budget: non-owners with `total_left <= 0` get 402 `no_tokens` (with `resets_at`).
4. Guest rate limit: the same per-account `guest_requests_per_minute` window as chat (each agent step
   is one request). 429 `rate_limited` **with `retry_after` (seconds, int)** so the client can wait and
   retry automatically instead of failing the whole task. (Adding `retry_after` to the chat 429 too is
   harmless.)

Prompt building:
- One system message, in this order: `system_prompt` from config (Nook identity), the account's custom
  instructions (if any), then the client's system message(s) joined. Client system messages anywhere
  else in the list are folded into this one.
- Context window = `owner_context_tokens` / `guest_context_tokens`, minus the reply budget
  (`owner_max_reply_tokens` / `guest_max_reply_tokens`). Token estimate is the existing `est()`;
  tool definitions count as `est(json.dumps(tools))`; a message with tool calls counts its content plus
  `json.dumps(tool_calls)`.
- **Must fit:** system + tools + the last `user` message + the final message. If not: 413 `doesnt_fit`
  with `needed` and `limit` (same shape as chat) and a message such as
  "This task is too big for the context window: about N tokens, but this account can use about M."
- Then messages **after** the last user message are added newest-first while they fit, then messages
  before it, newest-first. Whatever does not fit is dropped (oldest first). A dropped assistant
  message takes its following `tool` results with it, and the kept history never starts with orphan
  `tool` messages. The number of dropped messages is reported as `trimmed` (see events).

Ollama call: `POST {ollama_url}/api/chat` with
`{"model", "messages", "tools", "stream": true, "think": false,
  "options": {"num_ctx": owner_context_tokens, "num_predict": max_reply}}` — the same `num_ctx` as chats
(`Ollama.load_options()`), so the model is never reloaded. Ollama start-up, `active` counting, idle
timer, `status: starting` and the offline error work exactly like chat.

Charging: `prompt_eval_count + eval_count` from Ollama's final chunk via `charge()`. If the client
disconnects or the stream fails: prompt estimate + chunks produced (same as chat).

## Response

Errors before the stream starts: JSON `{"error", "message", ...}` with the HTTP status above.

Otherwise `200 text/event-stream`, one JSON object per `data:` line (same framing as chat):

| event | meaning |
|---|---|
| `{"type":"status","state":"starting"}` | Ollama / the model is being started ("Waking up Nook…") |
| `{"type":"status","state":"ready"}` | model answered, generation is running |
| `{"type":"notice","message":"..."}` | e.g. "Older steps were left out to fit the context window (3 messages)." |
| `{"type":"delta","text":"..."}` | streamed assistant text |
| `{"type":"tool_calls","tool_calls":[{"function":{"name":"...","arguments":{...}}}]}` | tool calls from the model (may arrive once or in several events; the client concatenates) |
| `{"type":"done","usage":{"prompt":n,"completion":n,"total":n},"budget":{...},"trimmed":n}` | turn finished |
| `{"type":"error","error":"offline"\|"model","message":"..."}` | failure mid-stream |

`arguments` is passed through as Ollama returns it (normally an object). The client also tolerates a
JSON string and tool calls written as text in `content` (`<tool_call>{...}</tool_call>`), which Qwen
models sometimes produce; that parsing lives in the client, not the server.

## Client behaviour (desktop app, for reference)

- Loop: send messages + tools -> run the returned tool calls locally (after approval for writes and
  commands) -> append `assistant` (with `tool_calls`) and one `tool` message per call -> repeat, max 25
  steps, user can stop at any time (closes the stream, so the server charges only what was produced).
- Tool results are truncated client-side (about 12 000 characters) to keep guests inside 8K contexts.
- 429 `rate_limited`: waits `retry_after` (default 10 s) and retries the same step, up to 6 times.
- 413 `doesnt_fit`: stops the task and shows the message.
