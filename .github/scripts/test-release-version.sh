#!/usr/bin/env bash
set -euo pipefail
script="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/release-version.sh"
failures=0

expect() {
  local actual
  if actual=$(bash "$script" "$1" "$2" 2>/dev/null) && [ "$actual" = "$3" ]; then
    return
  fi
  echo "FAIL: $1 '$2' gave '${actual:-<error>}', expected '$3'"
  failures=$((failures + 1))
}

reject() {
  if bash "$script" "$@" >/dev/null 2>&1; then
    echo "FAIL: '$*' was accepted"
    failures=$((failures + 1))
  fi
}

expect tag v1.2.3 1.2.3
expect tag 1.2.3 1.2.3
expect tag V1.2.3 1.2.3
expect tag v2.0.0-rc.2000 2.0.0-rc.2000
expect tag v2.0.0-beta.1 2.0.0-beta.1
expect next v0.13.3 0.13.4
expect next v2.0.0-rc.2000 2.0.0-rc.2001
expect next v2.0.0-rc.9 2.0.0-rc.10
expect next v2.0.0-rc.09 2.0.0-rc.10
expect next v2.0.0-beta 2.0.0-beta.1
expect next v2.0.0-7 2.0.0-8
expect next v2.0.0-preview.1.rc 2.0.0-preview.1.rc.1

for tag in v1.2 release-1 v1.2.3+build v1.2.3- v1.2.3-rc..1 '' 'v1.2.3 ' 'v1.2.3;rm'; do
  reject tag "$tag"
  reject next "$tag"
done
reject tag
reject other v1.2.3

if [ "$failures" -ne 0 ]; then
  exit 1
fi
echo "release-version.sh: all cases passed"
