#!/bin/bash
# The release workflow's first step (.github/workflows/release.yml, docs/2.0/packaging.md "Releases"): decides the run's
# mode and version, and refuses what mustn't be built. Everything comes from the environment, never from arguments
# expanded into the workflow's script:
#
#   EVENT_NAME     github.event_name (push, workflow_dispatch)
#   REF_TYPE       github.ref_type (tag, branch)
#   REF_NAME       github.ref_name (the tag's name for a tag push)
#   INPUT_VERSION  the dispatch's version input (may be empty)
#   RUN_NUMBER     github.run_number
#   COMMIT_SHA     github.sha (the commit a tag points at)
#   ALLOW_ORIGIN   the repository variable INFOPANEL_ALLOW_ORIGIN
#   RELEASE_BRANCH the ref a tagged commit must be on (default origin/2.0.x)
#
# A push of a tag is a release: the tag must be a bare SemVer 2 version with major 2 (2.0.0, 2.0.0-beta.1), no +build,
# not a -dev version, on a commit of 2.0.x. Anything else is a dry run, stamped with the dispatch's version or
# 2.0.0-dev.ci.<run number> (SemVer 2, major 2, no +build). Writes mode, version and prerelease to $GITHUB_OUTPUT
# (else stdout) and a line to $GITHUB_STEP_SUMMARY when set. Exit 0 accepted, 1 refused.
set -euo pipefail

# A value as it may be shown in a log line: printable version characters only, at most 80.
shown() { printf '%s' "$1" | LC_ALL=C tr -c '0-9A-Za-z.+-' '?' | cut -c1-80; }
refuse() { echo "::error::$1"; exit 1; }

event="${EVENT_NAME:-}"
ref_type="${REF_TYPE:-}"
ref_name="${REF_NAME:-}"
input="${INPUT_VERSION:-}"
run_number="${RUN_NUMBER:-}"
sha="${COMMIT_SHA:-}"
origin="${ALLOW_ORIGIN:-}"
branch="${RELEASE_BRANCH:-origin/2.0.x}"

# The origin first, before anything uses it, and never echoed: a newline in it would start a workflow command.
[ -n "$origin" ] || refuse "Set the repository variable INFOPANEL_ALLOW_ORIGIN to the web client's origin"
if [[ "$origin" =~ [[:cntrl:]] ]] || ! [[ "$origin" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]+)?$ ]]; then
  refuse "The repository variable INFOPANEL_ALLOW_ORIGIN isn't an origin such as https://client.example (scheme, host and an optional port; no path, no spaces)"
fi

if [ "$event" = "push" ] && [ "$ref_type" = "tag" ]; then
  mode=release
  version="$ref_name"
elif [ -n "$input" ]; then
  mode=dry-run
  version="$input"
else
  mode=dry-run
  [[ "$run_number" =~ ^[0-9]+$ ]] || refuse "RUN_NUMBER isn't a number: '$(shown "$run_number")'"
  version="2.0.0-dev.ci.$run_number"
fi

# SemVer 2.0, as packaging/macos/build.sh checks it: numeric parts without leading zeros, an optional -prerelease (dot-
# separated, none empty, numeric ones without leading zeros) and an optional +build.
num='(0|[1-9][0-9]*)'
pre_id='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
build_id='[0-9A-Za-z-]+'
if ! [[ "$version" =~ ^$num\.$num\.$num(-$pre_id(\.$pre_id)*)?(\+$build_id(\.$build_id)*)?$ ]]; then
  refuse "'$(shown "$version")' isn't a SemVer version such as 2.0.0 or 2.0.0-beta.1 (no leading v)"
fi
[ "${BASH_REMATCH[1]}" = "2" ] || refuse "'$(shown "$version")' isn't a 2.x version"
# No +build anywhere: the engine reports the version without it, and the Windows installer's script refuses it.
[[ "$version" != *+* ]] || refuse "'$(shown "$version")' has +build metadata: use the bare version"

if [ "$mode" = "release" ]; then
  # A source build's version: never a release's.
  prerelease_part="${version#*-}"
  if [[ "$version" == *-* ]] && [ "${prerelease_part%%.*}" = "dev" ]; then
    refuse "'$version' is a -dev version, which is a source build's, never a release's"
  fi
  [[ "$sha" =~ ^[0-9a-f]{40}$ ]] || refuse "COMMIT_SHA isn't a commit id: '$(shown "$sha")'"
  git rev-parse --verify --quiet "$branch^{commit}" > /dev/null || refuse "$branch isn't known here: fetch it first"
  git cat-file -e "$sha^{commit}" 2> /dev/null || refuse "the commit $sha isn't known here"
  if ! git merge-base --is-ancestor "$sha" "$branch"; then
    refuse "the tag $version is on $sha, which isn't on $branch: a release is tagged on a 2.0.x commit"
  fi
fi

# Every 2.x release is a GitHub pre-release for now, 2.0.0 included: 1.x's update check offers every release that isn't
# one, so a 2.x release marked otherwise would be offered to 1.x users.
prerelease=true
note=""
if [ "$mode" = "release" ] && [[ "$version" != *-* ]]; then
  note="$version is a stable version, marked pre-release on GitHub like every 2.x release for now."
fi

out="${GITHUB_OUTPUT:-/dev/stdout}"
{
  echo "mode=$mode"
  echo "version=$version"
  echo "prerelease=$prerelease"
} >> "$out"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  echo "**$mode**: InfoPanel $version" >> "$GITHUB_STEP_SUMMARY"
  if [ -n "$note" ]; then
    printf '\n%s\n' "$note" >> "$GITHUB_STEP_SUMMARY"
  fi
fi
[ -z "$note" ] || echo "::notice::$note"
echo "Accepted: $mode, $version"
