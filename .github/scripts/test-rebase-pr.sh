#!/usr/bin/env bash
set -euo pipefail

# Offline harness for rebase-pr.sh, strip-trailing-whitespace.py and
# rebase-comment.py. Builds bare upstream and fork repositories under a temp
# directory, drives the scripts with file:// remotes and no token, and asserts
# every outcome they can reach. Exits non-zero if any assertion fails.
#
#   bash .github/scripts/test-rebase-pr.sh

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
comment_script="$script_dir/rebase-comment.py"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# Ignore whatever the developer has in ~/.gitconfig; the scripts' behaviour
# must not depend on it.
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME="Setup"
export GIT_AUTHOR_EMAIL="setup@example.invalid"
export GIT_COMMITTER_NAME="Setup"
export GIT_COMMITTER_EMAIL="setup@example.invalid"

bot_email="41898282+github-actions[bot]@users.noreply.github.com"
contributor="contributor@example.invalid"

# Every test case works under $tmp, made by mktemp above. Nothing here should
# ever touch the worktree this harness itself was launched from; case 18 below
# checks that it did not.
repo_root="$(git -C "$script_dir" rev-parse --show-toplevel)"
worktree_status_before="$(git -C "$repo_root" status --porcelain)"

checks=0
failures=0

pass() {
  checks=$((checks + 1))
  printf 'ok    %s\n' "$1"
}

fail() {
  checks=$((checks + 1))
  failures=$((failures + 1))
  printf 'FAIL  %s\n        %s\n' "$1" "$2"
}

expect_eq() {
  if [ "$2" = "$3" ]; then
    pass "$1"
  else
    fail "$1" "got [$2], want [$3]"
  fi
}

expect_contains() {
  # expect_contains <label> <haystack> <needle>
  case "$2" in
    *"$3"*) pass "$1" ;;
    *) fail "$1" "[$3] not found in [$2]" ;;
  esac
}

expect_missing() {
  # expect_missing <label> <haystack> <needle>
  case "$2" in
    *"$3"*) fail "$1" "[$3] should not appear in [$2]" ;;
    *) pass "$1" ;;
  esac
}

expect_bytes() {
  # expect_bytes <label> <actual file> <expected file>
  if cmp -s "$2" "$3"; then
    pass "$1"
  else
    fail "$1" "bytes differ: $(od -c "$2" | head -8 | tr '\n' '|')"
  fi
}

commit() {
  # commit <repo> <file> <content> <message>
  printf '%s\n' "$3" >"$1/$2"
  git -C "$1" add -- "$2"
  git -C "$1" commit --quiet -m "$4"
}

# Creates <case>/upstream.git with two branches, <case>/fork.git, a working
# clone <case>/work with both as remotes, and <case>/runner, a clone of main
# standing in for the workflow's actions/checkout. Leaves work on indev.
#
# The two branches matter. issue_comment workflows only run from the default
# branch, so main is where the scripts ship, while every pull request targets
# indev, which does not carry them. Running them from $d/runner by their
# relative path, the way the workflow does, is what makes this harness able to
# see a run that replaces that checkout with the pull request's own files.
new_repo() {
  local d="$tmp/$1"
  mkdir -p "$d"
  git init --quiet --bare --initial-branch=main "$d/upstream.git"
  git init --quiet --bare --initial-branch=indev "$d/fork.git"

  git init --quiet --initial-branch=main "$d/work"
  mkdir -p "$d/work/.github/scripts"
  cp "$script_dir/rebase-pr.sh" "$script_dir/strip-trailing-whitespace.py" \
    "$script_dir/rebase-comment.py" "$d/work/.github/scripts/"
  git -C "$d/work" add -A
  git -C "$d/work" commit --quiet -m "Add the rebase scripts"
  git -C "$d/work" remote add origin "file://$d/upstream.git"
  git -C "$d/work" remote add fork "file://$d/fork.git"
  git -C "$d/work" push --quiet origin main

  # --orphan, so indev has no .github at all, as upstream/indev has none.
  git -C "$d/work" switch --quiet --orphan indev
  commit "$d/work" base.txt "base one" "Add base file"
  git -C "$d/work" push --quiet origin indev

  git clone --quiet --branch main "file://$d/upstream.git" "$d/runner"
}

