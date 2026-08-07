# SentinelCore daily review

You are running unattended at 6am on the ThinkCentre that hosts SentinelCore. You have a
full shell and your working directory is the `core/` directory of the repo. Nathan has
explicitly said this is a **test instance, not his primary security system**, so you are
authorized to fix, commit, push, and deploy on your own judgment. Bias toward action. Do
not sit on an obvious fix waiting for permission.

## The project

.NET 10 ASP.NET Core. Blue Iris (or any compatible alert source) POSTs camera alert images
to SentinelCore on port 5080. Images are queued, sent to a local Ollama vision model
(`gemma3:4b` at localhost:11434) with configurable prompt profiles, and stored as structured
JSON. `SentinelCore.Web` (port 3320) adds a dashboard, live view, PTZ, a HomeKit sidecar
(port 3321), trigger zones, and accounts.

- `SentinelCore/` — main ingest + inference service
- `SentinelCore.Web/` — dashboard
- `SentinelCore.Tests/`, `BlueIrisOllamaBridge.Tests/` — xunit
- `SentinelCore/data/` — `events.db` (SQLite), `results/*.json`, saved media
- `scripts/deploy.sh` — build + test + restart + health check, the correct way to deploy
- `docs/` — DEPLOYMENT.md, USER_GUIDE.md, PUSH_NOTIFICATIONS.md, and handoff docs
- systemd units: `sentinelcore`, `sentinelcore-web`, `sentinelcore-homekit` (optional)

Outside `core/`, one level up in the repo root, there are also Apple clients:

- `ios/SentinelCore.xcodeproj` — SwiftUI iPhone app
- `appletv/SentinelCoreTV.xcodeproj` — tvOS app

## Scope — what you may and may not change

**You may edit, build, test, commit, push, and deploy:** everything under `core/` — the
server (`SentinelCore`), the web dashboard (`SentinelCore.Web`), the test projects, the
scripts, and the docs. These build and run on this machine, so you can actually verify them.

**You may NOT edit the Swift code under `ios/` or `appletv/`.** This is a Linux box with no
Xcode and no iOS/tvOS simulator — you cannot compile or run those apps, so any change you
made there would be unverified and untestable. Read them freely to understand how clients
call the API, and report bugs you spot, but never modify them. Put those findings in the
"Needs your input" section of the report so Nathan can handle them on a Mac.

One important consequence: if you change a server API route, response shape, or field name,
check whether `ios/SentinelCore/Networking/SentinelClient.swift`, `appletv/SentinelCoreTV/
Networking/SentinelClient.swift`, or the `Models/` files depend on it. If a server change
would break an Apple client you cannot update, do not ship it — report it as a coordinated
change Nathan needs to make on both sides.

Also ignore `.claude/worktrees/` entirely. Those are separate git worktrees for in-progress
branches, not the main tree. Only ever work in the main checkout.

## 1. Review recent code changes

```bash
git log --since="36 hours ago" --stat
git log --since="36 hours ago" -p
git status
```

Review those diffs for real defects, in priority order: unhandled exceptions on the alert
ingest path, race conditions in the queue, resource leaks (undisposed `HttpClient`,
`FileStream`, SQLite connections), sync-over-async and blocking calls in async paths,
missing `CancellationToken` plumbing, swallowed exceptions, hardcoded secrets or API keys,
and untested new logic. Skip style nitpicks.

## 2. Review the saved data

Inspect what the system has actually been producing over the last day.

```bash
sqlite3 SentinelCore/data/events.db ".tables"
sqlite3 SentinelCore/data/events.db ".schema"
du -sh SentinelCore/data/*
ls -lt SentinelCore/data/results | head -50
```

`SentinelCore/data/` holds several databases, not just `events.db` — also `devices.db`,
`schedules.db`, `push-devices.db`, `automation-rules.db`, `package-watches.db`, and
`timeline-reviews.db`. Give the others a look too, particularly for rows that reference
devices or media that no longer exist.

If `sqlite3` is not installed, say so in the report and fall back to inspecting the JSON
result files — do not skip section 2 silently.

Look for:

- **Alert quality** — false positives, duplicate triggers on one physical event, empty or
  malformed model responses, results where the model hedged or returned nothing useful,
  low-confidence detections treated as positives, prompt profiles producing vague answers
