# Vendored: de4dotEx

This directory is a **verbatim vendored copy** of de4dotEx — the de4dot fork used as the
deobfuscation engine by the dnSpy.MCP `deobfuscate` tool. It is built as a separate project and
invoked **out-of-process** (see `../DeobHost`), never loaded into the dnSpy process.

| | |
|---|---|
| Upstream | https://github.com/GDATAAdvancedAnalytics/de4dotEx |
| Vendored commit | `660088d663239de6bc0df7da15accabbd15495d7` |
| Vendored on | 2026-08-22 (repo commit `1c114b63`, replacing the former git submodule) |
| License | GPLv3 — see `COPYING` + `LICENSE.de4dotEx.txt` in this directory |

## Why vendored (not a submodule)
- **Self-contained, reproducible build** — no 8th mandatory submodule on top of dnSpy's existing seven;
  a plain `git clone` yields a buildable tree.
- **Supply-chain resilience** — a third-party fork could be rewritten or removed; a vendored copy keeps
  building regardless.
- The **lock-down** (symbol renaming OFF, dynamic string-decryption rejected — analysis only, never runs
  the target's code) lives in `../DeobHost`, which calls `de4dot.cui.Program.Main` here. Keeping the exact
  shipped source in-tree makes that behavior visible and auditable.

## Updating
Re-vendor from the upstream commit you want:
1. Fetch the upstream tree at the target commit.
2. Replace this directory's contents (keep this `VENDORED.md`).
3. Update the table above (commit + date) and re-run the Tier-2 `DeobfuscateIntegrationTests`.

## Do not edit
Treat these files as read-only upstream code — local behavior changes belong in `../DeobHost`, not here.
This tree is marked `linguist-vendored` / `linguist-generated` in the repo-root `.gitattributes`, so GitHub
excludes it from language stats and collapses it in pull-request diffs.
