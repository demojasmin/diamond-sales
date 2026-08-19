#!/usr/bin/env bash
# Runs every saved probe suite against the CURRENT Release DLL, one at a time.
# Sequential on purpose: the suites share one offscreen app instance and a stale
# DLL or a half-loaded catalogue is how a probe passes without testing anything.
#
# Each suite is a top-level program, so only one can be compiled at a time —
# hence the copy-over-Program.cs dance rather than one project per suite.
#
# Do not run this while another copy is already running: both would overwrite Program.cs
# mid-flight and each would report another suite's results.
cd "$(dirname "$0")" || exit 1

for bak in Program.*.bak; do
    suite="${bak#Program.}"
    suite="${suite%.bak}"
    cp "$bak" Program.cs
    out=$(dotnet run -c Release 2>&1)
    if echo "$out" | grep -qE "error CS|Unhandled exception"; then
        printf '%-14s BUILD/RUN FAILED\n' "$suite"
        echo "$out" | grep -E "error CS|Unhandled exception" | head -3 | sed 's/^/               /'
    else
        printf '%-14s %s\n' "$suite" "$(echo "$out" | tail -1)"
    fi
done
