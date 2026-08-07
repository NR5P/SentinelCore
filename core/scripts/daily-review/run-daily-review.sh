#!/usr/bin/env bash
# Runs the SentinelCore daily review headlessly via the Claude Code CLI.
#
# Invoked by cron at 6am (see install-cron.sh). Runs from core/ with a full shell, so
# unlike the in-app Cowork task it can actually build, test, commit, push, and deploy.
#
# Usage:
#   ./run-daily-review.sh              # normal run
#   DRY_RUN=1 ./run-daily-review.sh    # review and report, but change nothing
#
# Optional environment:
#   CLAUDE_MODEL   pin a specific model instead of the CLI default
#   AUTH_ENV_FILE  path to a mode-0600 file exporting CLAUDE_CODE_OAUTH_TOKEN or
#                  ANTHROPIC_API_KEY (defaults to ~/.config/sentinelcore-daily-review.env)
#
# Reports go to core/daily-reviews/ (gitignored). Logs go outside the repo entirely, under
# ~/.local/state/sentinelcore-daily-review/, so they can never dirty the working tree —
# a dirty tree makes the review refuse to commit anything.

set -euo pipefail

if [[ ${EUID} -eq 0 ]]; then
  echo "ERROR: run as your normal user, not root. The Claude CLI refuses to use" >&2
  echo "       --dangerously-skip-permissions as root, and cron would use root's crontab." >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
CORE_DIR=$(cd -- "$SCRIPT_DIR/../.." && pwd)
REPO_ROOT=$(cd -- "$CORE_DIR/.." && pwd)
PROMPT_FILE="$SCRIPT_DIR/review-prompt.md"
REPORT_DIR="$CORE_DIR/daily-reviews"
LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/sentinelcore-daily-review"
LOG_FILE="$LOG_DIR/$(date +%Y-%m-%d).log"

mkdir -p "$LOG_DIR" "$REPORT_DIR"

# cron gives us a near-empty PATH, so put the usual suspects back before resolving anything.
# $HOME/.dotnet matters: if .NET was installed with Microsoft's dotnet-install.sh rather
# than the snap, every dotnet build/test in deploy.sh fails without it.
export PATH="$HOME/.local/bin:$HOME/bin:$HOME/.dotnet:$HOME/.dotnet/tools:/usr/local/bin:/usr/bin:/bin:/snap/bin:$PATH"
[[ -d "$HOME/.dotnet" ]] && export DOTNET_ROOT="$HOME/.dotnet"

# If Claude was installed through nvm, the node bin dir isn't on PATH under cron.
# nvm.sh trips over unbound variables, and `|| true` does not catch a set -u expansion
# error inside a sourced file, so disable -u across the source.
if [[ -s "$HOME/.nvm/nvm.sh" ]]; then
  set +u
  # shellcheck disable=SC1091
  . "$HOME/.nvm/nvm.sh" >/dev/null 2>&1 || true
  set -u
fi

# Credentials. Under cron there is no D-Bus session, so a keyring-backed login won't open.
# A token in a 0600 env file is the reliable path.
AUTH_ENV_FILE="${AUTH_ENV_FILE:-$HOME/.config/sentinelcore-daily-review.env}"
if [[ -f "$AUTH_ENV_FILE" ]]; then
  set +u
  # shellcheck disable=SC1090
  . "$AUTH_ENV_FILE"
  set -u
fi

find_claude() {
  local candidate
  for candidate in \
    "$(command -v claude 2>/dev/null || true)" \
    "$HOME/.claude/local/claude" \
    "$HOME/.local/bin/claude" \
    "/usr/local/bin/claude"; do
    if [[ -n "$candidate" && -x "$candidate" ]]; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

if ! CLAUDE_BIN=$(find_claude); then
  echo "ERROR: could not find the 'claude' CLI." | tee -a "$LOG_FILE" >&2
  echo "Install it with:  npm install -g @anthropic-ai/claude-code" | tee -a "$LOG_FILE" >&2
  exit 127
fi

if [[ ! -f "$PROMPT_FILE" ]]; then
  echo "ERROR: prompt file missing at $PROMPT_FILE" | tee -a "$LOG_FILE" >&2
  exit 1
fi

PROMPT=$(cat "$PROMPT_FILE")

if [[ "${DRY_RUN:-0}" == "1" ]]; then
  PROMPT="$PROMPT

## DRY RUN OVERRIDE

This is a dry run. Do all the review work, but make no edits, no commits, no pushes, and
no deploys. Write the report as usual, and in the Shipped section list what you *would*
have changed instead of what you did."
fi

cd "$CORE_DIR"

{
  echo "================================================================"
  echo "SentinelCore daily review — $(date '+%Y-%m-%d %H:%M:%S %Z')"
  echo "claude:  $CLAUDE_BIN"
  echo "dotnet:  $(command -v dotnet 2>/dev/null || echo 'NOT FOUND — builds will fail')"
  echo "sqlite3: $(command -v sqlite3 2>/dev/null || echo 'NOT FOUND — db checks will be skipped')"
  echo "cwd:     $CORE_DIR"
  echo "dry run: ${DRY_RUN:-0}"
  echo "================================================================"
} >>"$LOG_FILE"

# --dangerously-skip-permissions is deliberate: this runs unattended with no TTY to approve
# prompts. It's acceptable here because this is Nathan's test instance, the scope is fenced
# in the prompt (core/ only, never ios/ or appletv/), and deploy.sh gates on build + tests.
#
# --add-dir gives read access to the repo root so the review can inspect the Swift clients
# for API compatibility without being able to build them.
# -k 60 escalates to SIGKILL if Claude ignores the SIGTERM at the one hour mark.
#
# The prompt goes in on stdin rather than as a trailing positional argument. --add-dir
# accepts more than one directory, so a positional after it gets swallowed as another
# directory and Claude exits with "Input must be provided either through stdin or as a
# prompt argument". Piping removes the ambiguity and has no argument-length limit.
CLAUDE_ARGS=(--print --dangerously-skip-permissions --add-dir "$REPO_ROOT")
[[ -n "${CLAUDE_MODEL:-}" ]] && CLAUDE_ARGS+=(--model "$CLAUDE_MODEL")

set +e
printf '%s' "$PROMPT" | timeout -k 60 3600 "$CLAUDE_BIN" "${CLAUDE_ARGS[@]}" \
  >>"$LOG_FILE" 2>&1
STATUS=$?
set -e

{
  echo
  if [[ $STATUS -eq 124 || $STATUS -eq 137 ]]; then
    echo "RESULT: timed out after 60 minutes"
  elif [[ $STATUS -ne 0 ]]; then
    echo "RESULT: claude exited $STATUS"
  else
    echo "RESULT: ok"
  fi
  echo "Finished $(date '+%Y-%m-%d %H:%M:%S %Z')"
  echo
} >>"$LOG_FILE"

# Keep 30 days of logs and nothing older.
find "$LOG_DIR" -name '*.log' -type f -mtime +30 -delete 2>/dev/null || true

exit $STATUS
