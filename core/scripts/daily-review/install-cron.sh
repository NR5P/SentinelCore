#!/usr/bin/env bash
# Installs (or updates) the 6am daily SentinelCore review cron job for the current user.
#
# Usage:
#   ./install-cron.sh              # install at 6:00am daily
#   ./install-cron.sh "30 5 * * *" # install at a custom schedule
#   ./install-cron.sh --remove     # uninstall
#
# Safe to re-run: it replaces its own entry rather than stacking duplicates.
#
# The preflight checks below deliberately run things in a stripped environment, because the
# whole class of bug this job is prone to is "works in your terminal, fails silently at 6am".

set -euo pipefail

if [[ ${EUID} -eq 0 ]]; then
  echo "ERROR: run as your normal user, not root — sudo would install this into root's" >&2
  echo "       crontab, and the Claude CLI refuses to run unattended as root." >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
RUNNER="$SCRIPT_DIR/run-daily-review.sh"
CORE_DIR=$(cd -- "$SCRIPT_DIR/../.." && pwd)
LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/sentinelcore-daily-review"
MARKER="# sentinelcore-daily-review"
SCHEDULE="${1:-0 6 * * *}"

WARNINGS=0
# warn() counts a distinct problem. advise() is continuation detail for the problem above
# it and must not inflate the count — otherwise a single issue with five lines of
# remediation advice reports as six warnings.
warn()   { echo "    $*"; WARNINGS=$((WARNINGS + 1)); }
advise() { echo "    $*"; }

# Read the existing crontab, distinguishing "no crontab yet" from a real failure. Piping
# a failed `crontab -l` straight into `crontab -` would silently wipe every other job.
read_crontab() {
  local out status
  set +e
  out=$(crontab -l 2>&1)
  status=$?
  set -e
  if [[ $status -ne 0 ]]; then
    if [[ "$out" == *"no crontab for"* ]]; then
      echo ""
      return 0
    fi
    echo "ERROR: could not read the current crontab: $out" >&2
    return 1
  fi
  echo "$out"
}

# Our entry is a single line with the marker as a trailing comment, so dropping every line
# containing the marker removes exactly it and nothing else.
strip_existing() {
  read_crontab | grep -v -F "$MARKER" || true
}

if [[ "${1:-}" == "--remove" ]]; then
  strip_existing | crontab -
  echo "Removed the daily review cron job."
  exit 0
fi

chmod +x "$RUNNER" "$SCRIPT_DIR/install-cron.sh"
mkdir -p "$LOG_DIR"

echo "==> Preflight"

if ! command -v crontab >/dev/null 2>&1; then
  echo "ERROR: crontab not found. Install cron:  sudo apt install cron" >&2
  exit 1
fi

# Resolve tools the same way the runner does, so failures surface now rather than at 6am.
export PATH="$HOME/.local/bin:$HOME/bin:$HOME/.dotnet:$HOME/.dotnet/tools:/usr/local/bin:/usr/bin:/bin:/snap/bin:$PATH"
if [[ -s "$HOME/.nvm/nvm.sh" ]]; then
  set +u; . "$HOME/.nvm/nvm.sh" >/dev/null 2>&1 || true; set -u
fi

if command -v claude >/dev/null 2>&1; then
  echo "    claude:   $(command -v claude)"
else
  echo "    claude:   NOT FOUND"
  echo "              Install it first:  npm install -g @anthropic-ai/claude-code"
  exit 127
fi

if command -v dotnet >/dev/null 2>&1; then
  echo "    dotnet:   $(command -v dotnet) ($(dotnet --version 2>/dev/null || echo 'version unknown'))"
else
  warn   "dotnet:   NOT FOUND on the cron PATH. Builds, tests and deploys will fail."
fi

if command -v sqlite3 >/dev/null 2>&1; then
  echo "    sqlite3:  $(command -v sqlite3)"
else
  warn   "sqlite3:  NOT FOUND. The event database checks will be skipped."
  advise "          Install with:  sudo apt install sqlite3"
fi

# Authentication. A keyring-backed login works interactively and dies under cron, so test
# it the way cron will see it: no session bus, no inherited environment.
AUTH_ENV_FILE="${AUTH_ENV_FILE:-$HOME/.config/sentinelcore-daily-review.env}"
echo "    auth:     testing a real headless call (this takes a few seconds)..."
set +e
AUTH_OUT=$(env -i HOME="$HOME" PATH="$PATH" AUTH_ENV_FILE="$AUTH_ENV_FILE" \
  timeout 120 "$(command -v claude)" --print --model haiku \
  "Reply with exactly: ok" </dev/null 2>&1)
AUTH_STATUS=$?
set -e
if [[ $AUTH_STATUS -eq 0 && "$AUTH_OUT" == *"ok"* ]]; then
  echo "    auth:     ok (works without a desktop session)"