- **Storage** — total size and growth rate, orphaned media with no matching result row,
  results with no media, anything that should be pruned or compressed, missing retention policy
- **Errors and gaps** — truncated or zero-byte files, gaps in the event timeline suggesting
  camera dropouts or service restarts, items stuck in the queue, failed webhook posts

## 3. Review how it is actually running

```bash
systemctl --no-pager status sentinelcore sentinelcore-web
journalctl -u sentinelcore --since "24 hours ago" -p warning --no-pager
journalctl -u sentinelcore-web --since "24 hours ago" -p warning --no-pager
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5080/health/ready
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:3320/api/health
ollama list
```

Check for restart loops, memory growth, inference latency trends, queue backlog, config
drift between the `appsettings.json` files, and assumptions about the Ollama model that no
longer hold.

## 4. Update documentation that fell behind

Compare the current code against `README.md` and everything in `docs/`. Fix:

- new/renamed config keys in `appsettings.json` that aren't documented
- changed API routes or request/response shapes
- new or removed environment variables
- changed ports, paths, service names, or install steps that no longer match `scripts/`
- new dashboard features missing from `USER_GUIDE.md`
- handoff docs describing work that has since shipped or been abandoned — mark them done
  or stale rather than leaving them looking pending

Correct what is actually wrong or missing. Do not restructure or rewrite docs wholesale.

## What to do with what you find

**Fix it yourself, verify, then ship it.** For each issue you're confident about:

1. Make the edit.
2. `dotnet build -c Release` — if it fails, revert that change and report it instead.
3. `dotnet test` — if it fails, revert that change and report it instead.
4. Commit with a message explaining the why, not just the what.
5. `git push`
6. Deploy with `./scripts/deploy.sh` (builds, tests, restarts, health-checks).
   For documentation-only commits, skip the deploy — pushing is enough.

Group related fixes into one commit rather than one per line changed. Prefix commit messages
with `daily-review:` so they're easy to spot in the log.

**Leave alone and escalate instead** — anything where you'd be guessing at Nathan's intent:

- anything in `ios/` or `appletv/` — you cannot compile or test Swift here
- server changes that would break an Apple client you can't update in the same commit
- detection threshold and prompt-profile tuning (judgment calls about his cameras)
- database schema migrations
- deleting saved media or event history
- architectural changes, new dependencies, changes to the public API shape
- anything touching credentials, the API key, or network exposure
- any fix where the build or tests failed

A dirty working tree does NOT block you from working. Apply the rule per file, not globally:

- If a file you want to change already has uncommitted modifications, leave that file alone
  and report instead — you'd be committing someone else's half-finished work along with
  your fix.
- If a file you want to change is clean, go ahead, even if other files in the tree are dirty.

Expect these to be dirty more or less permanently, and never treat them as a reason to stop:
`SentinelCore/appsettings.json` and `SentinelCore.Web/appsettings.json` hold live local
config that is deliberately not committed, and `daily-reviews/` is gitignored output of
your own. A blanket "tree is dirty, so do nothing" rule would mean shipping nothing, ever,
on this machine.

Stage your own changes explicitly with `git add <path>`. Never use `git add -A` or
`git commit -a`, which would sweep up unrelated work sitting in the tree.

If you are running short on time or get interrupted, leave the repository in a clean,
consistent state: either finish the commit or revert the partial edit. Never leave a
half-applied change or a service that was stopped and not restarted.

## Output

Write a report to `daily-reviews/YYYY-MM-DD.md` (relative to `core/`, create the directory
if needed) with three sections:

1. **Shipped** — what you changed, tested, committed, pushed, and deployed. Include commit
   SHAs and the deploy result.
2. **Needs your input** — issues you deliberately left alone, each with options and your
   recommendation.
3. **Observations** — data and runtime trends worth knowing that aren't bugs. Storage growth,
   alert volume, latency, anything drifting.

Read yesterday's report first if it exists. Don't restate still-open items at full length —
one line and a reference is enough. Flag anything open more than 3 days.

If nothing meaningful changed and nothing is wrong, write two lines saying so. Do not
manufacture findings to fill the report.

Finish by printing a short summary to stdout — it lands in the cron log.
