#!/usr/bin/env bash
set -euo pipefail
# Bash 4.4+ (CI runners); older shells such as macOS bash 3.2 run without it.
shopt -s inherit_errexit 2> /dev/null || true
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
expect tag v0.0.0 0.0.0
expect tag v65534.65534.65534 65534.65534.65534
expect tag v2.0.0-rc.2000 2.0.0-rc.2000
expect tag v1.2.3-beta.1 1.2.3-beta.1
expect tag v1.2.3-beta01 1.2.3-beta01
expect tag v1.2.3-0 1.2.3-0
expect tag v1.2.3-0a.--x 1.2.3-0a.--x
expect next v0.13.3 0.13.4
expect next v2.0.0-rc.2000 2.0.0-rc.2001
expect next v2.0.0-rc.9 2.0.0-rc.10
expect next v2.0.0-rc.0 2.0.0-rc.1
expect next v2.0.0-beta 2.0.0-beta.1
expect next v2.0.0-beta01 2.0.0-beta01.1
expect next v2.0.0-7 2.0.0-8
expect next v2.0.0-preview.1.rc 2.0.0-preview.1.rc.1

for tag in v1.02.3 v01.2.3 v1.2.03 v1.2.3-beta..1 v1.2.3-beta.01 v1.2.3-01 v1.2.3- v1.2 \
  v65535.0.0 v0.65535.0 v0.0.65535 v100000.0.0 release-1 v1.2.3+build v1.2.3-rc.1+build \
  '' 'v1.2.3 ' 'v1.2.3;rm' 'v1.2.3-rc_1'; do
  reject tag "$tag"
  reject next "$tag"
done
reject next v1.2.65534

# gt: exit 0 when the first version ranks higher, 1 when it does not, 2 when either is invalid.
gt() {
  local status=0
  bash "$script" gt "$1" "$2" 2>/dev/null || status=$?
  if [ "$status" -ne "$3" ]; then
    echo "FAIL: gt '$1' '$2' exited $status, expected $3"
    failures=$((failures + 1))
  fi
}

# Each pair is strictly increasing: the later one is greater, the earlier one is not.
ordered=(
  1.9.0 1.10.0
  1.10.1 1.11.0
  2.0.0-rc.9 2.0.0-rc.10
  2.0.0-rc.2001 2.0.0-rc.2010
  2.0.0-rc.2001 2.0.0
  2.0.0-rc.1 2.0.0
  1.0.0-alpha 1.0.0-alpha.1
  1.0.0-alpha.1 1.0.0-alpha.beta
  1.0.0-alpha.beta 1.0.0-beta
  1.0.0-beta 1.0.0-beta.2
  1.0.0-beta.2 1.0.0-beta.11
  1.0.0-beta.11 1.0.0-rc.1
  1.0.0-rc.1 1.0.0
  1.0.0-1 1.0.0-alpha
  1.0.0-Z 1.0.0-a
  1.0.0-rc.99999999999999999999 1.0.0-rc.100000000000000000000
  1.2.3 2.0.0-rc.1
  0.13.3 2.0.0-rc.2001
)
for ((i = 0; i < ${#ordered[@]}; i += 2)); do
  gt "v${ordered[i + 1]}" "v${ordered[i]}" 0
  gt "${ordered[i]}" "${ordered[i + 1]}" 1
done
gt v2.0.0-rc.2001 2.0.0-rc.2001 1
gt v1.2.3 v1.2.3 1
gt v1.2.3 release-1 2
gt release-1 v1.2.3 2
gt v1.02.3 v1.2.3 2
reject gt v1.2.3
reject tag
reject other v1.2.3

if [ "$failures" -ne 0 ]; then
  exit 1
fi
echo "release-version.sh: all cases passed"
