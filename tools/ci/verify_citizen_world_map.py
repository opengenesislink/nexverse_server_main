#!/usr/bin/env python3
"""Source-contract regression for the public NexVerse citizen world map."""
from pathlib import Path

root=Path("NexVerse/Server/Api")
api=(root/"NexCitizenWorldMapApi.cs").read_text(encoding="utf-8")
page=(root/"NexCitizenWorldMapPage.cs").read_text(encoding="utf-8")
connector=(root/"NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
openapi=(root/"NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")
docs=Path("doc/NexVerse/WORLD_API.md").read_text(encoding="utf-8")
ci=Path(".github/workflows/nexjast-ci.yml").read_text(encoding="utf-8")

for item in (
    '"/api/v1/world-map"',
    '"/api/v1/world-map/view"',
    "apiGate.Wrap(citizenWorldMap.Handle)",
    "new NexCitizenWorldMapApi(grid, teleportBaseUri)",
):
    assert item in connector or item in api, item

for item in (
    "MaxWindowAxis = 16",
    "GetRegionRange(UUID.Zero,",
    "GetRegionsByName(UUID.Zero, search, 32)",
    "OpenSim.Framework.RegionFlags.Reservation",
    "OpenSim.Framework.RegionFlags.RegionOnline",
    "Uri.EscapeDataString(name)",
    'root.Scheme != "http"',
    "correlation_id",
    "NexApiRequestContext.Ensure(response)",
):
    assert item in api, item

assert "server_uri" not in api
assert "node_id" not in api
assert "owner_id" not in api
assert 'm_Grid.GetRegionRange(UUID.Zero, fromX, toX, fromY, toY)' in api

for item in (
    "CitizenWorldMapRegion",
    "CitizenWorldMapResponse",
    'ApplyJsonContract(paths, "/api/v1/world-map"',
    '["/api/v1/world-map/view"] = GetOperation',
):
    assert item in openapi, item

for item in (
    "Weltkarte",
    "world-map",
    "world-map/view",
    "pointerdown",
    "pointerup",
    "fetch('/api/v1/world-map'",
    "map-1-",
    "teleport_uri",
    "encodeURIComponent(q)",
    ".textContent",
    "rel=\"noopener noreferrer\"",
):
    assert item in page, item

# The old pointermove implementation caused one HTTP request per pointer movement.
pointermove=page.split("world.addEventListener('pointermove',", 1)[1].split(
    "world.addEventListener('wheel',", 1
)[0]
assert "load()" not in pointermove
assert "innerHTML" not in page, "Region names must never enter HTML unsanitized"
assert "GET /api/v1/world-map" in docs
assert "verify_citizen_world_map.py" in ci
print("Citizen world-map API, portal view, security boundaries and CI contract: OK")