# new_repo plus the fork branch "feature", two commits by a third party.
new_case() {
  new_repo "$1"
  local d="$tmp/$1"
  git -C "$d/work" switch --quiet -c feature
  GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
    commit "$d/work" feature.txt "feature one" "Add feature one"
  GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
    commit "$d/work" feature2.txt "feature two" "Add feature two"
  git -C "$d/work" push --quiet fork feature
}

advance_base() {
  # advance_base <case> <file> <content> <message>
  local d="$tmp/$1"
  git -C "$d/work" switch --quiet indev
  commit "$d/work" "$2" "$3" "$4"
  git -C "$d/work" push --quiet origin indev
  git -C "$d/work" switch --quiet feature
}

run_rebase() {
  # run_rebase <case> <head sha> [head repo url] [head ref] [base ref]; sets rc
  # and out. Invoked from the runner checkout by relative path, as the workflow
  # does.
  local d="$tmp/$1"
  set +e
  out="$(
    cd "$d/runner" &&
      BASE_REPO_URL="file://$d/upstream.git" \
        BASE_REF="${5:-indev}" \
        HEAD_REPO_URL="${3:-file://$d/fork.git}" \
        HEAD_REF="${4:-feature}" \
        HEAD_SHA="$2" \
        bash .github/scripts/rebase-pr.sh 2>"$d/stderr"
  )"
  rc=$?
  set -e
}

fork_head() {
  git -C "$tmp/$1/fork.git" rev-parse refs/heads/feature
}

# The pushed objects live in the fork, and the runner checkout must be exactly
# what it was before the run, so everything is read back from the fork.
seen() {
  # seen <case> <git argument>...
  local d="$tmp/$1"
  shift
  git -C "$d/fork.git" "$@"
}

expect_workspace_intact() {
  # expect_workspace_intact <case>. The workflow runs rebase-comment.py from
  # this checkout after the rebase step, and the rebase script reads its own
  # python helper from it, so a run that leaves the pull request's tree here
  # breaks the next step and executes files the pull request wrote.
  local d="$tmp/$1"
  expect_eq "workspace is still the checkout of main" \
    "$(git -C "$d/runner" rev-parse --abbrev-ref HEAD)" "main"
  expect_eq "workspace has no added or removed file" \
    "$(git -C "$d/runner" status --porcelain | wc -l)" "0"
}

field() {
  # field <json result line> <name>
  RESULT_LINE="$1" FIELD="$2" python3 -c 'import json, os
line = os.environ["RESULT_LINE"].strip() or "{}"
print(json.loads(line).get(os.environ["FIELD"], ""))'
}

echo "== case 1: merge commit dropped, tree preserved =="
new_case merged
advance_base merged extra.txt "extra one" "Add extra file"
git -C "$tmp/merged/work" merge --quiet --no-ff -m "Merge branch 'indev' into feature" indev
git -C "$tmp/merged/work" push --quiet fork feature
merged_head="$(fork_head merged)"
merged_tree="$(git -C "$tmp/merged/work" rev-parse 'HEAD^{tree}')"
merged_authored="$(git -C "$tmp/merged/work" log -2 --format='%at %ae %s' "$merged_head^1")"
base_tip="$(git -C "$tmp/merged/upstream.git" rev-parse refs/heads/indev)"

run_rebase merged "$merged_head"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "result is rebased" "$(field "$out" result)" "rebased"
expect_eq "old sha reported" "$(field "$out" old)" "$merged_head"
expect_eq "two commits replayed" "$(field "$out" commits)" "2"
expect_eq "nothing to strip" "$(field "$out" stripped_lines)" "0"

new_head="$(fork_head merged)"
expect_eq "fork branch moved to the new head" "$new_head" "$(field "$out" new)"
expect_eq "merge commit dropped" \
  "$(seen merged rev-list --merges --count "$base_tip..$new_head")" "0"
expect_eq "base tip is now an ancestor" \
  "$(seen merged merge-base --is-ancestor "$base_tip" "$new_head" && echo yes)" "yes"
expect_eq "tree is byte identical" \
  "$(seen merged rev-parse "$new_head^{tree}")" "$merged_tree"
expect_eq "authors, author dates and subjects preserved" \
  "$(seen merged log -2 --format='%at %ae %s' "$new_head")" "$merged_authored"
expect_eq "committer is the bot" \
  "$(seen merged log -2 --format='%ce' "$new_head" | sort -u)" "$bot_email"
expect_eq "checkout config untouched" \
  "$(git -C "$tmp/merged/runner" config --local --get-regexp '^(user|credential)\.' | wc -l)" "0"
expect_workspace_intact merged

