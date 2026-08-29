# claude-watch hook: Claude edited a file -> journal it (no rebuild yet).
# Fire-and-forget; silent when the watcher isn't running.
try {
    Invoke-RestMethod -Uri http://127.0.0.1:__PORT__/hook/post-tool-use -Method Post `
        -Body ([Console]::In.ReadToEnd()) -ContentType application/json -TimeoutSec 1 | Out-Null
} catch {}
exit 0
