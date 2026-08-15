# dnSpy MCP Server — Design

Date: 2026-08-15
Status: Implemented. Builds clean on net48 + net10.0-windows; protocol layer passes an 11-assertion HTTP smoke test. Runtime verification on Windows deferred to user.

Implementation notes (deltas from original plan, confirmed against the real dnSpy API):
- Breakpoints go through `DbgDotNetBreakpointFactory.Create(ModuleId, token, offset, settings)` (wraps location creation + add), not the low-level `DbgDotNetCodeLocationFactory` path.
- `DbgProcess.Id` (not `ProcessId`); start uses `BreakKind` string + `PredefinedBreakKinds.EntryPoint` (not a `BreakProcessKind` enum).
- Stepping via `DbgThread.CreateStepper().Step(kind, autoClose:true)`.
- `DbgDispatcher` only exposes `BeginInvoke`; `DbgAccess` wraps it with a `TaskCompletionSource` + timeout to return values to the HTTP thread.
- Call-stack/locals/eval read the object graph on the dispatcher thread; session-control mutators (`Start`/`BreakAll`/`RunAll`/`StopDebuggingAll`/`Restart`) are called directly.

## Goal

Expose dnSpy's debugger to MCP clients (Claude Code and others) so an AI agent can drive a debug session: start/attach, breakpoints, stepping, call stack, locals, and expression evaluation.

## Architecture (assumption)

**In-process dnSpy extension hosting an MCP server over Streamable HTTP.**

```
┌─────────────┐   HTTP 127.0.0.1:27115/mcp   ┌──────────────────────────┐
│ MCP client  │ ◄──────────────────────────► │ dnSpy.exe                │
│ (Claude     │      JSON-RPC 2.0 POST       │  dnSpy.MCP.x.dll (ext)   │
│  Code, …)   │                              │   MEF imports:           │
└─────────────┘                              │   DbgManager, BPs svc,   │
                                             │   CallStack, Languages   │
                                             └──────────────────────────┘
```

Alternatives considered:
- *Extension + stdio bridge exe*: extra process + custom RPC protocol; only needed for stdio-only clients. Claude Code supports HTTP transport → skipped (YAGNI).
- *Headless engine host*: dnSpy's debug engine is deeply wired into UI/MEF composition; extraction is a large risky refactor → rejected.

Transport details:
- `HttpListener` bound to `http://127.0.0.1:<port>/` — **localhost only**, no external exposure. Port: `DNSPY_MCP_PORT` env var, default `27115`.
- **Auth (added after security review):** requests with a browser `Origin` header or a non-loopback `Host` header are rejected (anti-CSRF / anti-DNS-rebinding); the tools launch processes and eval expressions, so the browser pivot must be closed. Optional bearer token via `DNSPY_MCP_TOKEN` guards against hostile local processes too. Only the `/mcp` path is served; request body capped at 4 MB.
- Streamable HTTP, stateless mode: `POST /mcp` with a JSON-RPC message → `application/json` response. No SSE stream in v1 (no server-initiated messages needed; clients poll via tools).
- Protocol versions accepted: `2024-11-05` through `2025-06-18`; echo the client's requested version.
- No new heavyweight deps: JSON via `Newtonsoft.Json` (version already pinned in `DnSpyCommon.props`), HTTP via BCL `HttpListener`. No ASP.NET Core (must run on net48 too).

## Project layout

`Extensions/dnSpy.MCP/` — modeled on `dnSpy.Analyzer.csproj`:
- `AssemblyName = dnSpy.MCP.x` (required `*.x.dll` pattern), signed with `dnSpy.snk`, `OutputPath` → `dnSpy/dnSpy/bin/$(Configuration)`, added to `dnSpy.sln`.
- References: `dnSpy.Contracts.Logic`, `dnSpy.Contracts.DnSpy`, `dnSpy.Contracts.Debugger`, `dnSpy.Contracts.Debugger.DotNet`, `dnSpy.Contracts.Debugger.DotNet.CorDebug`.

Files:

