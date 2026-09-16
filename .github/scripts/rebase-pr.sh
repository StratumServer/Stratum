#!/usr/bin/env bash
set -euo pipefail

# Rebases a pull request branch onto the current tip of its base branch, strips
# trailing whitespace from the lines the pull request added, and force-pushes
# the result back to the head repository. Merge commits are dropped, and a merge
# carrying changes of its own is refused rather than replayed without them.
# The original author of every commit is preserved and the committer becomes
# github-actions[bot]. The whitespace cleanup lands as one extra commit
# authored by the bot, never mixed into a contributor's commit.
#
# Every input arrives through the environment. Nothing is read from the command
# line and nothing is interpolated into this file by the caller, so a comment
# body, a branch name or a pull request title can never reach a shell here.
#
#   BASE_REPO_URL  clone URL of the repository holding the base branch
#   BASE_REF       base branch name, for example indev
#   HEAD_REPO_URL  clone URL of the repository holding the pull request branch
#   HEAD_REF       pull request branch name
#   HEAD_SHA       pull request head commit as observed when the run started
#   AUTH_TOKEN     optional; used to push over https, never printed
#
# All of the git work happens in a throwaway repository under the system temp
# directory, never in the checkout this script was launched from. That checkout
# is the only copy of these scripts the job has, and checking the pull request
# head out into it would replace them with the contributor's files: the line
# below that runs strip-trailing-whitespace.py would then run the pull
# request's copy, with AUTH_TOKEN in its environment, and the workflow step
# that runs rebase-comment.py afterwards would find the file gone.
#
# Writes exactly one line to stdout, a compact JSON object, and everything else
# to stderr. JSON because conflicting paths and branch names land in it: a
# space separated key=value
# line lets a path called "a RESULT=noop b.cs" rewrite the outcome the caller
# reads. json.dumps also guarantees the line carries no newline, so the caller
# can append it to GITHUB_OUTPUT as is.
#
#   {"result":"rebased","old":...,"new":...,"commits":...,
#    "stripped_files":...,"stripped_lines":...}
#   {"result":"noop","head":...}          nothing to rebase, nothing to strip
#   {"result":"empty","head":...}         the rebase leaves no commits at all
#   {"result":"conflict","files":...}     newline separated paths, no push
#   {"result":"error","reason":...}       protected_branch, merge_carries_changes,
#                                         head_moved, push_stale, push_failed
#
# Exit status: 0 rebased, noop or empty; 1 conflict; 2 error.
#
# set -x is deliberately absent: it would print the push URL and the credential
# helper definition.

: "${BASE_REPO_URL:?BASE_REPO_URL is required}"
: "${BASE_REF:?BASE_REF is required}"
: "${HEAD_REPO_URL:?HEAD_REPO_URL is required}"
: "${HEAD_REF:?HEAD_REF is required}"
: "${HEAD_SHA:?HEAD_SHA is required}"
AUTH_TOKEN="${AUTH_TOKEN:-}"
export AUTH_TOKEN

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# Never wait on a terminal for credentials; a prompt in CI is a hang.
export GIT_TERMINAL_PROMPT=0
bot_name="github-actions[bot]"
bot_email="41898282+github-actions[bot]@users.noreply.github.com"
export GIT_COMMITTER_NAME="$bot_name"
export GIT_COMMITTER_EMAIL="$bot_email"

# Keep the result line on fd 3 and send every other line, including all of
# git's own output, to stderr.
exec 3>&1 1>&2

result() {
  # result <key> <value> [<key> <value> ...]
  python3 -c 'import json, sys
a = sys.argv[1:]
print(json.dumps(dict(zip(a[::2], a[1::2])), separators=(",", ":")))' "$@" >&3
}

