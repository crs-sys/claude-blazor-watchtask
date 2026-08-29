# claude-watch hook: Claude finished a turn -> trigger a rebuild round.
# Fire-and-forget; silent when the watcher isn't running.
try {
    Invoke-RestMethod -Uri http://127.0.0.1:__PORT__/hook/stop -Method Post `
        -Body ([Console]::In.ReadToEnd()) -ContentType application/json -TimeoutSec 2 | Out-Null
} catch {}
exit 0
