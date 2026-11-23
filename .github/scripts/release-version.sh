#!/usr/bin/env bash
# Release version helpers shared by release-draft.yml and modular-pipeline.yml.
#
#   release-version.sh tag <tag>   Prints the version a release tag stands for (v1.2.3 -> 1.2.3).
#                                  Fails unless the tag is X.Y.Z with an optional v prefix and an
#                                  optional prerelease suffix (v2.0.0-rc.1). Build metadata is rejected.
#   release-version.sh next <tag>  Prints the version one step above a release tag: the next patch
#                                  of a stable version (v1.2.3 -> 1.2.4), or the next candidate of a
#                                  prerelease (v2.0.0-rc.5 -> 2.0.0-rc.6, v2.0.0-beta -> 2.0.0-beta.1).
#   release-version.sh gt <a> <b>  Exits 0 when version a has higher SemVer 2.0 precedence than b,
#                                  1 when it does not (equal or lower), and 2 when either is not a
#                                  release version.
#
# Keep the grammar and the step in sync with GitVersionDetails in
# Respire.Pipeline/Modules/NugetVersionGeneratorModule.cs.
set -euo pipefail
# Bash 4.4+ (CI runners); older shells such as macOS bash 3.2 run without it.
shopt -s inherit_errexit 2> /dev/null || true
# String comparisons below must use ASCII order, not the locale's collation.
export LC_ALL=C

# SemVer 2.0 without build metadata: no leading zeros in numeric parts or numeric prerelease
# identifiers, and each of major, minor and patch at most 65534 because they become the assembly
# version.
number='(0|[1-9][0-9]*)'
identifier='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
pattern="^[vV]?${number}\\.${number}\\.${number}(-(${identifier}(\\.${identifier})*))?$"

parse() {
  if [[ ! "$1" =~ $pattern ]]; then
    echo "::error::'$1' is not a release version. Use a tag such as v1.2.3 or v2.0.0-rc.1." >&2
    return 1
  fi
  local part
  for part in "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}"; do
    if [ "${#part}" -gt 5 ] || [ "$part" -gt 65534 ]; then
      echo "::error::'$1' is not a release version: each of major, minor and patch must be at most 65534." >&2
      return 1
    fi
  done
  major=${BASH_REMATCH[1]}
  minor=${BASH_REMATCH[2]}
  patch=${BASH_REMATCH[3]}
  prerelease=${BASH_REMATCH[5]}
}

# Compares two numbers of any length (no leading zeros): prints -1, 0 or 1.
compare_numbers() {
  if [ "${#1}" -ne "${#2}" ]; then
    [ "${#1}" -lt "${#2}" ] && echo -1 || echo 1
  elif [[ "$1" == "$2" ]]; then
    echo 0
  elif [[ "$1" < "$2" ]]; then
    echo -1
  else
    echo 1
  fi
}

# SemVer 2.0 precedence of two prerelease labels, where an empty label (a stable version) ranks
# above any prerelease: prints -1, 0 or 1.
compare_prereleases() {
  if [ "$1" = "$2" ]; then
    echo 0
    return
  fi
  if [ -z "$1" ]; then
    echo 1
    return
  fi
  if [ -z "$2" ]; then
    echo -1
    return
  fi
  local -a left right
  IFS=. read -ra left <<< "$1"
  IFS=. read -ra right <<< "$2"
  local index a b numeric='^(0|[1-9][0-9]*)$'
  for ((index = 0; index < ${#left[@]} && index < ${#right[@]}; index++)); do
    a=${left[index]}
    b=${right[index]}
    if [ "$a" = "$b" ]; then
      continue
    fi
    if [[ "$a" =~ $numeric ]] && [[ "$b" =~ $numeric ]]; then
      compare_numbers "$a" "$b"
    elif [[ "$a" =~ $numeric ]]; then
      echo -1
    elif [[ "$b" =~ $numeric ]]; then
      echo 1
    elif [[ "$a" < "$b" ]]; then
      echo -1
    else
      echo 1
    fi
    return
  done
  # All shared identifiers are equal: the longer list ranks higher.
  if [ "${#left[@]}" -lt "${#right[@]}" ]; then echo -1; else echo 1; fi
}

case "${1:-} ${2+set}" in
  'tag set')
    parse "$2"
    echo "$major.$minor.$patch${prerelease:+-$prerelease}"
    ;;
  'next set')
    parse "$2"
    if [ -z "$prerelease" ]; then
      next="$major.$minor.$((patch + 1))"
    elif [[ "$prerelease" =~ ^(.*\.)?(0|[1-9][0-9]*)$ ]]; then
      next="$major.$minor.$patch-${BASH_REMATCH[1]}$((BASH_REMATCH[2] + 1))"
    else
      next="$major.$minor.$patch-$prerelease.1"
    fi
    # The step can leave the grammar (patch 65534 -> 65535); fail rather than suggest it.
    parse "$next"
    echo "$next"
    ;;
  'gt set')
    if [ "$#" -ne 3 ]; then
      echo "usage: $0 gt <a> <b>" >&2
      exit 2
    fi
    parse "$2" || exit 2
    a=("$major" "$minor" "$patch" "$prerelease")
    parse "$3" || exit 2
    b=("$major" "$minor" "$patch" "$prerelease")
    for index in 0 1 2; do
      order=$(compare_numbers "${a[index]}" "${b[index]}")
      if [ "$order" != 0 ]; then
        [ "$order" = 1 ] && exit 0 || exit 1
      fi
    done
    [ "$(compare_prereleases "${a[3]}" "${b[3]}")" = 1 ] && exit 0 || exit 1
    ;;
  *)
    echo "usage: $0 tag|next <tag> | gt <a> <b>" >&2
    exit 2
    ;;
esac