# The token reaches git through the environment of a credential helper: never
# in the remote URL, never in an argument, never written to config. The empty
# value first drops any helper the checkout left behind. Single quotes matter,
# AUTH_TOKEN expands when git runs the helper, not when this line is read.
credential_args=()
if [ -n "$AUTH_TOKEN" ]; then
  credential_args=(
    -c credential.helper=
    -c 'credential.helper=!f() { printf "username=x-access-token\npassword=%s\n" "$AUTH_TOKEN"; }; f'
  )
fi

# Nothing here writes to .git/config. Run by hand in a worktree, config --local
# would land in the shared config of every worktree of that clone.
identity_args=(-c "user.name=$bot_name" -c "user.email=$bot_email")

# A pull request whose head branch lives in this repository is rewritten with
# GITHUB_TOKEN, and the release pull request is exactly that: indev into main,
# same repository on both sides. Running the command there would flatten every
# merge commit on indev, rewrite whatever the whitespace pass considers added
# since main, and force-push the lot over the branch everyone works from. A
# fork branch called indev is somebody else's branch and stays allowed.
if [ "$HEAD_REPO_URL" = "$BASE_REPO_URL" ]; then
  case "$HEAD_REF" in
    main | indev)
      result result error reason protected_branch branch "$HEAD_REF"
      exit 2
      ;;
  esac
fi

# The throwaway repository, see the header. Created before the first fetch so
# that nothing this script does can reach the checkout it was launched from.
work="$(mktemp -d)"
strip_message="$work.message"
strip_paths="$work.paths"
trap 'rm -rf "$work" "$strip_message" "$strip_paths"' EXIT
git init --quiet "$work"
cd "$work"

# Both fetches land on a local ref, not just FETCH_HEAD. fetch-pack builds the
# "have" lines it negotiates with from refs/, so without a destination the
# second fetch starts from an empty ref set and the fork ships the whole shared
# history a second time.
git "${credential_args[@]}" fetch --no-tags --quiet "$BASE_REPO_URL" \
  "+refs/heads/$BASE_REF:refs/fetched/base"
base_tip=$(git rev-parse refs/fetched/base)

git "${credential_args[@]}" fetch --no-tags --quiet "$HEAD_REPO_URL" \
  "+refs/heads/$HEAD_REF:refs/fetched/head"
head_tip=$(git rev-parse refs/fetched/head)

echo "base $BASE_REF is at $base_tip"
echo "head $HEAD_REF is at $head_tip"

# The branch moved between the moment the pull request was read and now. Stop
# before touching anything; the lease below would refuse the push anyway.
if [ "$head_tip" != "$HEAD_SHA" ]; then
  result result error reason head_moved expected "$HEAD_SHA" observed "$head_tip"
  exit 2
fi

git checkout --quiet --detach "$head_tip"

# A flattening rebase replays the non-merge commits and nothing else, so
# anything that lives only in a merge commit's tree is dropped: a conflict
# resolved by hand while merging the base branch in, or the compile fix people
# make in the same commit. The replay does not conflict, the push goes through
# and the pull request quietly loses content the reviewer approved. A merge
# whose tree is exactly what merging its parents produces on its own carries
# nothing of its own and is safe to drop; anything else is refused here, before
# the first rewrite.
merge_list=$(git rev-list --merges "$base_tip..$head_tip")
carriers=""
for merge in $merge_list; do
  parents=$(git rev-parse "$merge^@")
  mechanical=""
  if [ "$(printf '%s\n' "$parents" | wc -l)" -eq 2 ]; then
    # Non-zero means the parents do not merge cleanly on their own, so the
    # resolution exists only in this commit. Either way the first line is a
    # tree, and a tree that differs is content the replay would lose.
    mechanical=$(git merge-tree --write-tree $parents 2>/dev/null | head -n 1) || true
  fi
  if [ "$mechanical" != "$(git rev-parse "$merge^{tree}")" ]; then
    carriers="${carriers:+$carriers }$merge"
  fi
done
if [ -n "$carriers" ]; then
  result result error reason merge_carries_changes merges "$carriers"
  exit 2