echo
echo "== case 2: already rebased and nothing to strip =="
new_case linear
linear_head="$(fork_head linear)"
run_rebase linear "$linear_head"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "result is noop" "$(field "$out" result)" "noop"
expect_eq "fork branch untouched" "$(fork_head linear)" "$linear_head"

echo
echo "== case 3: conflicting commit on the base branch =="
new_case conflict
advance_base conflict feature.txt "base version" "Add feature file upstream"
conflict_head="$(fork_head conflict)"
run_rebase conflict "$conflict_head"
expect_eq "exit status is 1" "$rc" "1"
expect_eq "result is conflict" "$(field "$out" result)" "conflict"
expect_eq "conflicting file listed" "$(field "$out" files)" "feature.txt"
expect_eq "fork branch untouched" "$(fork_head conflict)" "$conflict_head"
expect_workspace_intact conflict

echo
echo "== case 4: branch moved before the run started =="
new_case raced
advance_base raced extra.txt "extra one" "Add extra file"
stale_head="$(fork_head raced)"
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  commit "$tmp/raced/work" feature3.txt "feature three" "Add feature three"
git -C "$tmp/raced/work" push --quiet fork feature
moved_head="$(fork_head raced)"

run_rebase raced "$stale_head"
expect_eq "exit status is 2" "$rc" "2"
expect_eq "result is error" "$(field "$out" result)" "error"
expect_eq "reason is head_moved" "$(field "$out" reason)" "head_moved"
expect_eq "fork branch untouched" "$(fork_head raced)" "$moved_head"

echo
echo "== case 5: branch moves after the fetch, the lease refuses the push =="
# The pre-check in case 4 cannot see this window. A post-checkout hook moves
# the fork branch while the script is between its fetch and its push, which is
# the only thing --force-with-lease covers. Replace the lease with a plain
# --force and this case fails. The hook reaches the script's own throwaway
# repository through GIT_TEMPLATE_DIR, since that repository is created by the
# script and deleted when it returns.
new_case lease
advance_base lease extra.txt "extra one" "Add extra file"
git -C "$tmp/lease/work" merge --quiet --no-ff -m "Merge branch 'indev' into feature" indev
git -C "$tmp/lease/work" push --quiet fork feature
lease_head="$(fork_head lease)"

# The racing commit, parked on another ref so its objects already live in the
# bare fork when the hook repoints the branch.
git -C "$tmp/lease/work" switch --quiet -c racer
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  commit "$tmp/lease/work" racer.txt "racer" "Add racer commit"
git -C "$tmp/lease/work" push --quiet fork racer
racer_sha="$(git -C "$tmp/lease/fork.git" rev-parse refs/heads/racer)"
git -C "$tmp/lease/work" switch --quiet feature

mkdir -p "$tmp/lease/template/hooks"
cat >"$tmp/lease/template/hooks/post-checkout" <<HOOK
#!/bin/sh
# Fires once, on the script's first checkout, after it has fetched the fork.
[ -e "$tmp/lease/raced" ] && exit 0
: > "$tmp/lease/raced"
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE
exec git --git-dir="$tmp/lease/fork.git" update-ref refs/heads/feature "$racer_sha"
HOOK
chmod +x "$tmp/lease/template/hooks/post-checkout"

export GIT_TEMPLATE_DIR="$tmp/lease/template"
run_rebase lease "$lease_head"
unset GIT_TEMPLATE_DIR
expect_eq "the hook fired mid run" "$(test -e "$tmp/lease/raced" && echo yes || echo no)" "yes"
expect_eq "exit status is 2" "$rc" "2"
expect_eq "result is error" "$(field "$out" result)" "error"
expect_eq "reason is push_stale" "$(field "$out" reason)" "push_stale"
expect_eq "fork branch still holds the racing commit" "$(fork_head lease)" "$racer_sha"

echo
echo "== case 6: the rebase empties the branch =="
new_case empty
git -C "$tmp/empty/work" switch --quiet indev
printf 'feature one\n' >"$tmp/empty/work/feature.txt"
printf 'feature two\n' >"$tmp/empty/work/feature2.txt"
git -C "$tmp/empty/work" add -A
git -C "$tmp/empty/work" commit --quiet -m "Land the same content upstream"
git -C "$tmp/empty/work" push --quiet origin indev
git -C "$tmp/empty/work" switch --quiet feature
empty_head="$(fork_head empty)"

