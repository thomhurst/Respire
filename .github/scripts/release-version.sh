#!/usr/bin/env bash
# Release version helpers shared by release-draft.yml and modular-pipeline.yml.
#
#   release-version.sh tag <tag>   Prints the version a release tag stands for (v1.2.3 -> 1.2.3).
#                                  Fails unless the tag is X.Y.Z with an optional v prefix and an
#                                  optional prerelease suffix (v2.0.0-rc.1). Build metadata is rejected.
#   release-version.sh next <tag>  Prints the version one step above a release tag: the next patch
#                                  of a stable version (v1.2.3 -> 1.2.4), or the next candidate of a
#                                  prerelease (v2.0.0-rc.5 -> 2.0.0-rc.6, v2.0.0-beta -> 2.0.0-beta.1).
#
# Keep the grammar and the step in sync with GitVersionDetails in
# Respire.Pipeline/Modules/NugetVersionGeneratorModule.cs.
set -euo pipefail

identifier='[0-9A-Za-z-]+'
pattern="^[vV]?([0-9]+)\.([0-9]+)\.([0-9]+)(-(${identifier}(\.${identifier})*))?$"

parse() {
  if [[ ! "$1" =~ $pattern ]]; then
    echo "::error::'$1' is not a release version. Use a tag such as v1.2.3 or v2.0.0-rc.1." >&2
    return 1
  fi
  major=$((10#${BASH_REMATCH[1]}))
  minor=$((10#${BASH_REMATCH[2]}))
  patch=$((10#${BASH_REMATCH[3]}))
  prerelease=${BASH_REMATCH[5]}
}

case "${1:-} ${2+set}" in
  'tag set')
    parse "$2"
    echo "$major.$minor.$patch${prerelease:+-$prerelease}"
    ;;
  'next set')
    parse "$2"
    if [ -z "$prerelease" ]; then
      echo "$major.$minor.$((patch + 1))"
    elif [[ "$prerelease" =~ ^(.*\.)?([0-9]+)$ ]]; then
      echo "$major.$minor.$patch-${BASH_REMATCH[1]}$((10#${BASH_REMATCH[2]} + 1))"
    else
      echo "$major.$minor.$patch-$prerelease.1"
    fi
    ;;
  *)
    echo "usage: $0 tag|next <tag>" >&2
    exit 2
    ;;
esac