| File | Purpose |
|---|---|
| `TheExtension.cs` | `[ExportExtension]` — extension identity; stops server on `ExtensionEvent.AppExit` |
| `ServerLoader.cs` | `[ExportAutoLoaded]` — imports MEF services, starts `McpHttpServer` after app load |
| `Server/JsonRpc.cs` | JSON-RPC 2.0 request/response/error types + serialization |
| `Server/McpHttpServer.cs` | `HttpListener` accept loop; MCP methods: `initialize`, `notifications/initialized`, `ping`, `tools/list`, `tools/call` |
| `Tools/IMcpTool.cs` | Tool contract: `Name`, `Description`, `InputSchema` (JObject), `Invoke(JObject args) → string` (text content) |
| `Tools/DbgAccess.cs` | Marshaling helper: run delegates on `DbgManager.Dispatcher` / UI dispatcher with timeout; `wait_for_break` support via `DbgManager` events |
| `Tools/DebugControlTools.cs` | session control tools |
| `Tools/BreakpointTools.cs` | breakpoint tools |
| `Tools/InspectionTools.cs` | stack/threads/locals/eval/modules tools |

## Tools (v1)

All results are JSON-serialized text content. Errors → MCP tool error (`isError: true`) with message.

Session control:
- `dbg_status` — `IsDebugging`, `IsRunning`, debug tags, processes (id, name, state)
- `dbg_start` — args: `path`, `args?`, `working_dir?`, `break_at_entry?`. Picks `DotNetStartDebuggingOptions` vs `DotNetFrameworkStartDebuggingOptions` by examining the file (fallback: try .NET then Framework). Calls `DbgManager.Start`
- `dbg_attach` — args: `pid?`, `name?` → `AttachableProcessesService.GetAttachableProcessesAsync`, then `AttachableProcess.Attach()`
- `dbg_break` / `dbg_continue` — `BreakAll()` / `RunAll()`
- `dbg_stop` / `dbg_restart` — `StopDebuggingAll()` / `Restart()`
- `dbg_step` — args: `kind` (`into`|`over`|`out`), `thread_id?`. Creates `DbgStepper` on the paused thread, `Step(kind, autoClose: true)`, waits for `StepComplete`
- `dbg_wait_for_break` — args: `timeout_ms?`. Long-polls until `IsRunning == false` (break hit/step done) or timeout; returns stop reason + top frame

Breakpoints (IL-based, matching dnSpy's model):
- `bp_add` — args: `module` (name or path), `token` (method MD token), `il_offset?` (default 0), `condition?`, `enabled?`. Uses `DbgDotNetCodeLocationFactory.Create(ModuleId, token, offset)` + `DbgCodeBreakpointsService.Add`
- `bp_list` — id, location (module/token/offset), enabled, hit count, bound state
- `bp_remove` — args: `id` (or `all: true` → `Clear()`)
- `bp_toggle` — args: `id`, `enabled`

Inspection (require paused process):
- `dbg_threads` — id, name, state, kind; marks the break thread
- `dbg_callstack` — args: `thread_id?`, `max_frames?`. Formatted frames via `DbgCallStackService` / `DbgStackWalker`
- `dbg_locals` — args: `frame_index?`. `DbgLanguage.LocalsProvider.GetNodes` → name, value, type per node (one level; no lazy expansion in v1)
- `dbg_eval` — args: `expression`, `frame_index?`. `DbgLanguage.ExpressionEvaluator` with a `DbgEvaluationContext` from `CreateContext`; func-eval timeout ~5 s; formatted result value + type
- `dbg_modules` — per process: name, path, address, dynamic/in-memory flags

Out of scope v1 (documented for v2): file:line breakpoints (needs decompiler mapping), name→token method resolution, value-node child expansion, memory read/write, exceptions settings, SSE push notifications.

## Threading model

- Debugger object graph is touched only on `DbgManager.Dispatcher` (via `DbgAccess.InvokeDbg(Func<T>)` wrapper with timeout).
- UI-affine services (e.g. `DbgCallStackService` selected-thread state) accessed via WPF `Application.Current.Dispatcher` where required.
- HTTP requests handled on thread-pool threads; each tool call marshals inward, result marshaled back as string. One request at a time per session is assumed (MCP clients serialize tool calls); no additional locking in v1.

## Error handling

- Tool called while not debugging / not paused → clear error text (`"not debugging"`, `"process is running; call dbg_break first"`).
- Timeouts on dispatcher marshaling and func-eval → error text, server stays alive.
- Port already in use → log to dnSpy output window, extension stays loaded but server off.

## Testing / verification

No test projects exist in the repo; app is Windows-only, dev machine is macOS. Verification strategy:
1. `dotnet build` of the new project (compile-level verification is achievable cross-platform since `EnableWindowsTargeting` is set).
2. Manual protocol smoke-test documented in the extension README (curl examples for `initialize`, `tools/list`, `tools/call`).
3. Runtime verification on Windows is deferred to the user (documented steps).