run_rebase empty "$empty_head"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "result is empty, not noop" "$(field "$out" result)" "empty"
expect_eq "head still reported as the old tip" "$(field "$out" head)" "$empty_head"
expect_eq "fork branch untouched" "$(fork_head empty)" "$empty_head"

echo
echo "== case 7: trailing whitespace on the lines the pull request added =="
new_repo ws
d="$tmp/ws"
w="$d/work"
mkdir -p "$w/sources" "$w/patches" "$w/docs" "$w/scripts" "$w/baseline/VintagestoryLib"
printf 'namespace A;\nold line with trailing   \n' >"$w/sources/legacy.cs"
git -C "$w" add -A
git -C "$w" commit --quiet -m "Add legacy file"
git -C "$w" push --quiet origin indev

git -C "$w" switch --quiet -c feature
printf 'namespace A;\nold line with trailing   \nadded line with trailing   \n' >"$w/sources/legacy.cs"
printf 'namespace B;\nclean line\n' >"$w/sources/clean.cs"
{
  printf 'diff --git a/x.cs b/x.cs   \n'
  printf 'index 1111111..2222222 100644\n'
  printf -- '--- a/x.cs   \n'
  printf '+++ b/x.cs   \n'
  printf '@@ -1,3 +1,4 @@   \n'
  printf ' context line with trailing  \n'
  printf -- '-removed line with trailing  \n'
  printf '+added line with trailing  \n'
  printf '+   \n'
  printf ' tail context\n'
} >"$w/patches/sample.patch"
printf 'A hard break line  \nand the next one.\n' >"$w/docs/notes.md"
printf 'Write-Host "hi"   \r\nWrite-Host "bye"\r\n' >"$w/scripts/thing.ps1"
printf 'generated line   \n' >"$w/baseline/VintagestoryLib/Gen.cs"
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add files through a translation tool"
git -C "$w" push --quiet fork feature
ws_head="$(fork_head ws)"

run_rebase ws "$ws_head"
ws_new="$(field "$out" new)"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "result is rebased" "$(field "$out" result)" "rebased"
expect_eq "no commit was replayed, only the cleanup ran" "$(field "$out" commits)" "0"
expect_eq "three files stripped" "$(field "$out" stripped_files)" "3"
expect_eq "four lines stripped" "$(field "$out" stripped_lines)" "4"
expect_eq "fork branch moved to the cleanup commit" "$(fork_head ws)" "$ws_new"
expect_eq "the cleanup commit is the only new commit" \
  "$(git -C "$d/fork.git" rev-parse "$ws_new~1")" "$ws_head"
expect_eq "cleanup commit author is the bot" \
  "$(git -C "$d/fork.git" log -1 --format='%ae' "$ws_new")" "$bot_email"
expect_eq "cleanup commit committer is the bot" \
  "$(git -C "$d/fork.git" log -1 --format='%ce' "$ws_new")" "$bot_email"
expect_eq "cleanup commit subject" \
  "$(git -C "$d/fork.git" log -1 --format='%s' "$ws_new")" \
  "Strip trailing whitespace from added lines"
expect_eq "cleanup commit body lists the files" \
  "$(git -C "$d/fork.git" log -1 --format='%b' "$ws_new" | grep -c ': [0-9] line(s)')" "3"
expect_eq "cleanup touched exactly the three files" \
  "$(git -C "$d/fork.git" diff --name-only "$ws_head" "$ws_new" | sort | tr '\n' ' ')" \
  "patches/sample.patch scripts/thing.ps1 sources/legacy.cs "

got="$d/got"
want="$d/want"

git -C "$d/fork.git" show "$ws_new:sources/legacy.cs" >"$got"
printf 'namespace A;\nold line with trailing   \nadded line with trailing\n' >"$want"
expect_bytes "added .cs line stripped, the line it did not add kept" "$got" "$want"

git -C "$d/fork.git" show "$ws_new:patches/sample.patch" >"$got"
{
  printf 'diff --git a/x.cs b/x.cs   \n'
  printf 'index 1111111..2222222 100644\n'
  printf -- '--- a/x.cs   \n'
  printf '+++ b/x.cs   \n'
  printf '@@ -1,3 +1,4 @@   \n'
  printf ' context line with trailing  \n'
  printf -- '-removed line with trailing  \n'
  printf '+added line with trailing\n'
  printf '+\n'
  printf ' tail context\n'
} >"$want"
expect_bytes "patch: only the diff's own added lines stripped, headers and context kept" "$got" "$want"

