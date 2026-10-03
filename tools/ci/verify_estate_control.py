#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
errors = []

def require(path, *needles):
    text = (ROOT / path).read_text(encoding="utf-8-sig")
    for needle in needles:
        if needle not in text:
            errors.append(f"{path}: missing {needle!r}")
    return text

security = require(
    "NexVerse/Core/Security/NexSecurity.cs",
    'EstatesRead = "estates:read"',
    'EstatesManage = "estates:manage"',
)

require(
    "NexVerse/Core/Security/NexApiKeyStore.cs",
    "NexScopes.EstatesRead",
    "NexScopes.EstatesManage",
)

require(
    "NexVerse/Server/Api/NexOAuthApi.cs",
    "NexScopes.EstatesRead",
    "NexScopes.EstatesManage",
)

require(
    "NexVerse/Server/Api/NexOAuthBrowserPage.cs",
    "NexScopes.EstatesRead",
    "Estate-Informationen lesen",
)

estate_api = require(
    "NexVerse/Server/Api/NexEstateApi.cs",
    "NexScopes.EstatesRead",
    '"/api/v1/estates"',
    "LoadEstateSettingsAll()",
    "LoadEstateSettings(",
    "GetRegions(",
    "estate_id",
    "owner_id",
    "parent_estate_id",
    "region_count",
    "NexScopes.EstatesManage",
    "HandleCreate(",
    "HandleUpdate(",
    "HandleManagement(",
    "HandleRegionAssignment(",
    "EstateManagers",
    "EstateAccess",
    "EstateBans",
    "EstateGroups",
    '"estate.created"',
    '"estate.updated"',
    '"estate.region.assigned"',
    "limit > 100",
    "offset > 10000",
)

require(
    "NexVerse/Server/Api/NexRegionMutationApi.cs",
    "private readonly IEstateDataService m_Estates",
    "m_Estates.LoadEstateSettings(",
    '"estate_not_found"',
    '"estate_data_unavailable"',
)

require(
    "NexVerse/Server/Api/NexVerseUserApi.cs",
    "private readonly NexEstateApi m_EstateApi",
    '"/api/v1/estates"',
    "m_EstateApi.Handle(request, response)",
)

require(
    "NexVerse/Server/Api/NexVerseWorldApiConnector.cs",
    "IEstateDataService estateData",
    'LoadOptionalService<IEstateDataService>(config, "EstateDataStore")',
)

for config in (
    "bin/Robust.HG.ini",
    "bin/Robust.HG.ini.example",
    "bin/Robust.ini.example",
):
    require(
        config,
        "[EstateDataStore]",
        'LocalServiceModule = "OpenSim.Services.EstateService.dll:EstateDataService"',
    )

docs = require(
    "NexVerse/Server/Api/NexApiDocsPage.cs",
    'id="gridCreateEstateId"',
    'id="gridEstateOptions"',
    'id="loadGridEstates"',
    "async function loadGridEstates()",
    "'/api/v1/estates?'+qs",
    'id="loadEstateManagement"',
    'id="createEstateManagement"',
    'id="updateEstateManagement"',
    'id="assignEstateRegion"',
    "async function loadEstateManagement()",
    "async function createEstateManagement()",
    "async function updateEstateManagement()",
    "async function assignEstateRegion()",
    "estates:read",
    "estates:manage",
)

handlers = require(
    "NexVerse/Server/Api/NexVerseWorldApiHandlers.cs",
    '["/api/v1/estates"]',
    '["/api/v1/estates/{estateId}"]',
    '["/api/v1/estates/{estateId}/management"]',
    '["/api/v1/estates/{estateId}/regions/{regionId}"]',
    '"estates:read"',
    '"estates:manage"',
    '"Estate"',
    '"EstateListResponse"',
    '"EstateResponse"',
    '"EstateManagement"',
    '"EstateCreateRequest"',
    '"EstateUpdateRequest"',
    '"EstateManagementResponse"',
    '"EstateRegionAssignmentResponse"',
    '"estateId"',
)

require(
    "OpenSim/Data/SQLite/SQLiteEstateData.cs",
    'select RegionID from estate_map where EstateID = :EstateID',
    "result.Add(regionID)",
)

if errors:
    print("[NX-ESTATE-CONTROL] FEHLER")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("[NX-ESTATE-CONTROL] OK")