else
  warn   "auth:     FAILED headlessly — this is the most common cause of a silent 6am failure."
  advise "          ${AUTH_OUT:0:200}"
  advise "          Fix: run  claude setup-token  then save the token to"
  advise "               $AUTH_ENV_FILE  as:"
  advise "                 export CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat...."
  advise "               and  chmod 600 $AUTH_ENV_FILE"
fi

cd "$CORE_DIR"

# git must push without a terminal. An unlocked keyring or ssh-agent in your interactive
# shell will make a naive check pass and cron still fail, so strip the environment.
if git rev-parse --git-dir >/dev/null 2>&1; then
  set +e
  PUSH_OUT=$(env -i HOME="$HOME" PATH="$PATH" GIT_TERMINAL_PROMPT=0 \
    git -C "$CORE_DIR" push --dry-run 2>&1)
  PUSH_STATUS=$?
  set -e
  if [[ $PUSH_STATUS -eq 0 ]]; then
    echo "    git push: ok (non-interactive)"
  else
    warn   "git push: FAILED without a terminal. The review will run and report, but can't push."
    advise "          ${PUSH_OUT:0:200}"
    advise "          For an HTTPS remote:  git config --global credential.helper store"
    advise "          then push once by hand to save the credential."
    advise "          For SSH: use a key with no passphrase."
  fi

  if [[ "$(git config --get commit.gpgsign 2>/dev/null || echo false)" == "true" ]]; then
    warn   "gpg:      commit.gpgsign is on. Commits will fail under cron (no pinentry TTY)."
    advise "          Disable for this repo:  git config commit.gpgsign false"
  fi
else
  warn   "git:      not a git repo? skipping push check"
fi

# deploy.sh restarts services with sudo, and cron gets no terminal to type a password at.
# Grep the allowlist for a NOPASSWD line rather than asking `sudo -n -l <command>`: that form
# reports authorization, not passwordlessness, and this box's blanket `(ALL : ALL) ALL` makes
# it exit 0 for everything. `-k` does not rescue it - the blanket rule grants authorization
# with or without a cached timestamp. See setup.sh for the same check, and deploy.sh's
# preflight, which is where this wording comes from.
if sudo -n -l 2>/dev/null | grep -qE "NOPASSWD:.*systemctl restart sentinelcore( |\$)"; then
  echo "    sudo:     passwordless restart allowed"
else
  warn   "sudo:     restarting services needs a password, which cron can't supply."
  advise "          Deploys will fail; builds, tests, commits and pushes still work."
  advise "          Fix:  sudo visudo -f /etc/sudoers.d/sentinelcore"
  advise "          and paste:"
  advise "            $USER ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart sentinelcore"
  advise "            $USER ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart sentinelcore-web"
  advise "            $USER ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart sentinelcore-homekit"
fi

# Reading system unit logs requires being in adm or systemd-journal, otherwise journalctl
# succeeds and returns nothing — the review would wrongly conclude there were no warnings.
if id -nG | tr ' ' '\n' | grep -qxE 'adm|systemd-journal'; then
  echo "    journal:  readable"
else
  warn   "journal:  you're not in 'adm' or 'systemd-journal', so journalctl -u sentinelcore"
  advise "          returns empty instead of failing. The review would see no warnings."
  advise "          Fix:  sudo usermod -aG adm $USER   (then log out and back in)"
fi

echo
echo "==> Installing cron entry"

# Invoke through bash explicitly rather than relying on the executable bit. If the repo is
# ever re-cloned, copied without permissions, or restored from an archive, the exec bit can
# be lost and cron fails with a bare "Permission denied" into a log nobody reads.
CRON_LINE="$SCHEDULE /bin/bash \"$RUNNER\" >>\"$LOG_DIR/cron.log\" 2>&1 $MARKER"

# Build the new crontab in a variable first; if reading the old one failed, abort rather
# than replacing it with a single-line file.
NEW_CRONTAB=$(strip_existing)
printf '%s\n%s\n' "$NEW_CRONTAB" "$CRON_LINE" | grep -v '^$' | crontab -

echo "    schedule: $SCHEDULE"
echo "    command:  $RUNNER"
echo
echo "Installed. Current crontab:"
crontab -l | sed 's/^/    /'
echo
if [[ $WARNINGS -gt 0 ]]; then
  echo "$WARNINGS warning(s) above. The job is installed and will run, but fix those or"
  echo "parts of it will quietly do nothing."
  echo
fi
echo "Test it right now without changing anything:"
echo "    DRY_RUN=1 $RUNNER"
echo
echo "Reports:   $CORE_DIR/daily-reviews/"
echo "Run logs:  $LOG_DIR/"
echo "Cron log:  $LOG_DIR/cron.log   (catches failures before the script can log)"
