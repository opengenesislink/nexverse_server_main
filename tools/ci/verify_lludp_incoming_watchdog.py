#!/usr/bin/env python3
"""Guard against false LLUDP watchdog warnings while regions are idle."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
udp = (ROOT / "OpenSim/Region/ClientStack/Linden/UDP/LLUDPServer.cs").read_text(encoding="utf-8")
watchdog = (ROOT / "OpenSim/Framework/Monitoring/Watchdog.cs").read_text(encoding="utf-8")

timeout_match = re.search(r"DEFAULT_WATCHDOG_TIMEOUT_MS\s*=\s*(\d+)", watchdog)
assert timeout_match, "Watchdog timeout constant not found"
timeout_ms = int(timeout_match.group(1))

start = udp.index("protected void IncomingPacketHandler()")
end = udp.index("protected void OutgoingPacketHandler()", start)
handler = udp[start:end]

poll_match = re.search(
    r"packetInbox\.TryTake\(out IncomingPacket incomingPacket,\s*(\d+)\)", handler
)
assert poll_match, "Expected incoming-packet idle poll was changed or removed"
poll_ms = int(poll_match.group(1))
assert 0 < poll_ms <= timeout_ms // 4, (
    f"Idle packet poll ({poll_ms}ms) must be far below watchdog threshold ({timeout_ms}ms)"
)
assert "incomingPacket.Client.ProcessInPacket(incomingPacket.Packet)" in handler, (
    "Incoming packet processing must remain intact"
)
assert "Watchdog.UpdateThread();" in handler, (
    "The incoming thread must report its heartbeat after polling and processing"
)
assert 'IncomingPacketHandler,\n                $"Incoming Packets ({Scene.Name})"' in udp, (
    "Incoming packet worker must remain monitored by the watchdog"
)
print(f"LLUDP idle watchdog safety: OK ({poll_ms}ms idle poll, {timeout_ms}ms timeout)")