git -C "$d/fork.git" show "$ws_new:docs/notes.md" >"$got"
printf 'A hard break line  \nand the next one.\n' >"$want"
expect_bytes "markdown hard break kept" "$got" "$want"

git -C "$d/fork.git" show "$ws_new:scripts/thing.ps1" >"$got"
printf 'Write-Host "hi"\r\nWrite-Host "bye"\r\n' >"$want"
expect_bytes "CRLF file keeps its endings" "$got" "$want"

git -C "$d/fork.git" show "$ws_new:baseline/VintagestoryLib/Gen.cs" >"$got"
printf 'generated line   \n' >"$want"
expect_bytes "generated tree left alone" "$got" "$want"

git -C "$d/fork.git" show "$ws_new:sources/clean.cs" >"$got"
printf 'namespace B;\nclean line\n' >"$want"
expect_bytes "file with nothing to strip left alone" "$got" "$want"

echo
echo "== case 8: the pull request ships its own copy of the scripts =="
# The run reads the scripts from the checkout it was launched from, and the
# workflow runs rebase-comment.py from there afterwards. A run that checks the
# pull request head out into that checkout would run the contributor's copy of
# both, with the push token in its environment.
new_repo hostile
d="$tmp/hostile"
w="$d/work"
git -C "$w" switch --quiet -c feature
mkdir -p "$w/.github/scripts" "$w/sources"
cat >"$w/.github/scripts/strip-trailing-whitespace.py" <<PY
import os
open("$d/pwned", "w").write("AUTH_TOKEN=%s\n" % os.environ.get("AUTH_TOKEN", ""))
print("0 0")
PY
cp "$w/.github/scripts/strip-trailing-whitespace.py" "$w/.github/scripts/rebase-comment.py"
printf 'namespace C;\nadded with trailing   \n' >"$w/sources/hostile.cs"
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add a helper script"
git -C "$w" push --quiet fork feature
hostile_head="$(fork_head hostile)"

export AUTH_TOKEN="ghp_not_a_real_token"
run_rebase hostile "$hostile_head"
unset AUTH_TOKEN
expect_eq "exit status is 0" "$rc" "0"
expect_eq "the pull request's script never ran" \
  "$(test -e "$d/pwned" && echo yes || echo no)" "no"
expect_eq "our own cleanup ran instead" "$(field "$out" stripped_lines)" "1"
expect_workspace_intact hostile
expect_eq "the comment script in the workspace is still ours" \
  "$(cmp -s "$d/runner/.github/scripts/rebase-comment.py" "$comment_script" && echo yes)" "yes"

echo
echo "== case 9: a renamed file keeps the lines the pull request did not add =="
new_repo renamed
d="$tmp/renamed"
w="$d/work"
mkdir -p "$w/sources"
printf 'public class Foo\n{\n\tvoid A() { }   \n\tvoid B() { }   \n}\n' >"$w/sources/Foo.cs"
git -C "$w" add -A
git -C "$w" commit --quiet -m "Add Foo"
git -C "$w" push --quiet origin indev

git -C "$w" switch --quiet -c feature
git -C "$w" mv sources/Foo.cs sources/Bar.cs
printf 'public class Bar\n{\n\tvoid A() { }   \n\tvoid B() { }   \n\tvoid C() { }   \n}\n' \
  >"$w/sources/Bar.cs"
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Rename Foo to Bar and add C"
git -C "$w" push --quiet fork feature
renamed_head="$(fork_head renamed)"

run_rebase renamed "$renamed_head"
renamed_new="$(field "$out" new)"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "only the added line counted" "$(field "$out" stripped_lines)" "1"

got="$d/got"
want="$d/want"
seen renamed show "$renamed_new:sources/Bar.cs" >"$got"
printf 'public class Bar\n{\n\tvoid A() { }   \n\tvoid B() { }   \n\tvoid C() { }\n}\n' >"$want"
expect_bytes "the renamed file keeps the trailing whitespace it arrived with" "$got" "$want"

echo
echo "== case 10: a rebase and a cleanup in the same run =="
new_case combined
d="$tmp/combined"
w="$d/work"
mkdir -p "$w/sources"
printf 'namespace D;\nadded with trailing   \n' >"$w/sources/translated.cs"
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add a translated file"
advance_base combined extra.txt "extra one" "Add extra file"
git -C "$w" merge --quiet --no-ff -m "Merge branch 'indev' into feature" indev
git -C "$w" push --quiet fork feature
combined_head="$(fork_head combined)"
combined_base="$(git -C "$d/upstream.git" rev-parse refs/heads/indev)"