fi

replayed=0
if [ -z "$merge_list" ] && git merge-base --is-ancestor "$base_tip" "$head_tip"; then
  echo "already linear on top of $BASE_REF, only the whitespace cleanup runs"
else
  # --onto <base> <base> replays base_tip..HEAD onto base_tip. Merge commits are
  # dropped because a plain rebase flattens; --no-rebase-merges makes that
  # independent of whatever rebase.rebaseMerges the runner happens to carry.
  if ! git "${identity_args[@]}" rebase --no-rebase-merges --onto "$base_tip" "$base_tip"; then
    files=$(git -c core.quotePath=false diff --name-only --diff-filter=U)
    git rebase --abort || true
    result result conflict files "${files:-unknown}"
    exit 1
  fi
  replayed=$(git rev-list --count "$base_tip..HEAD")
fi

# Zero commits left means every commit was already in the base branch. Pushing
# would leave an empty pull request, and calling that "already rebased" hides
# the real news, which is that the pull request can be closed.
if [ "$(git rev-list --count "$base_tip..HEAD")" -eq 0 ]; then
  echo "the rebase leaves no commits"
  result result empty head "$head_tip"
  exit 0
fi

# Whitespace cleanup, on the lines this pull request added and nothing else.
# It runs even when the rebase was a no-op, so /rebase always leaves the branch
# clean. python3 ships on ubuntu-latest. script_dir is this repository's
# checkout, which the work above never touched, so this is our own copy of the
# helper and not one the pull request shipped.
totals=$(BASE_TIP="$base_tip" STRIP_MESSAGE="$strip_message" \
  STRIP_PATHS="$strip_paths" python3 "$script_dir/strip-trailing-whitespace.py")
stripped_files=${totals% *}
stripped_lines=${totals#* }
if [ "$stripped_lines" -gt 0 ]; then
  echo "stripped $stripped_lines line(s) in $stripped_files file(s)"
  # Stage the rewritten files and only those, with the bytes the strip pass
  # wrote. commit --all would sweep in every other file the work tree shows as
  # modified, and any staging path runs the checkin filter, which renormalizes
  # a whole file whose line endings disagree with .gitattributes. Either one
  # puts changes the pull request never made into a commit whose message says
  # it only stripped trailing whitespace.
  while IFS= read -r -d '' path; do
    entry=$(git ls-files --stage -z -- ":(literal)$path" | tr -d '\0')
    blob=$(git hash-object -w --no-filters -- "$path")
    git update-index --cacheinfo "${entry%% *},$blob,$path"
  done <"$strip_paths"
  GIT_AUTHOR_NAME="$bot_name" GIT_AUTHOR_EMAIL="$bot_email" \
    git "${identity_args[@]}" commit --quiet --file "$strip_message"
fi

new_sha=$(git rev-parse HEAD)
if [ "$new_sha" = "$head_tip" ]; then
  result result noop head "$head_tip"
  exit 0
fi

# --force-with-lease pins the ref to the sha the pull request was read at, so a
# push that races a contributor push is refused instead of overwriting it.
if ! push_log=$(git "${credential_args[@]}" push --quiet \
  --force-with-lease="refs/heads/$HEAD_REF:$HEAD_SHA" \
  "$HEAD_REPO_URL" "$new_sha:refs/heads/$HEAD_REF" 2>&1); then
  printf '%s\n' "$push_log"
  # A lost lease and a rejected token are different problems with different
  # answers. Telling a maintainer to retry a run that failed on an expired
  # secret makes them retry forever.
  case "$push_log" in
    *"stale info"*) reason=push_stale ;;
    *) reason=push_failed ;;
  esac
  result result error reason "$reason"
  exit 2
fi

result result rebased old "$head_tip" new "$new_sha" commits "$replayed" \
  stripped_files "$stripped_files" stripped_lines "$stripped_lines"
