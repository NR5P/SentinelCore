#!/usr/bin/env bash
# Every store upload needs a build number higher than the last, on every platform. This bumps
# the three apps together so their numbers never drift:
#
#   scripts/bump-build.sh                 # build number +1 (iOS/Mac + tvOS CURRENT_PROJECT_VERSION, Android versionCode)
#   scripts/bump-build.sh --version 0.2.0 # also set the user-visible version on all three
#
# Commit the result before running the release scripts, so a TestFlight build can always be
# traced to a commit.
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
VERSION=""
[[ "${1:-}" == "--version" ]] && VERSION=${2:?--version needs a value like 0.2.0}

python3 - "$ROOT" "$VERSION" <<'PY'
import re, sys
root, version = sys.argv[1], sys.argv[2]
pbx = [f"{root}/ios/SentinelCore.xcodeproj/project.pbxproj", f"{root}/appletv/SentinelCoreTV.xcodeproj/project.pbxproj"]
gradle = f"{root}/android/app/build.gradle.kts"

current = max(int(n) for f in pbx for n in re.findall(r"CURRENT_PROJECT_VERSION = (\d+);", open(f).read()))
current = max(current, int(re.search(r"versionCode = (\d+)", open(gradle).read()).group(1)))
new = current + 1

for f in pbx:
    s = open(f).read()
    s = re.sub(r"CURRENT_PROJECT_VERSION = \d+;", f"CURRENT_PROJECT_VERSION = {new};", s)
    if version: s = re.sub(r"MARKETING_VERSION = [^;]+;", f"MARKETING_VERSION = {version};", s)
    open(f, "w").write(s)
s = open(gradle).read()
s = re.sub(r"versionCode = \d+", f"versionCode = {new}", s)
if version: s = re.sub(r'versionName = "[^"]+"', f'versionName = "{version}"', s)
open(gradle, "w").write(s)
print(f"build {current} -> {new}" + (f", version -> {version}" if version else ""))
PY
