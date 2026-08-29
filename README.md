# claude-watch

A Claude Code-aware alternative to `dotnet watch`. Instead of rebuilding on every file save,
it rebuilds and restarts your .NET web app **once per round of Claude edits** — signaled by
Claude Code hooks — then reloads the browser automatically. Chat-only turns cost nothing.

## How it works

1. A `PostToolUse` hook (on `Edit|Write|MultiEdit|NotebookEdit`) POSTs each edited file path
   to the watcher's localhost endpoint — the **change journal**.
2. A `Stop` hook fires when Claude finishes its turn and triggers a **round**:
   - No relevant files changed → skip (nothing happens).
   - Otherwise: stop the app → run matching pre-build steps (e.g. Tailwind, only if
     razor/css changed) → `dotnet build` → restart → wait for readiness → tell every
     connected browser tab to reload (SSE).
3. Build failures are parsed into structured errors, shown in the console and exposed at
   `GET /status` — so Claude itself can check whether its round broke the build.

The app runs inside a Windows Job Object (`KILL_ON_JOB_CLOSE`): if the watcher dies for any
reason, the app dies with it — no orphaned processes or locked DLLs. On startup, anything
already listening on the app's ports is killed (`run.killOrphansOnPorts`).

## Quick start

```powershell
# from this repo
dotnet run --project src\ClaudeWatch -- init --target C:\path\to\your-repo   # scaffold config + hooks
dotnet run --project src\ClaudeWatch -- --config C:\path\to\your-repo\claude-watch.json

# or install as a global tool
dotnet pack src\ClaudeWatch -o nupkg
dotnet tool install -g ClaudeWatch --add-source .\nupkg
claude-watch init --target C:\path\to\your-repo
cd C:\path\to\your-repo; claude-watch
```

`init` writes `claude-watch.json` (edit the TODOs), hook scripts under `.claude/claude-watch/`,
and merges the hooks into `.claude/settings.json` (never clobbers existing hooks — prints a
merge block instead). It also prints a dev-only `<script>` snippet to add to your root
component (e.g. `Components/App.razor`); the snippet is inert unless the app was launched by
claude-watch (`CLAUDE_WATCH_PORT` env var).

A ready-made config for SraServiceStack is in `samples/sra/claude-watch.json`.

## Console hotkeys

| Key | Action |
|-----|--------|
| `R` | Force full rebuild + restart (covers your own hand edits and interrupted Claude turns — the Stop hook doesn't fire on Ctrl+C) |
| `S` | Print `/status` JSON |
| `C` | Clear screen |
| `Q` | Quit (stops the app too) |

## HTTP API (localhost only, default port 43617)

| Route | Purpose |
|-------|---------|
| `POST /hook/post-tool-use` | Raw Claude PostToolUse JSON → journal the edited file(s) |
| `POST /hook/stop` | Raw Claude Stop JSON → trigger a round |
| `POST /changed` | `{"file_path": "..."}` → journal a file manually |
| `POST /trigger` | Trigger a round (journal-classified) |
| `POST /force` | Force a full rebuild round |
| `GET /status` | State, round, app pid, last build result with parsed errors, pending changes |
| `GET /events` | SSE stream; emits `reload` after each successful restart |
| `GET /claude-watch-reload.js` | Browser reload client (referenced by the App.razor snippet) |

`claude-watch status` queries a running watcher and exits 0 (build ok + app running),
1 (build failed / app down), or 2 (no watcher reachable) — scriptable from Claude skills.

## Config reference

See `samples/sra/claude-watch.json` for a full example. Notable knobs:

- `preBuildSteps[].when` — globs; the step runs only when a round touched a matching file.
- `preBuildSteps[].output` — the file the step produces. The watcher snapshots it after each
  build and **warns loudly if anything rewrites it afterwards** (surfaced as `staleAssets` in
  `/status`). This catches the MapStaticAssets desync trap: assets are served with build-time
  fingerprints (Content-Length/ETag/precompressed .gz), so a post-build rewrite — classically a
  `tailwind --watch` left running in another terminal — makes browsers receive broken or stale
  CSS while direct fetches of the file still look correct.
  **Do not run `npm run ui:dev` (or any asset watcher) alongside claude-watch** — the watcher
  runs the asset build itself each round.
- `classify.exclude` — files that never trigger a rebuild (**include your app's runtime-write
  dirs** like `App_Data/**`, plus `bin`, `obj`, generated CSS, docs).
- `classify.cssFastPath` — opt-in: rounds touching only `classify.cssOnly` files run pre-build
  steps + browser reload without an app restart. Off by default: apps using `MapStaticAssets()`
  serve build-time-fingerprinted assets and may not pick up files rewritten after build.
- `run.readiness` — stdout regex and/or probe URL that mark the app as up.
- `fallbackWatch` — filesystem watching with two modes:
  - `"mode": "journal"` (recommended alongside hooks): changes only feed the change journal;
    rounds still trigger on the Claude Stop hook. This catches edits made by **scripts**
    (python, sed, node workflows like br-edit.js) that bypass the Edit/Write tool hooks —
    without it, a scripted-edit turn classifies as "no relevant changes" and skips.
  - `"mode": "trigger"` (hook-free): a quiet-period debounce after changes triggers the
    round (rebuild only after N seconds of no changes). Also enabled by the `--watch` flag.

## Development

```powershell
dotnet build
dotnet test
```
