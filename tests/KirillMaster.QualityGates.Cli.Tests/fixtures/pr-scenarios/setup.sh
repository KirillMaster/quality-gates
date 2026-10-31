#!/usr/bin/env bash
set -euo pipefail
name="$1"
dest="$2"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$dest"
cp -R "$here/_base/." "$dest/"
mkdir -p "$dest/quality"
cp "$here/$name/quality/gates.json" "$dest/quality/gates.json"
if [ "${NO_BASELINE:-}" != "1" ]; then cp "$here/$name/quality/baseline.json" "$dest/quality/baseline.json"; fi
if [ "${BAD_CONFIG:-}" = "1" ]; then printf '{"service":"sample","diffCoverage":{"core":"ninety"}}\n' > "$dest/quality/gates.json"; fi
cp -R "$here/$name/coverage" "$dest/coverage"
cp -R "$here/$name/sarif" "$dest/sarif"
cd "$dest"
printf 'coverage/\nsarif/\n' > .gitignore
git init -q -b develop
git config core.autocrlf false
git config user.email qg@example.com
git config user.name qg
git add -A
git commit -q -m base
git update-ref refs/remotes/origin/develop HEAD
git checkout -q -b feature/x
cp -R "$here/$name/change/." "$dest/"
git add -A
git commit -q -m change