run_rebase combined "$combined_head"
combined_new="$(field "$out" new)"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "three commits replayed" "$(field "$out" commits)" "3"
expect_eq "one line stripped" "$(field "$out" stripped_lines)" "1"
expect_eq "the cleanup commit is the only commit on top of the replayed three" \
  "$(seen combined rev-list --count "$combined_base..$combined_new")" "4"
expect_eq "cleanup commit is the bot's" \
  "$(seen combined log -1 --format='%ae' "$combined_new")" "$bot_email"
expect_eq "the replayed commits keep their author" \
  "$(seen combined log -3 --format='%ae' "$combined_new~1" | sort -u)" "$contributor"

got="$d/got"
want="$d/want"
seen combined show "$combined_new~1:sources/translated.cs" >"$got"
printf 'namespace D;\nadded with trailing   \n' >"$want"
expect_bytes "the contributor's own commit was not rewritten" "$got" "$want"
seen combined show "$combined_new:sources/translated.cs" >"$got"
printf 'namespace D;\nadded with trailing\n' >"$want"
expect_bytes "the cleanup commit carries the stripped file" "$got" "$want"

echo
echo "== case 11: a branch of this repository is refused =="
# The release pull request is indev into main, both sides this repository, and
# rebasing it would flatten indev and force-push it over everyone's clone.
new_case protected
d="$tmp/protected"
protected_indev="$(git -C "$d/upstream.git" rev-parse refs/heads/indev)"
run_rebase protected "$protected_indev" "file://$d/upstream.git" indev main
expect_eq "exit status is 2" "$rc" "2"
expect_eq "result is error" "$(field "$out" result)" "error"
expect_eq "reason is protected_branch" "$(field "$out" reason)" "protected_branch"
expect_eq "the base repository's indev is untouched" \
  "$(git -C "$d/upstream.git" rev-parse refs/heads/indev)" "$protected_indev"
expect_workspace_intact protected

# The guard is about the repository, not the name: a contributor's own branch
# called indev is an ordinary pull request branch.
git -C "$d/work" push --quiet fork feature:indev
fork_indev="$(git -C "$d/fork.git" rev-parse refs/heads/indev)"
run_rebase protected "$fork_indev" "file://$d/fork.git" indev
expect_eq "a fork branch called indev is still served" "$(field "$out" result)" "noop"

echo
echo "== case 12: a merge commit carrying its own changes is refused =="
# The rebase replays the non-merge commits, so anything written into a merge
# commit itself is dropped. Here the contributor merged the base branch in and
# adapted the code to it in that same commit: replaying alone would not
# conflict, so the branch would be force-pushed back missing the adaptation
# while the comment reported a clean rebase.
new_case evil
d="$tmp/evil"
w="$d/work"
advance_base evil extra.txt "extra one" "Add extra file"
git -C "$w" merge --quiet --no-ff -m "Merge branch 'indev' into feature" indev
printf 'feature two, adapted to the new base\n' >"$w/feature2.txt"
git -C "$w" add -A
git -C "$w" commit --quiet --amend --no-edit
git -C "$w" push --quiet fork feature
evil_head="$(fork_head evil)"

run_rebase evil "$evil_head"
expect_eq "exit status is 2" "$rc" "2"
expect_eq "result is error" "$(field "$out" result)" "error"
expect_eq "reason is merge_carries_changes" "$(field "$out" reason)" "merge_carries_changes"
expect_eq "the merge commit is named" "$(field "$out" merges)" "$evil_head"
expect_eq "fork branch untouched" "$(fork_head evil)" "$evil_head"
expect_workspace_intact evil

echo
echo "== case 13: a conflict resolved inside a merge commit is refused =="
# Same loss, the ordinary way it happens: the merge conflicted and the
# resolution lives in the merge commit alone.
new_case resolved
d="$tmp/resolved"
w="$d/work"
advance_base resolved feature.txt "base version" "Rewrite the feature file upstream"
git -C "$w" merge --no-ff --no-commit indev >/dev/null 2>&1 || true
printf 'resolved by hand\n' >"$w/feature.txt"
git -C "$w" add -A
git -C "$w" commit --quiet -m "Merge branch 'indev' into feature"
git -C "$w" push --quiet fork feature
resolved_head="$(fork_head resolved)"

