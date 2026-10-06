#!/usr/bin/env python3
from pathlib import Path

store = Path("NexVerse/Core/Discovery/NexDiscoveryStore.cs").read_text(encoding="utf-8")
api = Path("NexVerse/Server/Api/NexDiscoveryApi.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
security = Path("NexVerse/Core/Security/NexSecurity.cs").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")
robust = Path("bin/Robust.ini.example").read_text(encoding="utf-8")
robust_hg = Path("bin/Robust.HG.ini.example").read_text(encoding="utf-8")
opensim_config = Path("bin/OpenSim.ini.example").read_text(encoding="utf-8")
viewer = Path("NexVerse/RegionModules/Discovery/NexDiscoveryViewerModule.cs").read_text(encoding="utf-8")
openapi = Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")
regression = Path("tools/ci/NexDiscoveryRegression/Program.cs").read_text(encoding="utf-8")

for marker in (
    "### 12.1 NexSearch",
    "### 12.2 Places",
    "### 12.3 Land portal",
    "### 12.4 Destination Guide",
):
    assert marker in roadmap, f"missing Roadmap 12 marker: {marker}"

for marker in (
    "NexPlaceRecord",
    "NexEventRecord",
    "NexClassifiedRecord",
    "NexDestinationRecord",
    "NexDiscoveryPublicationState",
    "NexDiscoveryMaturity",
    "NexLandUse",
    "ListPlaces(",
    "ListEvents(",
    "ListClassifieds(",
    "ListDestinations(",
    "ModerateDestination(",
    "RecordDestinationVisit(",
    "GetCategories(",
):
    assert marker in store, f"missing NexDiscovery store marker: {marker}"

for marker in (
    "HandleSearch(",
    "HandlePlaces(",
    "HandleLandPortal(",
    "HandleDestinations(",
    "HandleContent(",
    '"people"',
    '"groups"',
    '"regions"',
    '"parcels"',
    '"places"',
    '"events"',
    '"land_for_sale"',
    '"land_for_rent"',
    '"classifieds"',
    '"experiences"',
    '"destinations"',
    "SearchLandListings(",
    "FindGroups(",
    "GetRegionsByName(",
    "GetUserAccounts(",
    "m_Experiences.Search(",
    "DestinationTeleport(",
):
    assert marker in api, f"missing NexDiscovery API marker: {marker}"

for marker in (
    '"/api/v1/search"',
    '"/api/v1/places"',
    '"/api/v1/events"',
    '"/api/v1/classifieds"',
    '"/api/v1/land-portal"',
    '"/api/v1/destinations"',
    "new NexDiscoveryStore(",
    "new NexDiscoveryApi(",
):
    assert marker in connector, f"missing NexDiscovery connector marker: {marker}"

for marker in (
    '["/api/v1/search"]',
    '["/api/v1/places"]',
    '["/api/v1/land-portal"]',
    '["/api/v1/destinations"]',
    '["/api/v1/destinations/{destinationId}/moderation"]',
    '"DiscoveryPlaceRequest"',
    '"DiscoveryDestinationRequest"',
    '"DiscoveryModerationRequest"',
    "PublicOperations(",
    "MergeOperations(",
):
    assert marker in openapi, f"missing NexDiscovery OpenAPI marker: {marker}"

for marker in (
    'DiscoveryRead = "discovery:read"',
    'DiscoverySubmit = "discovery:submit"',
    'DiscoveryManage = "discovery:manage"',
):
    assert marker in security, f"missing discovery scope: {marker}"

for config in (robust, robust_hg):
    assert "[NexDiscovery]" in config
    assert 'StorePath = "data/opengenesislink-discovery.json"' in config
    assert 'TeleportBaseUri = ""' in config

for marker in (
    "NexDiscoveryViewerModule",
    '"NexSearch"',
    '"NexPlaces"',
    '"NexLandPortal"',
    '"NexDestinationGuide"',
    "/api/v1/search",
    "/api/v1/places",
    "/api/v1/land-portal",
    "/api/v1/destinations",
):
    assert marker in viewer, f"missing viewer discovery capability marker: {marker}"

assert "[NexDiscoveryViewer]" in opensim_config
assert 'WorldApiBaseUrl = "http://127.0.0.1:8002"' in opensim_config

for marker in (
    "submitted destination leaked into public results",
    "destination moderation failed",
    "destination popularity tracking failed",
    "discovery persistence/reopen failed",
    "place cascade cleanup failed",
):
    assert marker in regression, f"missing discovery regression marker: {marker}"

print("OpenGenesisLINK Roadmap 12 NexDiscovery contract: OK")
