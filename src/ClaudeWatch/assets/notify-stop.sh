#!/bin/sh
# claude-watch hook: Claude finished a turn -> trigger a rebuild round.
curl -s -m 2 -X POST --data-binary @- http://127.0.0.1:__PORT__/hook/stop >/dev/null 2>&1 || true
exit 0