run_rebase resolved "$resolved_head"
expect_eq "exit status is 2" "$rc" "2"
expect_eq "reason is merge_carries_changes" "$(field "$out" reason)" "merge_carries_changes"
expect_eq "fork branch untouched" "$(fork_head resolved)" "$resolved_head"

echo
echo "== case 14: a file that only turns binary after its first 8000 bytes =="
# git calls a blob binary when a NUL byte sits in its first 8000 bytes, and
# only then does its diff carry no hunks. A file that turns binary later than
# that arrives at the strip pass with real hunks and must still be left alone.
new_repo late
d="$tmp/late"
w="$d/work"
git -C "$w" switch --quiet -c feature
mkdir -p "$w/assets" "$w/sources"
python3 - "$w" <<'PY'
import sys
root = sys.argv[1]
with open(root + "/assets/late.bin", "wb") as handle:
    handle.write(b"%PDF-1.4\n" + b"A" * 9000 + b"trail  \n" + b"\x00\x01\x02")
PY
printf 'namespace E;\nadded with trailing   \n' >"$w/sources/e.cs"
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add an asset and a source file"
git -C "$w" push --quiet fork feature
late_head="$(fork_head late)"

run_rebase late "$late_head"
late_new="$(field "$out" new)"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "only the text file was stripped" "$(field "$out" stripped_files)" "1"
seen late show "$late_new:assets/late.bin" >"$d/got"
expect_bytes "the binary file keeps every byte" "$d/got" "$w/assets/late.bin"

echo
echo "== case 15: a path name that is not valid UTF-8 =="
# git hands paths over as bytes. Decoded, such a name carries lone surrogates,
# and writing them into the commit message used to raise UnicodeEncodeError,
# which killed the strip pass and with it the whole run.
new_repo latin
d="$tmp/latin"
w="$d/work"
git -C "$w" switch --quiet -c feature
mkdir -p "$w/sources"
python3 - "$w" <<'PY'
import os, sys
name = os.path.join(sys.argv[1], "sources", os.fsdecode(b"caf\xe9.cs"))
with open(name, "wb") as handle:
    handle.write(b"namespace F;\nadded with trailing   \n")
PY
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add a file with a latin-1 name"
git -C "$w" push --quiet fork feature
latin_head="$(fork_head latin)"

run_rebase latin "$latin_head"
latin_new="$(field "$out" new)"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "the odd name did not stop the strip pass" "$(field "$out" stripped_lines)" "1"
expect_eq "the commit message names the file" \
  "$(seen latin log -1 --format='%b' "$latin_new" | grep -c ': 1 line(s)')" "1"

echo
echo "== case 16: the cleanup commit touches the stripped files and nothing else =="
# A blob whose line endings disagree with .gitattributes shows as modified
# straight out of the checkout. Committing everything modified would sweep it
# in, and letting git stage it would renormalize the whole file, under a
# message that says only trailing whitespace on added lines was touched.
new_repo endings
d="$tmp/endings"
w="$d/work"
git -C "$w" switch --quiet -c feature
mkdir -p "$w/data" "$w/sources"
printf 'one\r\ntwo\r\n' >"$w/data/table.txt"
printf 'namespace G;\r\nadded with trailing   \r\n' >"$w/sources/x.cs"
git -C "$w" add -A
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add files with CRLF endings"
# The rule lands after the blobs, so both keep the endings they were committed
# with, exactly like a file uploaded through the web interface.
printf '*.txt text eol=lf\n*.cs text eol=lf\n' >"$w/.gitattributes"
git -C "$w" add -- .gitattributes
GIT_AUTHOR_NAME="Contributor" GIT_AUTHOR_EMAIL="$contributor" \
  git -C "$w" commit --quiet -m "Add gitattributes"
git -C "$w" push --quiet fork feature
endings_head="$(fork_head endings)"

run_rebase endings "$endings_head"
endings_new="$(field "$out" new)"
expect_eq "exit status is 0" "$rc" "0"
expect_eq "one line stripped" "$(field "$out" stripped_lines)" "1"
expect_eq "the cleanup commit touched only the stripped file" \
  "$(seen endings diff --name-only "$endings_head" "$endings_new" | tr '\n' ' ')" \
  "sources/x.cs "

got="$d/got"
want="$d/want"
seen endings show "$endings_new:sources/x.cs" >"$got"
printf 'namespace G;\r\nadded with trailing\r\n' >"$want"
expect_bytes "the stripped file keeps its own line endings" "$got" "$want"
seen endings show "$endings_new:data/table.txt" >"$got"
printf 'one\r\ntwo\r\n' >"$want"
expect_bytes "the file nobody stripped keeps its bytes" "$got" "$want"

