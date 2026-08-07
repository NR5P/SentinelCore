#!/usr/bin/env bash
# One-command setup for the automated 6am SentinelCore review.
#
# Installs whatever is missing, fixes the things that silently break unattended cron jobs,
# then installs the cron entry itself.
#
# Usage:
#   ./setup.sh              # interactive: asks before each install
#   ./setup.sh --yes        # assume yes to every install prompt
#   ./setup.sh --check      # report what's missing, change nothing
#
# Everything it does is idempotent — re-running is safe.

set -euo pipefail

# Without this, any unexpected non-zero exit kills the script silently and you're left
# guessing which of the seven steps died. Report it loudly instead.
trap 'status=$?; echo; echo "*** setup.sh aborted at line $LINENO (exit $status)"; echo "*** failing command: $BASH_COMMAND"; echo "*** Nothing further was installed. Paste this whole output back."; exit $status' ERR

if [[ ${EUID} -eq 0 ]]; then
  echo "ERROR: run as your normal user, not root. This script uses sudo where it needs to," >&2
  echo "       and running the whole thing as root would install the cron job into root's" >&2
  echo "       crontab and break the Claude CLI (it refuses unattended root execution)." >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
CORE_DIR=$(cd -- "$SCRIPT_DIR/../.." && pwd)
AUTH_ENV_FILE="$HOME/.config/sentinelcore-daily-review.env"

ASSUME_YES=0
CHECK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --yes|-y) ASSUME_YES=1 ;;
    --check|-n) CHECK_ONLY=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 1 ;;
  esac
done

