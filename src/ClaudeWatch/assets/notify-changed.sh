#!/bin/sh
# claude-watch hook: Claude edited a file -> journal it (no rebuild yet).
curl -s -m 1 -X POST --data-binary @- http://127.0.0.1:__PORT__/hook/post-tool-use >/dev/null 2>&1 || true
exit 0