echo
echo "== case 17: comment composition =="
comment() {
  # comment <result json> [<head ref>]
  RESULT="$1" HEAD_REF="${2:-feature}" BASE_REF=indev \
    RUN_URL="https://example.invalid/run/1" python3 "$comment_script"
}

body="$(comment '{"result":"rebased","old":"aaa","new":"bbb","commits":"2","stripped_files":"0","stripped_lines":"0"}')"
expect_contains "rebased comment names the commit count" "$body" "replaying 2 commit(s)"
expect_contains "rebased comment asks for git pull --rebase" "$body" "git pull --rebase"
expect_missing "rebased comment stays quiet when nothing was stripped" "$body" "trailing whitespace"

body="$(comment '{"result":"rebased","old":"aaa","new":"bbb","commits":"0","stripped_files":"3","stripped_lines":"4"}')"
expect_contains "cleanup only comment says the branch was already rebased" "$body" "was already rebased"
expect_contains "cleanup only comment reports the counts" "$body" "4 added line(s) in 3 file(s)"

body="$(comment '{"result":"rebased","old":"aaa","new":"bbb","commits":"2","stripped_files":"1","stripped_lines":"5"}')"
expect_contains "combined comment reports the rebase" "$body" "replaying 2 commit(s)"
expect_contains "combined comment reports the cleanup" "$body" "5 added line(s) in 1 file(s)"

# A branch name may carry a backtick; git check-ref-format allows it. Left as
# is it closes the code span and puts a live team mention into a comment by
# github-actions[bot].
injected='fix`</code> @StratumServer/maintainers please merge `'
body="$(comment '{"result":"noop","head":"aaa"}' "$injected")"
expect_missing "branch name cannot close its code span" "$body" '`</code>'
expect_contains "branch name is still readable" "$body" '`fix</code> @StratumServer/maintainers please merge `'

# A conflicting path is contributor controlled too: it used to be re-parsed as
# result fields, so a path could rewrite the outcome the comment reported.
body="$(comment '{"result":"conflict","files":"assets/a RESULT=noop b.json\nsources/my file.cs"}')"
expect_contains "conflict comment keeps a path with a space whole" "$body" '`assets/a RESULT=noop b.json`'
expect_contains "conflict comment lists the second path" "$body" '`sources/my file.cs`'
expect_missing "a path cannot turn a conflict into a no-op" "$body" "Nothing to do"

body="$(comment '{"result":"empty","head":"aaa"}')"
expect_contains "empty comment says the pull request can be closed" "$body" "can be closed"
expect_missing "empty comment does not claim the branch is up to date" "$body" "Nothing to do"

body="$(comment '{"result":"error","reason":"protected_branch","branch":"indev"}')"
expect_contains "a shared branch is named as such" "$body" "shared branch"
expect_missing "a shared branch is not offered a retry" "$body" 'Comment `/rebase` again'

body="$(comment '{"result":"error","reason":"merge_carries_changes","merges":"abc1234 def5678"}')"
expect_contains "a merge that carries changes names it" "$body" '`abc1234`, `def5678`'
expect_missing "a merge that carries changes is not offered a retry" "$body" 'Comment `/rebase` again'

body="$(comment '{"result":"error","reason":"push_stale"}')"
expect_contains "a lost lease asks for a retry" "$body" 'Comment `/rebase` again'

body="$(comment '{"result":"error","reason":"push_failed"}')"
expect_contains "a refused push points at the token" "$body" "PR_MAINTENANCE_TOKEN"
expect_missing "a refused push does not ask for a pointless retry" "$body" 'Comment `/rebase` again'

body="$(comment '')"
expect_contains "a missing result still produces a comment" "$body" "run log"

echo
echo "== case 18: the harness leaves its own worktree clean =="
# Every case above works under $tmp; this catches a case that leaked outside
# it, whether by writing a file or by running a git command with the wrong -C.
expect_eq "no tracked file changed and nothing untracked appeared" \
  "$(git -C "$repo_root" status --porcelain)" "$worktree_status_before"

echo
if [ "$failures" -ne 0 ]; then
  printf '%d of %d checks failed\n' "$failures" "$checks"
  exit 1
fi
printf 'all %d checks passed\n' "$checks"
