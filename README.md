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

`init` writes `claude-watch.json` (edit the TODOs) and merges the Claude Code hooks into
`.claude/settings.json` (never clobbers existing hooks — prints a merge block instead). It also prints a dev-only `<script>` snippet to add to your root
component (e.g. `Components/App.razor`); the snippet is inert unless the app was launched by
claude-watch (`CLAUDE_WATCH_PORT` env var).

A full example config for a Blazor + Tailwind app is in `samples/blazor/claude-watch.json`.

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
| `GET /status` | State, round, app pid, last build result with parsed errors, pending changes, per-phase timing of the last round (`lastRoundTimings`: stop / each pre-build step / build / start) |
| `GET /events` | SSE stream; emits `reload` after each successful restart |
| `GET /claude-watch-reload.js` | Browser reload client (referenced by the App.razor snippet) |

`claude-watch status` queries a running watcher and exits 0 (build ok + app running),
1 (build failed / app down), or 2 (no watcher reachable) — scriptable from Claude skills.

## In-browser feedback

Tabs connected to the watcher (via the reload script) get live round feedback:

- **Building**: the tab title animates (`☱ ☲ ☴` prefix) while a round is running.
- **Failure**: a click-to-dismiss full-screen overlay lists the parsed build errors
  (file/line/code/message), and the title gets a `❌` prefix — visible even though the app
  itself is down. Cleared automatically when the next round starts.
- **Success**: an animated checkmark toast appears for ~2s after a css hot-swap or a
  round-driven reload (not when a fresh tab merely self-heals an override).

## Config reference

See `samples/blazor/claude-watch.json` for a full example. Notable knobs:

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
- `classify.cssFastPath` — rounds touching only `classify.cssOnly` files (keep these to
  tailwind inputs: `tailwind.input.css`, `tailwind.config.js`) skip the entire
  stop→build→restart cycle: the CSS build runs, and open tabs **hot-swap the stylesheet in
  place** — no reload, the Blazor circuit stays alive, UI state survives. Because
  `MapStaticAssets()` can only serve build-time-fingerprinted content, the fresh CSS is served
  **by the watcher** (`/asset/{route}`, from `preBuildSteps[].route`) and tabs swap their
  `<link>` to it; new tabs are healed on SSE connect (active overrides are replayed). The next
  full round rebuilds, clears the overrides (`cssOverrides` in `/status`), and returns tabs to
  app-served CSS. Requires a pre-build step with both `output` and `route`.
- `run.readiness` — stdout regex and/or probe URL that mark the app as up.
- `fallbackWatch` — filesystem watching with three modes:
  - `"mode": "hybrid"` (recommended alongside hooks): changes feed the change journal, and
    **editor/hand edits self-trigger** after `quietPeriodSec` of quiet — but the trigger is
    **held while a Claude turn is in flight**, so the Stop-hook round picks the edits up
    instead of rebuilding mid-turn. Turn detection: the UserPromptSubmit hook marks busy,
    every PostToolUse (matcher `*`) refreshes activity, Stop marks idle;
    `agentIdleTimeoutSec` (default 180) releases the hold after an interrupted turn where
    Stop never fires. `/status` exposes `agentBusy`.
  - `"mode": "journal"`: changes only feed the journal; rounds trigger exclusively on the
    Claude Stop hook. Catches edits made by **scripts** (python, sed, node workflows like
    br-edit.js) that bypass the Edit/Write tool hooks.
  - `"mode": "trigger"` (hook-free): a quiet-period debounce after changes triggers the
    round (rebuild only after N seconds of no changes). Also enabled by the `--watch` flag.

## Development

```powershell
dotnet build
dotnet test
```
