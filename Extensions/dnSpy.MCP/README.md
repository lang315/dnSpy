# dnSpy.MCP

An MCP (Model Context Protocol) server embedded in dnSpy that exposes the debugger to AI agents such as Claude Code. It lets an agent start/attach a debug session, manage breakpoints, step, and inspect threads, call stacks, locals, modules, and evaluate expressions — all against dnSpy's live debug engine.

## How it works

The extension (`dnSpy.MCP.x.dll`) hosts a small HTTP server inside the dnSpy process:

```
MCP client  ──HTTP JSON-RPC──►  dnSpy.exe / dnSpy.MCP.x.dll  ──►  DbgManager, breakpoints,
(Claude Code)   127.0.0.1:27115/mcp                                call stack, evaluation (MEF)
```

- Transport: Streamable HTTP (stateless JSON mode), `POST /mcp`, JSON-RPC 2.0.
- Bound to loopback only (`127.0.0.1`).
- Port: `27115` by default; override with the `DNSPY_MCP_PORT` environment variable before launching dnSpy.
- The server starts when dnSpy finishes loading and stops on app exit.

### Security model

The tools execute code — they launch processes, patch memory and evaluate expressions inside a
debuggee — so an open port here is local code execution. The endpoint is guarded:

- **Bearer token, on by default.** A token is generated on first run and stored in `mcp-token.txt`
  next to dnSpy's settings file, so it survives restarts and you configure your client once. Every
  request must send `Authorization: Bearer <token>`. Override it with `DNSPY_MCP_TOKEN`, or disable
  authentication entirely with `DNSPY_MCP_NO_AUTH=1` (logged loudly).
  A 401 response tells you where the token file is.
- **Loopback only** — no remote host can reach it.
- **Anti-CSRF / anti-DNS-rebinding** — requests carrying a browser `Origin` header, or a non-loopback
  `Host` header, are rejected (403). This stops a malicious web page you happen to visit from driving
  the debugger.

The token file lives beside the settings file rather than at a fixed path so that it follows
`--settings-file`: a throwaway dnSpy gets its own token and cannot read the real one.

## Connecting from Claude Code

Read the token dnSpy generated, then register the server with it:

```powershell
$token = Get-Content "$env:APPDATA\dnSpy\mcp-token.txt"
claude mcp add --transport http dnspy http://127.0.0.1:27115/mcp `
  --header "Authorization: Bearer $token"
```

Or add to your MCP client config:

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "http",
      "url": "http://127.0.0.1:27115/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Call `dnspy_info` at any time to check which instance you reached, whether authentication is on and
which settings file it is using.

## Tools (33)

Endpoint:
- `dnspy_info` — which dnSpy this is, its version and port, whether a token is required and where it
  came from, and the settings file in use

Session control:
- `dbg_status` — is-debugging / running / process list
- `dbg_start` — launch an exe/dll (auto-detects .NET vs .NET Framework; `runtime` overrides)
- `dbg_list_attachable` — list attachable .NET processes
- `dbg_attach` — attach by `pid` or process `name`
- `dbg_set_thread` — set the default thread context
- `dbg_break`, `dbg_continue`, `dbg_stop`, `dbg_restart`
- `dbg_step` — `into` / `over` / `out`, waits for completion
- `dbg_wait_for_break` — block until a process pauses

Breakpoints:
- `bp_add` — by module + metadata `token` (+ `il_offset`, `condition`, `hit_count`, `enabled`)
- `bp_add_method` — by fully-qualified method name (no token needed; sets one per overload)
- `bp_add_line` — by source `line` in a method (needs the PDB)
- `bp_list`, `bp_remove` (`id` or `all`), `bp_toggle`
- `mbp_add` / `mbp_list` / `mbp_remove` — module-load breakpoints (break when a module loads)
- `exc_break` — break when a CLR exception is thrown (all, or a named type)

Inspection (require a paused process):
- `dbg_threads`, `dbg_modules`
- `dbg_callstack` — `thread_id?`, `max_frames?`
- `dbg_locals` — `frame_index?`, `thread_id?`
- `dbg_variables` — `returns` / `statics` / `exceptions` (dnSpy's .NET engine does not implement
  `autos`; the tool says so rather than returning its placeholder)
- `dbg_eval` — C#/VB `expression` in a frame's context
- `dbg_expand` — list an expression's child members (drill into objects/arrays)
- `dbg_set_variable` — assign a new value to a variable
- `dbg_set_next_statement` — move the instruction pointer to another IL offset
- `dbg_read_memory` / `dbg_write_memory` — raw process memory as hex

Read-only tools carry a `readOnlyHint` annotation; process-changing tools (`dbg_start`, `dbg_write_memory`, `dbg_set_variable`, …) carry `destructiveHint`.

## Live notifications (SSE)

Clients may open a `GET /mcp` stream with `Accept: text/event-stream`. The server pushes a
`notifications/paused` JSON-RPC notification whenever a debugged process pauses (breakpoint hit,
step complete, break), so an agent can react without polling `dbg_wait_for_break`.

## Manual smoke test

With dnSpy running:

```sh
curl -s http://127.0.0.1:27115/mcp -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'

curl -s http://127.0.0.1:27115/mcp -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

curl -s http://127.0.0.1:27115/mcp -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"dbg_status","arguments":{}}}'
```

## Notes / limits (v1)

- Breakpoints are identified by module name + method metadata token + IL offset — there is no source-line or method-name resolution yet.
- `dbg_locals` returns one level of variables (no lazy child expansion).
- All debugger access is marshaled onto dnSpy's debug-engine thread; each tool call runs to completion before the next is handled.
