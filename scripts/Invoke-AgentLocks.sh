#!/bin/sh
# PowerShell reads its Unix startup profile before AgentLocks.ps1 can run.
cache=$(mktemp -d "${TMPDIR:-/tmp}/respire-agent-pwsh.XXXXXXXX") || exit 1
trap 'find "$cache" -depth -delete' 0
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
XDG_CACHE_HOME="$cache" pwsh -NoProfile -NonInteractive -File "$(dirname "$0")/AgentLocks.ps1" "$@"
