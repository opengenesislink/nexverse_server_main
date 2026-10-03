#!/usr/bin/env python3
"""Verify the simulator NodeAgent command handler stays directed and allowlisted."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
source = (ROOT / "NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text(encoding="utf-8-sig")

required = (
    "NexNodeCommandProtocol.RequestEvent",
    "HandleCommandRequest",
    '"target_node_id"',
    "m_NodeId",
    '"expires_at_unix"',
    "NexNodeCommandProtocol.PingAction",
    '"unsupported_action"',
    "NexNodeCommandProtocol.ResultEvent",
    "NexNodeCommandProtocol.CompletedState",
    "NexNodeCommandProtocol.RejectedState",
)

errors = [f"NodeAgent missing command-plane marker: {token}" for token in required if token not in source]

for forbidden in (
    "MainConsole.Instance.RunCommand",
    "MainConsole.Instance.Commands",
    'case "restart"',
    'case "shutdown"',
):
    if forbidden in source:
        errors.append(f"NodeAgent command handler contains forbidden legacy-console marker: {forbidden}")

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print("NexVerse directed NodeAgent command handler verified.")
