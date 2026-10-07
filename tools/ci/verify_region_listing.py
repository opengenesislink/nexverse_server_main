#!/usr/bin/env python3
from pathlib import Path

api = Path("NexVerse/Server/Api/NexVerseUserApi.cs").read_text(encoding="utf-8")

start = api.index("public IReadOnlyList<NexRegionRecord> SearchHomeRegions")
end = api.index("public NexRegionRecord ResolveHomeRegion", start)
section = api[start:end]

assert "m_Grid.GetOnlineRegions(" in section
assert "safeLimit" in section
assert "m_Grid.GetDefaultRegions(UUID.Zero)" not in section

print("World API online region listing contract: OK")
