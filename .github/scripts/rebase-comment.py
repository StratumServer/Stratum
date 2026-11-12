#!/usr/bin/env python3
"""Compose the comment the /rebase command posts on a pull request.

Reads the JSON result line rebase-pr.sh printed and writes markdown. It lives
in its own file rather than inline in the workflow for two reasons: the values
it interpolates are contributor controlled and need one place that escapes
them, and it is then testable offline by test-rebase-pr.sh.

Environment:
    RESULT        the script's JSON result line, empty when it never ran
    BASE_REF      base branch name
    HEAD_REF      pull request branch name
    RUN_URL       link to this workflow run
    COMMENT_FILE  file to write the comment body into
"""

import json
import os
import sys

PULL_ADVICE = (
    "Your local branch is now behind the remote. "
    "Run `git pull --rebase` before pushing again."
)


def code(value):
    """Wrap a value in a code span it cannot escape from.

    A branch name may legally contain a backtick: git check-ref-format only
    rejects control characters and ``* ? [ \\ ~ ^ :``. A name carrying one
    would close the span and let a contributor put live @mentions or links
    into a comment written by github-actions[bot], so backticks and line
    breaks are dropped.
    """
    text = "".join(c for c in str(value) if c not in "`\r\n")
    return "`%s`" % (text or "(unknown)")


def whitespace_sentence(data):
    lines = int(data.get("stripped_lines") or 0)
    files = int(data.get("stripped_files") or 0)
    if not lines:
        return ""
    return (
        "Also stripped trailing whitespace from %d added line(s) in %d file(s), "
        "as a separate commit. Lines this pull request did not add were left "
        "alone, and so were markdown files and the context lines of any "
        "`.patch` file." % (lines, files)
    )


def rebased(data, head, base):
    replayed = int(data.get("commits") or 0)
    if replayed:
        parts = [
            "Rebased %s onto %s, replaying %d commit(s) and dropping any merge "
            "commits." % (head, base, replayed)
        ]
    else:
        parts = ["%s was already rebased on %s." % (head, base)]
    sentence = whitespace_sentence(data)
    if sentence:
        parts.append(sentence)
    parts.append("%s -> %s" % (code(data.get("old")), code(data.get("new"))))
    parts.append(PULL_ADVICE)
    return parts


def conflict(data, head, base):
    paths = [p for p in str(data.get("files", "")).split("\n") if p.strip()]
    listing = "\n".join("- %s" % code(p) for p in paths) or "- `(unknown)`"
    return [
        "Rebasing %s onto %s hit conflicts in:" % (head, base),
        listing,
        "Nothing was pushed. Rebase locally, resolve the conflicts, then push "
        "with `--force-with-lease`.",
    ]


def failure(data, head, base, run_url):
    merges = ", ".join(code(m) for m in str(data.get("merges", "")).split())
    reasons = {
        "no_token": (
            "This pull request comes from a fork, so CI needs the "
            "`PR_MAINTENANCE_TOKEN` repository secret (a classic personal "
            "access token with the `repo` scope, from a maintainer account) to "
            "push to it. It is not configured."
        ),
        "no_maintainer_edits": (
            'This pull request has "Allow edits by maintainers" turned off, so '
            "CI cannot push to %s. The author can enable it in the pull request "
            "sidebar." % head
        ),
        "protected_branch": (
            "%s is a shared branch of this repository, not a pull request "
            "branch, so `/rebase` refuses to rewrite it. The release pull "
            "request is merged, never rebased." % head
        ),
        "merge_carries_changes": (
            "%s carries a merge commit that is not just the two sides joined: "
            "%s. `/rebase` replays commits and drops merges, so whatever was "
            "written into that merge, a conflict resolution or a fix made "
            "while merging, would be silently thrown away. Nothing was done. "
            "Rebase locally instead, redoing that work on the way."
            % (head, merges or code(""))
        ),
        "head_moved": (
            "%s moved while this run was starting, so nothing was pushed. "
            "Comment `/rebase` again." % head
        ),
        "push_stale": (
            "%s moved while this run was rebasing, so the push was refused and "
            "nothing was changed. Comment `/rebase` again." % head
        ),
        "push_failed": (
            "The rebase succeeded but the push was refused. The branch did not "
            "move, so this is a credential or permission problem: an expired "
            "`PR_MAINTENANCE_TOKEN`, a token without the `repo` scope or from "
            "an account that lost write access, or a fork that was renamed or "
            "deleted. Nothing was changed. The [run log](%s) has git's own "
            "message." % run_url
        ),
    }
    default = "The rebase did not complete. See the [run log](%s)." % run_url
    return [reasons.get(data.get("reason", ""), default)]


def compose(data, head_ref, base_ref, run_url):
    head = code(head_ref)
    base = code(base_ref)
    kind = data.get("result", "error")
    if kind == "rebased":
        parts = rebased(data, head, base)
    elif kind == "noop":
        parts = [
            "%s is already rebased on %s with a linear history and has no "
            "trailing whitespace on the lines it adds. Nothing to do."
            % (head, base)
        ]
    elif kind == "empty":
        parts = [
            "Rebasing %s onto %s leaves no commits: everything in this pull "
            "request is already in %s. Nothing was pushed, and the pull request "
            "can be closed." % (head, base, base)
        ]
    elif kind == "conflict":
        parts = conflict(data, head, base)
    else:
        parts = failure(data, head, base, run_url)
    return "\n\n".join(parts) + "\n"


def main():
    raw = os.environ.get("RESULT") or ""
    try:
        data = json.loads(raw) if raw.strip() else {}
    except ValueError:
        data = {}
    if not isinstance(data, dict):
        data = {}

    body = compose(
        data,
        os.environ.get("HEAD_REF", ""),
        os.environ.get("BASE_REF", ""),
        os.environ.get("RUN_URL", ""),
    )

    target = os.environ.get("COMMENT_FILE")
    if target:
        with open(target, "w", encoding="utf-8") as handle:
            handle.write(body)
    else:
        sys.stdout.write(body)


if __name__ == "__main__":
    main()
