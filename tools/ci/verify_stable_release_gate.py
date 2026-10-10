#!/usr/bin/env python3
"""Keep the immutable 0.9.3.8 publisher isolated from newer dev main commits."""
from pathlib import Path

flow = Path(".github/workflows/publish-0.9.3.8-stable.yml").read_text()
required = (
    "workflows: [\"NexVerse CI\"]",
    "branches: [main]",
    "github.event.workflow_run.conclusion == 'success'",
    "github.event.workflow_run.event == 'push'",
    'test "$(git rev-parse HEAD)" = "$STABLE_SHA"',
    'VersionNumber = "0.9.3.8"',
    "VERSION_FLAVOUR = Flavour.Release;",
    "test -s doc/NexVerse/RELEASE_0.9.3.8.md",
    'echo "eligible=false" >> "$GITHUB_OUTPUT"',
    'echo "eligible=true" >> "$GITHUB_OUTPUT"',
    "if: steps.stable_check.outputs.eligible == 'true'",
    'tag="v0.9.3.8"',
    "Stable release already exists; immutable publication remains unchanged.",
    "Refusing to reuse or overwrite it.",
)
for mark in required:
    assert mark in flow, f"immutable stable release safety gate missing: {mark}"
assert flow.count("gh release create") == 1
assert "gh release delete" not in flow and "git push --force" not in flow
print("Dev main safe: legacy stable 0.9.3.8 publisher remains gated.")