# Files written by an editor or copied out of an archive often arrive without the exec bit.
# Restore it up front so nothing later dies on "Permission denied".
chmod +x "$SCRIPT_DIR"/*.sh 2>/dev/null || true

export PATH="$HOME/.local/bin:$HOME/bin:$HOME/.dotnet:$HOME/.dotnet/tools:/usr/local/bin:/usr/bin:/bin:/snap/bin:$PATH"
if [[ -s "$HOME/.nvm/nvm.sh" ]]; then
  set +u; . "$HOME/.nvm/nvm.sh" >/dev/null 2>&1 || true; set -u
fi

MISSING=0
FIXED=0
MANUAL=()

note()  { printf '    %s\n' "$*"; }
ok()    { printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()   { printf '  \033[31m✗\033[0m %s\n' "$*"; MISSING=$((MISSING + 1)); }
fixed() { printf '  \033[32m✓\033[0m %s\n' "$*"; FIXED=$((FIXED + 1)); }

# Ask before doing something. Returns 1 in --check mode or on a "no".
confirm() {
  local prompt="$1"
  if [[ $CHECK_ONLY -eq 1 ]]; then
    note "would: $prompt"
    return 1
  fi
  if [[ $ASSUME_YES -eq 1 ]]; then
    return 0
  fi
  if [[ ! -t 0 ]]; then
    note "not a terminal, skipping: $prompt"
    return 1
  fi
  local reply
  read -r -p "      $prompt [Y/n] " reply
  [[ -z "$reply" || "$reply" =~ ^[Yy] ]]
}

echo
echo "SentinelCore daily review — setup"
echo "================================="
echo

# ---------------------------------------------------------------- node + claude CLI

echo "[1/7] Claude Code CLI"
if command -v claude >/dev/null 2>&1; then
  ok "claude: $(command -v claude)"
else
  bad "claude CLI not installed — this is the thing that actually runs the review"
  if ! command -v npm >/dev/null 2>&1; then
    note "npm is missing too (needed to install it)"
    if confirm "Install Node.js and npm via apt?"; then
      sudo apt-get update -qq && sudo apt-get install -y nodejs npm
      fixed "installed nodejs + npm"
    else
      MANUAL+=("Install Node.js, then: npm install -g @anthropic-ai/claude-code")
    fi
  fi
  if command -v npm >/dev/null 2>&1; then
    if confirm "Install the Claude Code CLI globally via npm?"; then
      # A global npm install to /usr/lib needs root; a user-prefix install does not.
      if npm install -g @anthropic-ai/claude-code 2>/dev/null; then
        fixed "installed @anthropic-ai/claude-code"
      elif sudo npm install -g @anthropic-ai/claude-code; then
        fixed "installed @anthropic-ai/claude-code (with sudo)"
      else
        MANUAL+=("npm install -g @anthropic-ai/claude-code  # failed, run by hand")
      fi
    else
      MANUAL+=("npm install -g @anthropic-ai/claude-code")
    fi
  fi
fi
echo

# ---------------------------------------------------------------- dotnet

echo "[2/7] .NET SDK"
if command -v dotnet >/dev/null 2>&1; then
  ok "dotnet: $(command -v dotnet) ($(dotnet --version 2>/dev/null || echo '?'))"
else
  bad "dotnet not found on the cron PATH — builds, tests and deploys will fail"
  if confirm "Install .NET via snap (the method core/README.md recommends)?"; then
    sudo snap install dotnet --classic && sudo snap alias dotnet.dotnet dotnet 2>/dev/null || true
    fixed "installed .NET via snap"
  else
    MANUAL+=("sudo snap install dotnet --classic   # see core/docs/DEPLOYMENT.md")
  fi
fi
echo

# ---------------------------------------------------------------- sqlite3

echo "[3/7] sqlite3"
if command -v sqlite3 >/dev/null 2>&1; then
  ok "sqlite3: $(command -v sqlite3)"
else
  bad "sqlite3 missing — the review can't inspect events.db and the other databases"
  if confirm "Install sqlite3 via apt?"; then
    sudo apt-get update -qq && sudo apt-get install -y sqlite3
    fixed "installed sqlite3"
  else
    MANUAL+=("sudo apt install sqlite3")
  fi
fi
echo

# ---------------------------------------------------------------- headless auth

echo "[4/7] Headless authentication"
if [[ -f "$AUTH_ENV_FILE" ]]; then
  ok "token file present: $AUTH_ENV_FILE"
  PERMS=$(stat -c '%a' "$AUTH_ENV_FILE" 2>/dev/null || echo '?')
  if [[ "$PERMS" != "600" ]]; then
    chmod 600 "$AUTH_ENV_FILE" && fixed "tightened permissions on the token file to 600"
  fi
elif command -v claude >/dev/null 2>&1; then
  # Test the way cron will see it: no desktop session, no inherited environment.
  #
  # Note the `|| STATUS=$?` form rather than `set +e`. An ERR trap fires even when errexit
  # is off, so a bare `set +e` block does NOT protect an expected failure from the trap.
  # Being part of an || list is what actually exempts a command from both.
  CLAUDE_BIN_PATH=$(command -v claude)
  AUTH_STATUS=0
  AUTH_OUT=$(env -i HOME="$HOME" PATH="$PATH" timeout 120 "$CLAUDE_BIN_PATH" \
    --print --model haiku "Reply with exactly: ok" </dev/null 2>&1) || AUTH_STATUS=$?

  # A failure here is ambiguous: it can mean auth is broken, or just that --model haiku
  # was rejected. Retry without the flag so we don't send you chasing an auth problem
  # that isn't one.
  if [[ $AUTH_STATUS -ne 0 ]]; then
    RETRY_STATUS=0
    RETRY_OUT=$(env -i HOME="$HOME" PATH="$PATH" timeout 120 "$CLAUDE_BIN_PATH" \
      --print "Reply with exactly: ok" </dev/null 2>&1) || RETRY_STATUS=$?
    if [[ $RETRY_STATUS -eq 0 ]]; then
      AUTH_STATUS=0
      AUTH_OUT="$RETRY_OUT"
      note "(the --model haiku probe failed but the default model works; ignoring)"
    else
      AUTH_OUT="$AUTH_OUT"$'\n---- retry without --model ----\n'"$RETRY_OUT"
    fi
  fi

  if [[ $AUTH_STATUS -eq 0 && "$AUTH_OUT" == *"ok"* ]]; then
    ok "auth works headlessly (no token file needed)"
  else
    bad "the headless probe failed — full output below"
    printf '%s\n' "$AUTH_OUT" | sed 's/^/      | /'

    # Prefer a normal interactive login over `claude setup-token`.
    #
    # setup-token prints a year-long credential to the terminal in plaintext, where it ends
    # up in scrollback, in any `tee` log, and in anything you paste elsewhere. A normal
    # login writes ~/.claude/.credentials.json instead and never displays a secret. On Linux
    # that file is plain JSON on disk, not a keyring entry, so cron can read it at 6am with
    # no desktop session — which is exactly what we need.
    if [[ "$AUTH_OUT" == *"Not logged in"* || "$AUTH_OUT" == *"/login"* ]]; then
      note "you're simply not logged in to the CLI yet — no token juggling needed"
      note "run this in a terminal, type /login, finish in the browser, then re-run setup.sh:"
      note "    claude"
      MANUAL+=("Run 'claude', use /login to sign in, then re-run: bash $SCRIPT_DIR/setup.sh")
    else
      note "logged in, but the credential isn't readable without a desktop session."
      note "In that case a long-lived token is the fallback. Be aware it is printed in"
      note "plaintext, so run it somewhere you're not screen-sharing or logging output:"
      note "    claude setup-token"
      note "then write it straight to the file without echoing it anywhere else:"
      note "    install -m 600 /dev/null $AUTH_ENV_FILE"
      note "    printf 'export CLAUDE_CODE_OAUTH_TOKEN=%%s\\n' 'PASTE_TOKEN' > $AUTH_ENV_FILE"
      MANUAL+=("claude setup-token, then write it to $AUTH_ENV_FILE (mode 600). Never paste it into a chat or a log.")
    fi
  fi
else
  note "skipped — the claude CLI isn't installed yet"
fi
echo

# ---------------------------------------------------------------- git push

echo "[5/7] Non-interactive git push"
cd "$CORE_DIR"
PUSH_STATUS=0
PUSH_OUT=$(env -i HOME="$HOME" PATH="$PATH" GIT_TERMINAL_PROMPT=0 \
  git -C "$CORE_DIR" push --dry-run 2>&1) || PUSH_STATUS=$?
if [[ $PUSH_STATUS -eq 0 ]]; then
  ok "git can push without a terminal"
else
  bad "git push fails without a terminal — the review will report but never ship"
  printf '%s\n' "$PUSH_OUT" | sed 's/^/      | /'
  note "remote is HTTPS (github.com/NR5P/BlueIrisAiCameraAlert)"
  if confirm "Set credential.helper=store so a saved token is used non-interactively?"; then
    git config --global credential.helper store
    fixed "set credential.helper=store"
    note "Now push once by hand to save the credential:  git -C $CORE_DIR push"
    note "Use a GitHub personal access token as the password, not your account password."
    MANUAL+=("git -C $CORE_DIR push   # once, by hand, to cache the credential")
  else
    MANUAL+=("Make 'git push' work without a prompt (credential.helper store, or an SSH key with no passphrase)")
  fi
fi

if [[ "$(git config --get commit.gpgsign 2>/dev/null || echo false)" == "true" ]]; then
  bad "commit.gpgsign is on — commits will fail under cron (no pinentry TTY)"
  if confirm "Disable commit signing for this repo only?"; then
    git config commit.gpgsign false
    fixed "disabled commit.gpgsign for this repo"
  else
    MANUAL+=("git -C $CORE_DIR config commit.gpgsign false")
  fi
else
  ok "commit signing off (won't block unattended commits)"
fi
echo

# ---------------------------------------------------------------- sudo for deploys

echo "[6/7] Passwordless service restarts (for deploys)"
SUDOERS_FILE=/etc/sudoers.d/sentinelcore
SYSTEMCTL=$(command -v systemctl || echo /usr/bin/systemctl)
if sudo -n -l "$SYSTEMCTL" restart sentinelcore >/dev/null 2>&1; then
  ok "passwordless restart already allowed"
else
  bad "deploys will fail under cron — restarting services needs a password"
  if confirm "Write $SUDOERS_FILE allowing only these three restarts?"; then
    TMP=$(mktemp)
    cat >"$TMP" <<EOF
# Lets the automated daily review restart SentinelCore after a verified build.
# Scoped to exactly these three commands - not general sudo.
$USER ALL=(ALL) NOPASSWD: $SYSTEMCTL restart sentinelcore
$USER ALL=(ALL) NOPASSWD: $SYSTEMCTL restart sentinelcore-web
$USER ALL=(ALL) NOPASSWD: $SYSTEMCTL restart sentinelcore-homekit
EOF
    # Never install a sudoers file without validating it - a syntax error here can lock
    # you out of sudo entirely.
    if sudo visudo -cf "$TMP" >/dev/null 2>&1; then
      sudo install -m 0440 -o root -g root "$TMP" "$SUDOERS_FILE"
      rm -f "$TMP"
      fixed "installed $SUDOERS_FILE"
    else
      rm -f "$TMP"
      bad "generated sudoers file failed validation — not installing it"
      MANUAL+=("sudo visudo -f $SUDOERS_FILE   # add the three NOPASSWD restart lines by hand")
    fi
  else
    MANUAL+=("sudo visudo -f $SUDOERS_FILE   # add NOPASSWD lines for systemctl restart sentinelcore{,-web,-homekit}")
  fi
fi
echo

# ---------------------------------------------------------------- journal access

echo "[7/7] Journal read access"
if id -nG | tr ' ' '\n' | grep -qxE 'adm|systemd-journal'; then
  ok "you can read service logs"
else
  bad "not in 'adm' — journalctl returns empty instead of failing, so the review would"
  note "wrongly conclude there were no warnings all night"
  if confirm "Add $USER to the 'adm' group?"; then
    sudo usermod -aG adm "$USER"
    fixed "added $USER to adm — takes effect after you log out and back in"
  else
    MANUAL+=("sudo usermod -aG adm $USER   # then log out and back in")
  fi
fi
echo

# ---------------------------------------------------------------- cron

echo "================================="
if [[ $CHECK_ONLY -eq 1 ]]; then
  echo "Check only — nothing was changed. $MISSING issue(s) found."
  exit 0
fi

echo "Installing the cron job"
echo
bash "$SCRIPT_DIR/install-cron.sh"

echo
echo "================================="
if [[ ${#MANUAL[@]} -gt 0 ]]; then
  echo
  echo "Still needs you to do by hand:"
  for item in "${MANUAL[@]}"; do
    echo "  - $item"
  done
fi
echo
echo "Next: run a full review right now, changing nothing, to see if it actually works:"
echo "    DRY_RUN=1 $SCRIPT_DIR/run-daily-review.sh"
echo "    tail -f ${XDG_STATE_HOME:-$HOME/.local/state}/sentinelcore-daily-review/\$(date +%F).log"
