# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

dnSpyEx — an unofficial continuation of dnSpy: a .NET/Unity debugger and assembly editor. WPF desktop app, Windows-only at runtime, GPLv3. Targets `net48` and `net10.0-windows` (see `DnSpyCommon.props`).

## Build

Submodules are mandatory — seven of them (ILSpy decompiler, NRefactory, Mono.Debugger.Soft, netcorefiles, ICSharpCode.TreeView, Roslyn.ExpressionCompiler, dnSpy.Images). Nothing builds without them:

```
git submodule update --init --recursive
```

```powershell
./build.ps1                      # all TFMs, Release, uses msbuild
./build.ps1 -NoMsbuild           # uses `dotnet build` instead
./build.ps1 net                  # one target: all|netframework|net|net-x86|net-x64
```

`msbuild` is the default path because `dotnet build` does not support COM references (dnSpy issue #1053). `-NoMsbuild` works but is the fallback.

`clean-all.cmd` cleans the repo and every submodule (`git clean -xdf` in each).

Output lands in `dnSpy/dnSpy/bin/$(Configuration)/<tfm>/`. `build.ps1` then shuffles everything into a `bin` subdirectory, keeping only the exe/apphosts at the top, and runs `Build/AppHostPatcher` to repoint .NET apphosts at that `bin` dir.

The dnSpy app has no unit tests — verification of the app itself is "the build succeeds"; CI (`.github/workflows/build.yml`) runs the same `build.ps1` across the four platform variants on `windows-latest`. The app cannot be built or run on macOS/Linux — treat `build.ps1` and the CI workflow as the authoritative description of the build.

The one component with tests is the `dnSpy.MCP` extension (see below); those live under `tests/` (not in `dnSpy.sln`) and have their own CI (`.github/workflows/mcp-tests.yml`).

## Architecture

### MEF composition
Everything is wired with VS-MEF (`Microsoft.VisualStudio.Composition`), not plain System.ComponentModel.Composition. `dnSpy/dnSpy/MainApp/App.xaml.cs` builds the assembly list, composes the container, and caches the composition to disk (`CachedMefInfo`) so startup stays fast. Adding a new MEF-exported service in an assembly that isn't already in that list means it won't be discovered.

### Contracts layering
- `dnSpy.Contracts.Logic` — no UI dependency; decompiler/metadata/text primitives.
- `dnSpy.Contracts.DnSpy` — UI-facing contracts (documents, tabs, menus, toolbars, tree view, themes, hex editor).
- `dnSpy.Contracts.Debugger` + `.DotNet` + `.DotNet.CorDebug` + `.DotNet.Mono` — the debugger contract stack, split so a debug engine (CorDebug for .NET Framework/.NET, Mono soft-debugger for Unity) plugs in behind a common `DbgEngine` abstraction.

Extensions reference the Contracts assemblies, never the `dnSpy` app assembly.

### Extension model
Each extension is one assembly with a single `[ExportExtension]` class implementing `IExtension`. Rules that are easy to get wrong:
- `AssemblyName` **must** end in `.x` — the runtime searches for `*.x.dll` (`dnSpy/dnSpy/MainApp/App.xaml.cs:351`) in the bin dir, in an `Extensions/` subdir, and one level of subdirectories under it.
- `OutputPath` is overridden to build directly into `..\..\dnSpy\dnSpy\bin\$(Configuration)\`.
- Signed with `dnSpy.snk`; an optional `<dll>.x.dll.xml` config gates loading by OS/framework/app version (`ExtensionConfig`).

`Extensions/Examples/Example1.Extension` is the working template (menu commands, context menus, toolbar, settings page).

Built-in extensions: `dnSpy.Analyzer`, `dnSpy.AsmEditor`, `dnSpy.BamlDecompiler`, `dnSpy.Debugger/*`, `dnSpy.MCP`, `dnSpy.Scripting.Roslyn` (C# Interactive), `dnSpy.StringSearcher`, `ILSpy.Decompiler/*`.

### dnSpy.MCP extension
`Extensions/dnSpy.MCP` embeds an MCP (Model Context Protocol) server inside the dnSpy process and exposes the debugger to AI agents over loopback HTTP (`127.0.0.1:27115/mcp`, JSON-RPC 2.0). 44 tools: static analysis (decompile to C#/IL, list/search types & methods, find references to methods/fields/types & implementations, type hierarchy, IOC & resource extraction — no debug session needed), session control, breakpoints (by IL token, method name or source line; run-to), stepping, and inspection of a paused process (threads, call stack, locals, eval incl. func-eval, memory r/w). Read `Extensions/dnSpy.MCP/README.md` and `USAGE.md` before touching it. Load-bearing facts:
- **All debugger access is marshalled onto `DbgManager.Dispatcher`** via `Server/DbgAccess.cs`. Touching a debugger object off that thread is a bug.
- **Authentication is on by default** (`Server/TokenStore.cs`): a token is generated and stored next to the settings file. Anchoring to the settings file is what lets `--settings-file` isolate a throwaway dnSpy.
- **Bitness must match the debuggee** (CorDebug limitation) — build the x86 dnSpy (`build.ps1 net-x86`) to debug 32-bit targets.
- Tests are three tiers. **Tier 1** (`tests/dnSpy.MCP.Tests`) stands up a real `McpServer` over HTTP with stub tools — no dnSpy/WPF/engine, runs in CI. **Tier 2** (`tests/dnSpy.MCP.IntegrationTests`) drives a real dnSpy against the `tests/fixture/dbgtest` debuggee; run it with `tests/run-integration.ps1`, which **must** pass `--settings-file` at a temp path — the suite clears all breakpoints and a `SafetyGate` refuses to run against a dnSpy persisting to the user's real profile. **Tier 3** is manual MCP-client conformance. Only Tier 1 is CI-able; Tier 2/3 need an interactive desktop.

### Build-time IL rewriting
Two custom MSBuild tasks rewrite referenced assemblies with dnlib before compilation. Their prebuilt DLLs are checked in at `Build/compiled/` — editing the sources under `Build/` does not change the build until those DLLs are rebuilt and replaced.
- `MakeEverythingPublic` — makes Roslyn internals public and patches InternalsVisibleTo so the four `dnSpy/Roslyn/*.Internal` and `*.EditorFeatures` projects can consume them. Opted in per-project via `<MakeEverythingPublicAssemblies>`.
- `ConvertToNetstandardReferences` — rewrites `Microsoft.VisualStudio.*` references for the .NET (non-Framework) build; applied globally from `DnSpyCommon.props` when `IsDotNet == true`.

Both tasks carry a `const string VERSION` that must be bumped when their logic changes, or the cached rewritten assemblies are reused.

### Other entry points
- `dnSpy.Console` — command-line decompiler / project exporter (`--sdk-project`, `--asm-path`, `--threads`, …), same decompiler engine, no UI.
- `dnSpy-x86` — thin 32-bit launcher project.

## Cross-file coupling

`DnSpyCommon.props` is the single source of truth for TFMs, dependency versions, and the assembly version. Its own comments record what must be edited in lockstep:
- `TargetFrameworks` → also update `build.ps1`, `.github/workflows/build.yml`, `DnSpyRoslyn.props`.
- `DnSpyRuntimeIdentifiers` → also update `build.ps1` and the CI workflow.
- `DnSpyAssemblyVersion` or a dependency version such as `DnlibVersion` → also update `dnSpy/dnSpy/app.config` (binding redirects).

`Directory.Build.targets` deletes a hardcoded list of unwanted satellite/native files after every build and publish; new unwanted output goes there.

## Style

Enforced by `.editorconfig`, and unusual enough to note:
- Tabs, width 4 (spaces only for xml/xaml/props/yml).
- Braces on the same line (`csharp_new_line_before_open_brace = none`), and braces omitted for single statements (`csharp_prefer_braces = false`).
- `Nullable` is enabled in most projects; `Features` includes `strict;nullablePublicOnly`. Prefer `is null` / `is not null` — the codebase uses `Debug2.Assert` for nullable-aware asserts.
- Every source file carries the GPLv3 header block; copy it into new files.
- UI strings live in `Properties/*.resx` per project (localized via Crowdin) — do not hardcode user-visible text.
