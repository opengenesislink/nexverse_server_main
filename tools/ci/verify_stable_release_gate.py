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
# Scope changes must not accidentally reinstate a proprietary Firestorm
# Havok compatibility requirement or declare Experiences accepted by CI alone.
release = Path("doc/NexVerse/RELEASE_TRAIN_0.9.3.10.md").read_text()
parity = Path("doc/NexVerse/PATHFINDING_EXPERIENCES_FULL_PARITY_09310.md").read_text()
for mark in (
    "Firestorm-Havok-NavMesh und OGLVoice sind ausdruecklich keine 0.9.3.10-Stable-Blocker",
    "**Gate 1 – Functional:**",
    "**Gate 5 – Operator:**",
    "Firestorm-NavMesh auf spaeteren Viewer-Train verschoben",
    "**Gate 6 – Release:**",
):
    assert mark in release, f"0.9.3.10 scope/gate missing: {mark}"
for mark in ("**Gate P (0.9.3.10):**", "**Gate E:**", "**Gate R:**"):
    assert mark in parity, f"Native/Experience acceptance definition missing: {mark}"
assert release.count("- [ ] **Gate ") == 6, "do not claim gates passed without live acceptance"
print("Dev main safe: legacy stable 0.9.3.8 publisher remains gated; 0.9.3.10 native scope requires live acceptance.")
