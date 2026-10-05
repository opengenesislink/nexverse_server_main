// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using NexVerse.Core;
using NexVerse.Core.Audit;
using NexVerse.Core.Messaging;
using NexVerse.Core.Observability;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    public sealed class NexVerseWorldApiHandlers
    {
        private static readonly JsonSerializerOptions s_JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly string m_PublicBaseUrl;
        private readonly INexEventBus m_EventBus;
        private readonly INexAuditSink m_AuditSink;

        public NexVerseWorldApiHandlers(
            string publicBaseUrl,
            INexEventBus eventBus,
            INexAuditSink auditSink)
        {
            m_PublicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl)
                ? "http://world.stadt-nexverse.de"
                : publicBaseUrl.TrimEnd('/');

            m_EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            m_AuditSink = auditSink ?? NullNexAuditSink.Instance;
        }

        public void Root(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.root", "/api/v1");

            WriteJson(response, new
            {
                service = "NexVerse Welt-API",
                product = NexVersePlatform.ProductName,
                api_version = NexVersePlatform.ApiVersion,
                milestone = NexVersePlatform.MilestoneVersion,
                milestone_codename = NexVersePlatform.MilestoneCodename,
                milestone_title = NexVersePlatform.MilestoneTitle,
                language = NexVersePlatform.UiLanguage,
                status = "development",
                health = m_PublicBaseUrl + "/api/v1/health",
                version = m_PublicBaseUrl + "/api/v1/version",
                capabilities = m_PublicBaseUrl + "/api/v1/capabilities",
                openapi = m_PublicBaseUrl + "/api/v1/openapi.json",
                docs = m_PublicBaseUrl + "/api/v1/docs",
                correlation_id = correlationId
            });
        }

        public void Health(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.health.read", "/api/v1/health");

            WriteJson(response, new
            {
                status = "ok",
                product = NexVersePlatform.ProductName,
                api_version = NexVersePlatform.ApiVersion,
                milestone = NexVersePlatform.MilestoneVersion,
                milestone_codename = NexVersePlatform.MilestoneCodename,
                milestone_title = NexVersePlatform.MilestoneTitle,
                language = NexVersePlatform.UiLanguage,
                timestamp = DateTimeOffset.UtcNow,
                correlation_id = correlationId
            });
        }

        public void Version(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.version.read", "/api/v1/version");

            WriteJson(response, new
            {
                product = NexVersePlatform.ProductName,
                server_version = OpenSim.VersionInfo.Version.Trim(),
                api_version = NexVersePlatform.ApiVersion,
                protocol_version = NexVersePlatform.ProtocolVersion,
                nexbus_schema_version = NexVersePlatform.NexBusSchemaVersion,
                milestone = NexVersePlatform.MilestoneVersion,
                milestone_codename = NexVersePlatform.MilestoneCodename,
                milestone_title = NexVersePlatform.MilestoneTitle,
                language = NexVersePlatform.UiLanguage,
                correlation_id = correlationId
            });
        }

        public void Capabilities(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.capabilities.read", "/api/v1/capabilities");

            WriteJson(response, new
            {
                product = NexVersePlatform.ProductName,
                capabilities = NexVersePlatform.GetCompatibilityLevels(),
                correlation_id = correlationId
            });
        }

        public void OpenApi(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            BeginRequest(response, "worldapi.openapi.read", "/api/v1/openapi.json");

            Dictionary<string, object> paths = new Dictionary<string, object>
            {
                ["/api/v1"] = GetOperation("Metadaten des World-API-Dienstes"),
                ["/api/v1/health"] = GetOperation("Status der World API"),
                ["/api/v1/version"] = GetOperation("NexVerse-Server- und Protokollversionen"),
                ["/api/v1/capabilities"] = GetOperation("NexVerse-Funktions- und Kompatibilitätsstufen"),
                ["/api/v1/openapi.json"] = GetOperation("OpenAPI-Dokument"),
                ["/api/v1/docs"] = GetOperation("Selbst gehostete, durchsuchbare API-Dokumentation mit Live-Explorer"),
                ["/api/v1/auth/session"] = CredentialPostOperation(
                    "Native NexVerse-Einwohnersitzung erstellen"),
                ["/api/v1/auth/api-keys"] = AuthenticatedOperations(
                    ("get", "Eingeschränkte Maschinen-API-Schlüssel auflisten", "admin:*", "200"),
                    ("post", "Eingeschränkten Maschinen-API-Schlüssel mit Berechtigungsumfang erstellen", "admin:*", "201"),
                    ("patch", "Maschinen-API-Schlüssel aktivieren oder deaktivieren", "admin:*", "200")),
                ["/oauth/authorize"] = AuthenticatedOperations(
                    ("get", "OAuth-2.0-Autorisierungsendpunkt für Authorization Code + PKCE", null, "302")),
                ["/oauth/token"] = AuthenticatedOperations(
                    ("post", "OAuth-2.0-Token-Endpunkt für authorization_code, refresh_token und client_credentials", null, "200")),
                ["/oauth/revoke"] = AuthenticatedOperations(
                    ("post", "OAuth-2.0-Endpunkt zum Widerrufen von Tokens", null, "200")),
                ["/api/v1/auth/clients"] = AuthenticatedOperations(
                    ("get", "OAuth-/Dienst-Clients auflisten", "admin:*", "200"),
                    ("post", "OAuth-/Dienst-Client registrieren", "admin:*", "201"),
                    ("patch", "OAuth-/Dienst-Client aktivieren oder deaktivieren", "admin:*", "200")),
                ["/api/v1/auth/sessions/revoke"] = AuthenticatedOperations(
                    ("post", "Einwohnersitzungen widerrufen und Sicherheitsstempel fortschreiben", "self or admin:*", "200")),
                ["/api/v1/audit"] = AuthenticatedOperations(
                    ("get", "Persistente administrative Audit-Historie abfragen", "admin:*", "200")),
                ["/api/v1/statistics/summary"] = StatisticsOperation(),
                ["/api/v1/grid/layout"] = AuthenticatedOperations(
                    ("get", "Gebundenes Welt-Raster mit Zellbelegung lesen", "regions:read", "200")),
                ["/api/v1/grid/cells/{x}/{y}"] = AuthenticatedOperations(
                    ("get", "Einzelne Welt-Rasterzelle und Belegung lesen", "regions:read", "200")),
                ["/api/v1/grid/validate-placement"] = AuthenticatedOperations(
                    ("get", "Regionsplatzierung inklusive VarRegion-Footprint validieren", "regions:read", "200")),
                ["/api/v1/nodes"] = AuthenticatedOperations(
                    ("get", "Vom NexVerse NodeAgent beobachtete Simulator-Nodes auflisten", "simulators:read", "200")),
                ["/api/v1/nodes/{nodeId}"] = AuthenticatedOperations(
                    ("get", "Simulator-Node mit Gesundheits- und Regionszustand lesen", "simulators:read", "200")),
                ["/api/v1/estates"] = AuthenticatedOperations(
                    ("get", "Estate-Metadaten für Verwaltung und Regionszuordnung durchsuchen", "estates:read", "200"),
                    ("post", "Estate mit Owner, Listen und Policies erstellen", "estates:manage", "201")),
                ["/api/v1/estates/{estateId}"] = AuthenticatedOperations(
                    ("get", "Estate-Metadaten und Regionsanzahl lesen", "estates:read", "200"),
                    ("patch", "Estate-Stammdaten, Owner, Listen und Policies aktualisieren", "estates:manage", "200")),
                ["/api/v1/estates/{estateId}/management"] = AuthenticatedOperations(
                    ("get", "Verwaltungsdetails einschließlich Manager-, Zugriffs-, Ban- und Gruppenlisten lesen", "estates:manage", "200")),
                ["/api/v1/estates/{estateId}/regions/{regionId}"] = AuthenticatedOperations(
                    ("put", "Region einem Estate zuordnen oder dorthin verschieben", "estates:manage", "200")),
                ["/api/v1/regions"] = AuthenticatedOperations(
                    ("get", "Auswählbare Home-/Startregionen durchsuchen", "regions:read", "200"),
                    ("post", "NexVerse-verwaltete Region auf einem Simulator-Node erstellen", "regions:manage", "202")),
                ["/api/v1/regions/{regionId}/placement"] = AuthenticatedOperations(
                    ("patch", "NexVerse-verwaltete Region nach Placement-Prüfung verschieben", "regions:manage", "202")),
                ["/api/v1/regions/{regionId}/lifecycle"] = AuthenticatedOperations(
                    ("post", "NexVerse-verwaltete Region starten, stoppen oder neu starten", "regions:manage", "202")),
                ["/api/v1/region-operations/{operationId}"] = AuthenticatedOperations(
                    ("get", "Status einer asynchronen Regionsoperation lesen", "regions:read", "200")),
                ["/api/v1/inventory/tree"] = AuthenticatedOperations(
                    ("get", "Inventarwurzel und Ordnerstruktur lesen", "inventory:read", "200")),
                ["/api/v1/inventory/search"] = AuthenticatedOperations(
                    ("get", "Inventarordner und Items begrenzt durchsuchen", "inventory:read", "200")),
                ["/api/v1/inventory/folders"] = AuthenticatedOperations(
                    ("post", "Inventarordner erstellen", "inventory:write", "201")),
                ["/api/v1/inventory/folders/{folderId}"] = AuthenticatedOperations(
                    ("get", "Inventarordner samt direktem Inhalt lesen", "inventory:read", "200"),
                    ("patch", "Inventarordner umbenennen oder verschieben", "inventory:write", "200"),
                    ("delete", "Inventarordner sicher in den Papierkorb verschieben", "inventory:write", "200")),
                ["/api/v1/inventory/items"] = AuthenticatedOperations(
                    ("post", "Inventaritem aus vorhandenem Asset erzeugen", "admin:*", "201")),
                ["/api/v1/inventory/items/{itemId}"] = AuthenticatedOperations(
                    ("get", "Inventaritem lesen", "inventory:read", "200"),
                    ("patch", "Inventaritem umbenennen, beschreiben oder verschieben", "inventory:write", "200"),
                    ("delete", "Inventaritem sicher in den Papierkorb verschieben", "inventory:write", "200")),
                ["/api/v1/inventory/items/copy"] = AuthenticatedOperations(
                    ("post", "Eigenes Inventaritem in einen Zielordner kopieren", "inventory:write", "201")),
                ["/api/v1/inventory/links"] = AuthenticatedOperations(
                    ("post", "Direkten Item- oder Ordnerlink erzeugen", "inventory:write", "201")),
                ["/api/v1/inventory/lost-and-found"] = AuthenticatedOperations(
                    ("get", "Lost-&-Found-Ordner und direkten Inhalt lesen", "inventory:read", "200")),
                ["/api/v1/inventory/trash/empty"] = AuthenticatedOperations(
                    ("post", "Papierkorb des ausgewählten Inventars endgültig leeren", "inventory:write", "200")),
                ["/api/v1/users/me"] = AuthenticatedOperations(
                    ("get", "Authentifiziertes Einwohnerkonto lesen", null, "200")),
                ["/api/v1/users"] = AuthenticatedOperations(
                    ("get", "Benutzerkonten durchsuchen", "admin:*", "200"),
                    ("post", "Benutzerkonto erstellen und provisionieren", "admin:*", "201")),
                ["/api/v1/users/{principalId}"] = AuthenticatedOperations(
                    ("get", "Benutzerkonto lesen", "self or admin:*", "200"),
                    ("patch", "Profilfelder eines Kontos aktualisieren", "self or admin:*", "200"),
                    ("delete", "Benutzerkonto deaktivieren (Soft-Delete)", "admin:*", "200")),
                ["/api/v1/users/{principalId}/audit"] = AuthenticatedOperations(
                    ("get", "Persistente Audit-Historie eines Einwohners lesen", "admin:*", "200")),
                ["/api/v1/users/{principalId}/state"] = AuthenticatedOperations(
                    ("patch", "Benutzerkonto sperren, bannen, deaktivieren oder reaktivieren", "admin:*", "200")),
                ["/api/v1/users/{principalId}/level"] = AuthenticatedOperations(
                    ("patch", "UserLevel ändern", "admin:*", "200")),
                ["/api/v1/users/{principalId}/password"] = AuthenticatedOperations(
                    ("post", "Benutzerpasswort setzen oder zurücksetzen", "self or admin:*", "200"))
            };

            ApplyJsonContract(
                paths,
                "/api/v1/users",
                "get",
                null,
                "UserSearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users",
                "post",
                "UserCreateRequest",
                "UserCreateResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/grid/layout",
                "get",
                null,
                "GridLayoutResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/grid/cells/{x}/{y}",
                "get",
                null,
                "GridCellResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/grid/validate-placement",
                "get",
                null,
                "GridPlacementValidationResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/nodes",
                "get",
                null,
                "NodeListResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/nodes/{nodeId}",
                "get",
                null,
                "NodeResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/estates",
                "get",
                null,
                "EstateListResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/estates",
                "post",
                "EstateCreateRequest",
                "EstateManagementResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/estates/{estateId}",
                "get",
                null,
                "EstateResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/estates/{estateId}",
                "patch",
                "EstateUpdateRequest",
                "EstateManagementResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/estates/{estateId}/management",
                "get",
                null,
                "EstateManagementResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/estates/{estateId}/regions/{regionId}",
                "put",
                null,
                "EstateRegionAssignmentResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/regions",
                "get",
                null,
                "RegionSearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/regions",
                "post",
                "RegionCreateRequest",
                "RegionMutationAcceptedResponse",
                "202");
            ApplyJsonContract(
                paths,
                "/api/v1/regions/{regionId}/placement",
                "patch",
                "RegionPlacementRequest",
                "RegionMutationAcceptedResponse",
                "202");
            ApplyJsonContract(
                paths,
                "/api/v1/regions/{regionId}/lifecycle",
                "post",
                "RegionLifecycleRequest",
                "RegionMutationAcceptedResponse",
                "202");
            ApplyJsonContract(
                paths,
                "/api/v1/region-operations/{operationId}",
                "get",
                null,
                "RegionOperationResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/me",
                "get",
                null,
                "User",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}",
                "get",
                null,
                "User",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}",
                "patch",
                "UserUpdateRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/state",
                "patch",
                "AccountStateRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/level",
                "patch",
                "UserLevelRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/password",
                "post",
                "PasswordRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/session",
                "post",
                "ResidentSessionRequest",
                "ResidentSessionResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/api-keys",
                "get",
                null,
                "ApiKeyListResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/api-keys",
                "post",
                "ApiKeyCreateRequest",
                "ApiKeyCreateResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/api-keys",
                "patch",
                "ApiKeyStateRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/audit",
                "get",
                null,
                "AuditSearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/statistics/summary",
                "get",
                null,
                "StatisticsSummaryResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/inventory/tree",
                "get",
                null,
                "InventoryTreeResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/inventory/search",
                "get",
                null,
                "InventorySearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/inventory/folders/{folderId}",
                "get",
                null,
                "InventoryFolderContentResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/inventory/items/{itemId}",
                "get",
                null,
                "InventoryItemResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/inventory/items",
                "post",
                "InventoryItemCreateRequest",
                "InventoryItemCreateResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/inventory/links",
                "post",
                "InventoryLinkCreateRequest",
                "InventoryLinkCreateResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/audit",
                "get",
                null,
                "AuditSearchResponse",
                "200");

            object nodeIdParameter =
                StringPathParameter(
                    "nodeId",
                    "Stabile NodeId des NexVerse Simulator-NodeAgents.");
            AddOperationParameters(
                paths,
                "/api/v1/nodes/{nodeId}",
                "get",
                nodeIdParameter);

            AddOperationParameters(
                paths,
                "/api/v1/estates",
                "get",
                QueryParameter(
                    "q",
                    false,
                    "Optionaler Estate-Name oder exakte Estate-ID; leer listet alle Estates."),
                QueryParameter(
                    "owner_id",
                    false,
                    "Optionaler Filter nach Estate-Owner-UUID."),
                PaginationParameter(
                    "limit",
                    50,
                    1,
                    100),
                PaginationParameter(
                    "offset",
                    0,
                    0,
                    10000));

            object estateIdParameter =
                IntegerPathParameter(
                    "estateId",
                    1,
                    "Positive Estate-ID.");
            AddOperationParameters(
                paths,
                "/api/v1/estates/{estateId}",
                "get",
                estateIdParameter);
            AddOperationParameters(
                paths,
                "/api/v1/estates/{estateId}",
                "patch",
                estateIdParameter);
            AddOperationParameters(
                paths,
                "/api/v1/estates/{estateId}/management",
                "get",
                estateIdParameter);

            object estateRegionIdParameter =
                PathParameter(
                    "regionId",
                    "UUID der Region, die dem Estate zugeordnet werden soll.");
            AddOperationParameters(
                paths,
                "/api/v1/estates/{estateId}/regions/{regionId}",
                "put",
                estateIdParameter,
                estateRegionIdParameter);

            object regionIdParameter =
                PathParameter(
                    "regionId",
                    "UUID der NexVerse-verwalteten Region.");
            AddOperationParameters(
                paths,
                "/api/v1/regions/{regionId}/placement",
                "patch",
                regionIdParameter,
                HeaderParameter(
                    "Idempotency-Key",
                    false,
                    "Optionaler wiederholungssicherer Anfrageschlüssel; maximal 128 Zeichen."));
            AddOperationParameters(
                paths,
                "/api/v1/regions/{regionId}/lifecycle",
                "post",
                regionIdParameter,
                HeaderParameter(
                    "Idempotency-Key",
                    false,
                    "Optionaler wiederholungssicherer Anfrageschlüssel; empfohlen insbesondere für restart."));

            object operationIdParameter =
                StringPathParameter(
                    "operationId",
                    "Kennung der asynchronen Regionsoperation.");
            AddOperationParameters(
                paths,
                "/api/v1/region-operations/{operationId}",
                "get",
                operationIdParameter);

            AddOperationParameters(
                paths,
                "/api/v1/regions",
                "post",
                HeaderParameter(
                    "Idempotency-Key",
                    false,
                    "Optionaler wiederholungssicherer Anfrageschlüssel; maximal 128 Zeichen."));

            object inventoryOwnerParameter =
                QueryParameter(
                    "owner_id",
                    false,
                    "Optionaler Inventory-Owner als UUID; ein fremder Owner erfordert zusätzlich admin:*.");

            AddOperationParameters(
                paths,
                "/api/v1/inventory/tree",
                "get",
                inventoryOwnerParameter);
            AddOperationParameters(
                paths,
                "/api/v1/inventory/search",
                "get",
                inventoryOwnerParameter,
                QueryParameter(
                    "q",
                    false,
                    "Optionaler, nicht zwischen Groß-/Kleinschreibung unterscheidender Suchbegriff."),
                IntegerQueryParameter(
                    "limit",
                    false,
                    1,
                    500,
                    "Maximale Anzahl Ordner und Items je Ergebnistyp; Standard 100."));

            AddOperationParameters(
                paths,
                "/api/v1/inventory/folders",
                "post",
                inventoryOwnerParameter);
            AddOperationParameters(
                paths,
                "/api/v1/inventory/items",
                "post",
                inventoryOwnerParameter);
            AddOperationParameters(
                paths,
                "/api/v1/inventory/items/copy",
                "post",
                inventoryOwnerParameter);
            AddOperationParameters(
                paths,
                "/api/v1/inventory/links",
                "post",
                inventoryOwnerParameter);
            AddOperationParameters(
                paths,
                "/api/v1/inventory/lost-and-found",
                "get",
                inventoryOwnerParameter);
            AddOperationParameters(
                paths,
                "/api/v1/inventory/trash/empty",
                "post",
                inventoryOwnerParameter);

            object inventoryFolderIdParameter =
                PathParameter(
                    "folderId",
                    "UUID des Inventarordners.");
            foreach (string method in new[]
                     {
                         "get",
                         "patch",
                         "delete"
                     })
            {
                AddOperationParameters(
                    paths,
                    "/api/v1/inventory/folders/{folderId}",
                    method,
                    inventoryFolderIdParameter,
                    inventoryOwnerParameter);
            }

            object inventoryItemIdParameter =
                PathParameter(
                    "itemId",
                    "UUID des Inventaritems.");
            foreach (string method in new[]
                     {
                         "get",
                         "patch",
                         "delete"
                     })
            {
                AddOperationParameters(
                    paths,
                    "/api/v1/inventory/items/{itemId}",
                    method,
                    inventoryItemIdParameter,
                    inventoryOwnerParameter);
            }

            object principalIdParameter =
                PathParameter(
                    "principalId",
                    "UUID des Einwohner-Principals.");

            foreach (string method in new[]
                     {
                         "get",
                         "patch",
                         "delete"
                     })
            {
                AddOperationParameters(
                    paths,
                    "/api/v1/users/{principalId}",
                    method,
                    principalIdParameter);
            }

            foreach (string path in new[]
                     {
                         "/api/v1/users/{principalId}/audit",
                         "/api/v1/users/{principalId}/state",
                         "/api/v1/users/{principalId}/level",
                         "/api/v1/users/{principalId}/password"
                     })
            {
                string method =
                    path.EndsWith("/audit", StringComparison.Ordinal)
                        ? "get"
                        : path.EndsWith("/password", StringComparison.Ordinal)
                            ? "post"
                            : "patch";

                AddOperationParameters(
                    paths,
                    path,
                    method,
                    principalIdParameter);
            }

            AddOperationParameters(
                paths,
                "/api/v1/users",
                "get",
                QueryParameter(
                    "q",
                    true,
                    "Suchbegriff für Benutzer; mindestens zwei Zeichen."),
                QueryParameter(
                    "state",
                    false,
                    "Optionaler Filter nach Kontostatus."),
                QueryParameter(
                    "sort",
                    false,
                    "Sortierung nach name, created, user_level oder state."),
                QueryParameter(
                    "order",
                    false,
                    "Sortierrichtung: asc oder desc."),
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            AddOperationParameters(
                paths,
                "/api/v1/users",
                "post",
                HeaderParameter(
                    "Idempotency-Key",
                    false,
                    "Optionaler wiederholungssicherer Anfrageschlüssel; maximal 128 Zeichen."));

            AddOperationParameters(
                paths,
                "/api/v1/grid/layout",
                "get",
                IntegerQueryParameter(
                    "min_x",
                    true,
                    0,
                    null,
                    "Kleinste Grid-X-Zelle des Viewports."),
                IntegerQueryParameter(
                    "max_x",
                    true,
                    0,
                    null,
                    "Größte Grid-X-Zelle des Viewports; maximal 128 Zellen Spannweite."),
                IntegerQueryParameter(
                    "min_y",
                    true,
                    0,
                    null,
                    "Kleinste Grid-Y-Zelle des Viewports."),
                IntegerQueryParameter(
                    "max_y",
                    true,
                    0,
                    null,
                    "Größte Grid-Y-Zelle des Viewports; maximal 128 Zellen Spannweite."));

            AddOperationParameters(
                paths,
                "/api/v1/grid/cells/{x}/{y}",
                "get",
                IntegerPathParameter(
                    "x",
                    0,
                    "Grid-X-Zelle auf 256m-Basis."),
                IntegerPathParameter(
                    "y",
                    0,
                    "Grid-Y-Zelle auf 256m-Basis."));

            AddOperationParameters(
                paths,
                "/api/v1/grid/validate-placement",
                "get",
                IntegerQueryParameter(
                    "x",
                    true,
                    0,
                    null,
                    "Grid-X-Ursprung der geplanten Region."),
                IntegerQueryParameter(
                    "y",
                    true,
                    0,
                    null,
                    "Grid-Y-Ursprung der geplanten Region."),
                IntegerQueryParameter(
                    "size_x",
                    true,
                    256,
                    4096,
                    "Regionsbreite in Metern; Vielfaches von 256."),
                IntegerQueryParameter(
                    "size_y",
                    true,
                    256,
                    4096,
                    "Regionshöhe in Metern; Vielfaches von 256."));

            AddOperationParameters(
                paths,
                "/api/v1/regions",
                "get",
                QueryParameter(
                    "q",
                    false,
                    "Optionaler Suchbegriff für Regionsname oder UUID."),
                QueryParameter(
                    "sort",
                    false,
                    "Sortierung nach name, size_x oder size_y."),
                QueryParameter(
                    "order",
                    false,
                    "Sortierrichtung: asc oder desc."),
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            AddOperationParameters(
                paths,
                "/api/v1/audit",
                "get",
                QueryParameter(
                    "resource",
                    false,
                    "Exakte Ressourcenkennung."),
                QueryParameter(
                    "actor",
                    false,
                    "Exakter Audit-Akteur."),
                QueryParameter(
                    "action",
                    false,
                    "Exakte Aktion oder Präfix mit abschließendem Platzhalter, zum Beispiel users.*."),
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            AddOperationParameters(
                paths,
                "/api/v1/users/{principalId}/audit",
                "get",
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            WriteJson(response, new
            {
                openapi = "3.1.0",
                info = new
                {
                    title = "NexVerse Welt-API",
                    version = NexVersePlatform.ApiVersion,
                    description = "NexVerse-Robust-Steuerungs-API in deutscher Oberfläche. Administrative Endpunkte erfordern eine Authentifizierung mit passendem Berechtigungsumfang."
                },
                servers = new[]
                {
                    new { url = m_PublicBaseUrl }
                },
                paths,
                components = new
                {
                    schemas = BuildSchemas(),
                    securitySchemes = new
                    {
                        bearerAuth = new
                        {
                            type = "http",
                            scheme = "bearer",
                            bearerFormat = "JWT"
                        },
                        apiKeyAuth = new
                        {
                            type = "apiKey",
                            @in = "header",
                            name = "X-NexVerse-Api-Key"
                        }
                    }
                },
                x_nexverse_language = NexVersePlatform.UiLanguage,
                x_nexverse_version_history = new object[]
                {
                    new
                    {
                        api_version = NexVersePlatform.ApiVersion,
                        server_line = "0.9.3.5",
                        codename = NexVersePlatform.MilestoneCodename,
                        status = "development",
                        published = "2026-10-04",
                        compatibility = "OpenGenesisLINK v0.9.3.5 Dev — Inventory, OAR/IAR, Job Engine und NexBus",
                        highlights = new[]
                        {
                            "0.9.3.5 Entwicklungslinie gestartet",
                            "LunaTexture Recovery & Diagnostics als erster Entwicklungsblock",
                            "Inventory/OAR/IAR und Job Engine als aktive Roadmap"
                        }
                    },
                    new
                    {
                        api_version = NexVersePlatform.ApiVersion,
                        server_line = "0.9.3.4",
                        codename = NexVersePlatform.MilestoneCodename,
                        status = "release",
                        published = "2026-10-04",
                        compatibility = "Stabile NexVerse 0.9.3.4 Control Plane",
                        highlights = new[]
                        {
                            "0.9.3.2 und 0.9.3.3 Entwicklungs-Checkpoints abgeschlossen",
                            "NodeAgent-, Region- und Estate-Control-Plane für 0.9.3.4 integriert",
                            "NexVerse 0.9.3.4 stabil veröffentlicht"
                        }
                    },
                    new
                    {
                        api_version = NexVersePlatform.ApiVersion,
                        server_line = "0.9.3.2",
                        codename = "",
                        status = "completed",
                        published = "2026-10-03",
                        compatibility = "Abgeschlossener World-API-v1-Entwicklungs-Checkpoint innerhalb des 0.9.3.4 Release-Zugs",
                        highlights = new[]
                        {
                            "World API v1 mit OpenAPI 3.1, OAuth2/OIDC, API-Schlüsseln, Audit und Control Center gehärtet",
                            "36/36 explizite Roadmap-Kriterien abgeschlossen",
                            "NodeAgent-, Region- und Estate-Control-Plane-Arbeit für 0.9.3.4 vorgezogen und abgesichert"
                        }
                    },
                    new
                    {
                        api_version = NexVersePlatform.ApiVersion,
                        server_line = "0.9.3.1",
                        codename = "NEXJAST",
                        status = "release",
                        published = "2026-10-02",
                        compatibility = "Erster Vertrag der NexVerse Welt-API v1 und Plattform-Grundlage",
                        highlights = new[]
                        {
                            "Native Einwohnersitzungen und Zugriffstokens mit Berechtigungsumfang",
                            "OAuth2/OIDC-Autorisierungscode mit PKCE und Dienstzugängen",
                            "Persistente Audit-Historie, API-Schlüssel und idempotente Provisionierung",
                            "Selbst gehostetes API-Kontrollzentrum, interaktiver API-Prüfer und Statistikübersicht",
                            "Verteilte NexBus- und NodeAgent-Grundlage",
                            "Prometheus-/OpenTelemetry-/OTLP-Beobachtbarkeitsgrundlage"
                        }
                    }
                },
                x_nexverse_changelog = new object[]
                {
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.3",
                        category = "identity",
                        status = "implemented",
                        title = "Persistente Display-Name-Grundlage",
                        summary = "Display Names werden persistent im autoritativen UserAccount-Datensatz gespeichert. World API, Viewer GetDisplayNames/SetDisplayName, Login-Antwort und LSL llGetDisplayName/llRequestDisplayName verwenden dieselbe Quelle. Einwohner können ihren Anzeigenamen mit Sieben-Tage-Sperre ändern; Administratoren dürfen die Sperre übersteuern.",
                        endpoints = new[]
                        {
                            "/api/v1/users/{principalId}",
                            "/api/v1/openapi.json",
                            "/api/v1/docs"
                        }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.3",
                        category = "identity",
                        status = "development",
                        title = "NexVerse 0.9.3.3 Dev gestartet",
                        summary = "Der World-API-v1-Checkpoint 0.9.3.2 ist abgeschlossen. Die aktive Entwicklungslinie wechselt auf Identität, Anzeigenamen, Profile und soziales Netzwerk; MFA/Passkeys werden in diesem Sicherheits- und Identitätsmeilenstein weitergeführt.",
                        endpoints = new[] { "/api/v1/version", "/api/v1/openapi.json", "/api/v1/docs" }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.2",
                        category = "regions",
                        status = "implemented",
                        title = "Estate-Verwaltung: Owner, Listen, Policies und Regionszuordnung",
                        summary = "Die World API kann Estates mit estates:manage erstellen und bearbeiten, Owner und Manager sowie Allow-/Ban-/Gruppenlisten verwalten, zentrale Estate-Policies setzen und registrierte Regionen einem Estate zuordnen. Verwaltungslisten werden nur über den manage-geschützten Detailendpunkt ausgegeben.",
                        endpoints = new[]
                        {
                            "/api/v1/estates",
                            "/api/v1/estates/{estateId}",
                            "/api/v1/estates/{estateId}/management",
                            "/api/v1/estates/{estateId}/regions/{regionId}",
                            "/api/v1/docs"
                        }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.2",
                        category = "regions",
                        status = "implemented",
                        title = "Estate-Lesegrundlage für Region Control Plane",
                        summary = "Die privilegierte World API veröffentlicht mit estates:read grundlegende Estate-Metadaten, Owner, Parent-Estate und Regionsanzahl. Der Grid Planner kann diese Liste zur Auswahl der Estate-ID beim Erstellen verwalteter Regionen verwenden, ohne Estate-Zugriffslisten oder andere sensible Detaildaten offenzulegen.",
                        endpoints = new[]
                        {
                            "/api/v1/estates",
                            "/api/v1/estates/{estateId}",
                            "/api/v1/regions",
                            "/api/v1/docs"
                        }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.2",
                        category = "regions",
                        status = "implemented",
                        title = "Regionssuche, Lifecycle und interaktive Grid-Planer-Aktionen",
                        summary = "Robust validiert Regionen und Ziel-Nodes, veröffentlicht adressierte NexBus-Kommandos und verfolgt create/move/start/stop/restart als Operationen. Der Grid Planner kann registrierte Regionen global suchen und per veröffentlichter Grid-/Weltkoordinate anspringen, Regionen erstellen/verschieben sowie managed Start/Stop/Restart auslösen. Stop und Restart werden bei aktiven Root-Agents verweigert.",
                        endpoints = new[]
                        {
                            "/api/v1/regions",
                            "/api/v1/regions/{regionId}/placement",
                            "/api/v1/regions/{regionId}/lifecycle",
                            "/api/v1/region-operations/{operationId}",
                            "/api/v1/nodes/{nodeId}"
                        }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.2",
                        category = "platform",
                        status = "implemented",
                        title = "Robust NodeAgent Registry und Simulator-Kontrollansicht",
                        summary = "Robust projiziert NodeAgent-Heartbeats und Regions-Lebenszyklusereignisse jetzt in eine laufende Simulator-Registry mit online/stale/offline-Zustand. Geschützte API-Endpunkte und das API Control Center liefern Node-, Prozess-, Agenten- und Regionsdaten mit simulators:read.",
                        endpoints = new[]
                        {
                            "/api/v1/nodes",
                            "/api/v1/nodes/{nodeId}",
                            "/api/v1/grid/layout",
                            "/api/v1/docs"
                        }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.2",
                        category = "regions",
                        status = "implemented",
                        title = "Interaktiver Grid-Planer im API Control Center",
                        summary = "Das API Control Center besitzt jetzt eine read-only Rasteransicht mit Viewport-Navigation, Zellstatus, Hover-/Klickdetails, Größenwahl und Live-Placement-Vorschau über die Region-Control-Plane-Endpunkte.",
                        endpoints = new[]
                        {
                            "/api/v1/docs",
                            "/api/v1/grid/layout",
                            "/api/v1/grid/validate-placement"
                        }
                    },
                    new
                    {
                        date = "2026-10-03",
                        version = "0.9.3.2",
                        category = "regions",
                        status = "implemented",
                        title = "Region Control Plane: Raster- und Placement-Grundlage",
                        summary = "Die Welt-API kann gebundene 256m-Rasterfenster lesen, einzelne Zellen inspizieren und Regionsplatzierungen inklusive VarRegion-Footprints, Reservierungen und Überlappungen vorab validieren.",
                        endpoints = new[]
                        {
                            "/api/v1/grid/layout",
                            "/api/v1/grid/cells/{x}/{y}",
                            "/api/v1/grid/validate-placement"
                        }
                    },
                    new
                    {
                        date = "2026-10-02",
                        version = "0.9.3.2",
                        category = "api",
                        status = "development",
                        title = "NexVerse 0.9.3.2 Dev gestartet",
                        summary = "Die Welt-API-v1-Arbeit ist jetzt die aktive Produktlinie. Laufzeitversion, Entwicklungsplan-Metadaten, API-Status und CI-Vertrag wurden auf 0.9.3.2 Dev umgestellt.",
                        endpoints = new[] { "/api/v1/version", "/api/v1/openapi.json", "/api/v1/docs" }
                    },
                    new
                    {
                        date = "2026-10-02",
                        version = "0.9.3.1",
                        category = "analytics",
                        status = "implemented",
                        title = "Einwohner- und Hypergrid-Statistik-Dashboard",
                        summary = "Authentifizierte Statistiken der Welt-API zeigen registrierte Einwohner, aktuelle Anwesenheit, Aktivität der letzten 7/30 Tage, Hypergrid-Besucher, Aufschlüsselungen nach Heimat-Grid und Regionsbelegung im API-Kontrollzentrum.",
                        endpoints = new[] { "/api/v1/statistics/summary", "/api/v1/docs" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "observability",
                        status = "implemented",
                        title = "Produktive OTLP-Beobachtbarkeitspipeline",
                        summary = "Prometheus-kompatible NexMetrics, OpenTelemetry-kompatibles Tracing sowie begrenzter OTLP/HTTP-JSON-Export mit Wiederholungs- und TLS-Steuerung.",
                        endpoints = new[] { "/internal/metrics" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "security",
                        status = "implemented",
                        title = "Umstellung auf native Authentifizierung",
                        summary = "Die Authentifizierung der Welt-API hängt nicht mehr von der vorläufigen alten Bearer-Token-Anmeldung des AuthenticationService ab.",
                        endpoints = new[] { "/api/v1/auth/session", "/oauth/token", "/oauth/revoke", "/api/v1/auth/sessions/revoke" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "api",
                        status = "implemented",
                        title = "OAuth2/OIDC, API-Schlüssel und persistente API-Sicherheitskontrollen",
                        summary = "Authorization Code + PKCE, Refresh-Token-Rotation, ES256-ID-Tokens/JWKS, Dienstkonto-Zugangsdaten, gehashte API-Schlüssel mit Berechtigungsumfang, Rate-Limits und persistente Idempotenz sind verfügbar.",
                        endpoints = new[] { "/oauth/authorize", "/oauth/token", "/oauth/jwks", "/api/v1/auth/api-keys", "/api/v1/auth/clients" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "api",
                        status = "implemented",
                        title = "OpenAPI-3.1-Vertrag und API-Kontrollzentrum",
                        summary = "Der Live-Vertrag liefert konkrete Schemas, Zielgruppen-/Sicherheitsmetadaten der Endpunkte, Versionshistorie und maschinenlesbare Versionshinweise für die selbst gehostete Weboberfläche.",
                        endpoints = new[] { "/api/v1/openapi.json", "/api/v1/docs" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "identity",
                        status = "implemented",
                        title = "Einwohnerkompatible Anmeldenamen",
                        summary = "Kurze, punktgetrennte und ältere Resident-Anmeldeformen werden normalisiert, ohne gespeicherte Kontonamen umzuschreiben.",
                        endpoints = Array.Empty<string>()
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "platform",
                        status = "implemented",
                        title = "Verteilte NexBus- und Simulator-NodeAgent-Grundlage",
                        summary = "Authentifizierter HMAC-Peer-Transport, begrenzte Zustellung, Ereignis-Deduplizierung, Node-Heartbeats und Regions-Lebenszyklusereignisse verbinden Robust und Simulator-Nodes.",
                        endpoints = new[] { "/internal/nexbus/v1/events" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "runtime",
                        status = "implemented",
                        title = "BulletSim- und Meshmerizer-Laufzeitbasis wiederhergestellt",
                        summary = "Das NexVerse-Simulatorprofil standardisiert wieder BulletSim-Physik mit Meshmerizer und behält Warp3D-Kartenrendering als unterstützte Laufzeitbasis bei.",
                        endpoints = Array.Empty<string>()
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1",
                        category = "scripting",
                        status = "implemented",
                        title = "LSL-/OSSL-Kompatibilitätsarbeit",
                        summary = "RemoteData-XML-RPC-Regressionstests, das XML-RPC-Kompatibilitätsprofil des Simulators und zusätzliche OSSL-Helfer für geskriptete Inhalte sind aktiviert und durch CI abgesichert.",
                        endpoints = Array.Empty<string>()
                    }
                },
                x_nexverse_roadmap = new
                {
                    source = "doc/NexVerse/ROADMAP.md",
                    current_milestone = NexVersePlatform.MilestoneVersion,
                    current_codename = NexVersePlatform.MilestoneCodename,
                    status_model = new
                    {
                        released = "Abgeschlossener und veröffentlichter Produkt-Meilenstein.",
                        completed = "Abgeschlossener Entwicklungs-Meilenstein innerhalb eines größeren Release-Zugs; kein eigener Stable-Release.",
                        active = "Aktueller Produkt-Meilenstein.",
                        advanced = "Wesentliche Implementierungen wurden vor dem formalen Wechsel des Meilensteins vorgezogen.",
                        started = "Ein definierter Teil ist umgesetzt, wesentlicher Umfang steht jedoch noch aus.",
                        foundation = "Die Kerninfrastruktur ist vorhanden, während der Produktumfang des Meilensteins größtenteils noch offen ist.",
                        planned = "Der Roadmap-Umfang ist definiert; eine NexVerse-native Umsetzung dieses Meilensteins wird noch nicht beansprucht."
                    },
                    milestones = new object[]
                    {
                        new
                        {
                            version = "0.9.3.1",
                            codename = "NEXJAST",
                            title = "Altlasten-Bereinigung und Plattform-Grundlage",
                            status = "released",
                            checklist = new { completed = 45, total = 45, open = 0 },
                            summary = "Bereinigte Kernbasis, native Welt-API und Authentifizierung, NexBus, NodeAgent, Beobachtbarkeit und Kompatibilitätsschutz.",
                            evidence = new[]
                            {
                                "RemoteAdmin, Vivox, FreeSwitch und IRC-Bridge entfernt",
                                "Native Welt-API, OAuth2/OIDC, API-Schlüssel und Benutzerlebenszyklus umgesetzt",
                                "Verteilte NexBus-, NodeAgent- und NexMetrics/OTLP-Grundlage umgesetzt",
                                "Hypergrid-Anmeldung und RemoteData mit laufendem Skript sind durchgängig durch CI verifiziert"
                            }
                        },
                        new
                        {
                            version = "0.9.3.2",
                            codename = "",
                            title = "NexVerse Welt-API v1",
                            status = "completed",
                            checklist = new { completed = 36, total = 36, open = 0 },
                            summary = "Abgeschlossener Entwicklungs-Checkpoint innerhalb des 0.9.3.4 Release-Zugs. World API v1, Authentifizierung, Audit, Control Center und Betriebsverträge sind gehärtet; MFA/Passkeys wurden bewusst in den 0.9.3.3 Identitäts-Meilenstein verschoben.",
                            evidence = new[]
                            {
                                "REST/JSON, OpenAPI 3.1, Seitennavigation, Filterung, Anfragelimits und Idempotenz",
                                "OAuth2/OIDC, Dienstzugänge, API-Schlüssel mit Berechtigungsumfang und Prüfprotokoll-Historie",
                                "API-Kontrollzentrum, Versionshistorie und maschinenlesbares Änderungsprotokoll",
                                "Authentifizierte Statistikübersicht für Einwohneraktivität, Anwesenheit und Hypergrid"
                            }
                        },
                        new
                        {
                            version = "0.9.3.3",
                            codename = "",
                            title = "Identität, Anzeigenamen, Profile und soziales Netzwerk",
                            status = "completed",
                            checklist = new { completed = 4, total = 4, open = 0 },
                            summary = "Die einwohnerkompatible Normalisierung von Benutzernamen und die persistente Display-Name-Grundlage sind umgesetzt. Viewer-CAPS, Login, World API und LSL verwenden dieselbe Display-Name-Quelle; WebProfileV3 sowie die vollständige Chat/IM/Gruppen/Suche-Propagation bleiben offen.",
                            evidence = new[]
                            {
                                "Kurze, punktgetrennte und ältere Resident-Anmeldeformen sind normalisiert und CI-getestet",
                                "Persistente Display Names mit UserAccount-Migrationen für MySQL/MariaDB, PostgreSQL und SQLite",
                                "GetDisplayNames/SetDisplayName einschließlich EventQueue-Aktualisierung sowie Login- und LSL-Integration",
                                "World API stellt Display-Name-Zustand bereit und erzwingt die Sieben-Tage-Selbständerungssperre"
                            }
                        },
                        new
                        {
                            version = "0.9.3.4",
                            codename = "",
                            title = "Simulator-, Regionen- und Estate-Verwaltung",
                            status = "released",
                            checklist = (object)null,
                            summary = "NodeAgent-Registry, Raster-/Placement-Control-Plane, global durchsuchbarer interaktiver Grid Planner sowie verwaltetes Erstellen, Verschieben, Starten, Stoppen und Neustarten von Regionen sind vorgezogen umgesetzt. Die Estate-Control-Plane unterstützt Lesen, Erstellen, Bearbeiten und sicheres Löschen einschließlich Owner/Manager, Zugriffslisten, zentralen Policies und Regionszuordnung. Templates und weitere Spezialregeln folgen in späteren Meilensteinen.",
                            evidence = new[]
                            {
                                "NexVerseNodeAgentModule veröffentlicht Knoten-/Regionszustand und regelmäßige Statusmeldungen",
                                "Grid-Layout, VarRegion-Placement-Prüfung, Regionssuche mit Koordinaten und interaktiver Grid Planner sind über die Welt-API verbunden",
                                "NexVerse-managed create/move/start/stop/restart werden adressiert über NexBus ausgeführt, im Control Center bedient und asynchron als Operationen verfolgt",
                                "GET /api/v1/estates und /api/v1/estates/{estateId} liefern grundlegende Estate-Metadaten mit estates:read",
                                "estates:manage steuert Estate create/update/delete, Management-Details und Region→Estate-Zuordnung inklusive Audit/NexBus-Ereignissen"
                            }
                        },
                        new
                        {
                            version = "0.9.3.5",
                            codename = "",
                            title = "Inventar, OAR/IAR, Auftragssteuerung und NexBus",
                            status = "active",
                            checklist = new { completed = 74, total = 74, open = 0 },
                            summary = "Alle 74 expliziten 0.9.3.5-Checklist-Punkte sind umgesetzt. Inventar-API-Grundlage, OAR/IAR, persistente Auftragssteuerung, native Betriebs-Worker sowie LunaTexture-Persistenz und deren aggregierte NodeAgent/World-API-Projektion sind integriert; nicht checkbox-basierte Erweiterungen werden weiterhin separat verfolgt.",
                            evidence = new[]
                            {
                                "Persistente Job Engine mit queued/running/completed/failed/cancelled, Fortschritt, Logs und Result-Metadaten",
                                "OAR/IAR sind über NexBus/NodeAgent an die autoritativen Archivmodule angebunden",
                                "Native Backup/Restore-, Asset-Reindex-, Regionsmigrations-, Datenbankwartungs- und Inventarreparatur-Worker sind integriert",
                                "Inventar-API unterstützt Baum-, Ordner-, Item-Metadaten-, Trash-, Restore-, Copy- und Lost-&-Found-Abläufe",
                                "LunaTexture-Diagnosen werden pro Region persistent gespeichert und als datensparsame Aggregate über NodeAgent und World API projiziert"
                            }
                        },
                        new { version = "0.9.3.6", codename = "", title = "NV$-Wirtschaft, Bankwesen, Handel und Land", status = "planned", checklist = (object)null, summary = "Natives Kontobuch, Bankwesen, Viewer-Wirtschaftskompatibilität und Landhandel.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.7", codename = "", title = "NexGroups und NexExperiences", status = "planned", checklist = (object)null, summary = "Native Gruppen- und Experience-Dienste einschließlich Viewer-/LSL-Integration.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.8", codename = "", title = "Suche, Orte, Land und Reiseführer", status = "planned", checklist = (object)null, summary = "Suche, Orte, Landportal und Dienste zur Zielentdeckung.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.9", codename = "", title = "NexVoice", status = "planned", checklist = (object)null, summary = "WebRTC-/Janus-Sprachplattform als Ersatz für entfernte Legacy-Sprachsysteme.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.10", codename = "", title = "Wegfindung", status = "planned", checklist = (object)null, summary = "Wegfindungsdienst, NavMesh-Verhalten und LSL-Integration.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.11", codename = "", title = "LSL-Parität I — Kernkonformität", status = "planned", checklist = (object)null, summary = "Maschinenlesbare LSL-Matrix, Konformitätstests und Härtung der Script-Sandbox.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.12", codename = "", title = "LSL-Parität II — Erweiterte Systeme", status = "planned", checklist = (object)null, summary = "Erweiterte LSL-Systeme, Profiler und Entwicklerwerkzeuge.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.13", codename = "", title = "Assets, Darstellung, Marktplatz und Medien", status = "planned", checklist = (object)null, summary = "NexAsset, moderne Material- und Inhaltspipeline, Marktplatz und Medien.", evidence = Array.Empty<string>() },
                        new { version = "0.9.3.14", codename = "", title = "NexAds, Veranstaltungen und Benachrichtigungen", status = "planned", checklist = (object)null, summary = "Plattformdienste für Werbung, Veranstaltungen und Benachrichtigungen.", evidence = Array.Empty<string>() },
                        new
                        {
                            version = "0.9.3.15",
                            codename = "",
                            title = "Überwachung, Sicherheit und Betrieb",
                            status = "foundation",
                            checklist = (object)null,
                            summary = "Beobachtbarkeit und mehrere Sicherheitskontrollen wurden vorgezogen; Übersichten, Alarmierung, Sicherungen, Moderation und gestaffelte Aktualisierungen bleiben zukünftiger Umfang.",
                            evidence = new[]
                            {
                                "Prometheus-kompatible NexMetrics und OpenTelemetry-/OTLP-Export",
                                "Korrelations-IDs, API-Rate-Limits, Audit-Datensätze und Zugangsdaten mit Berechtigungsumfang"
                            }
                        },
                        new { version = "0.9.3.16", codename = "", title = "Entwicklerplattform, SDKs und Erweiterbarkeit", status = "planned", checklist = (object)null, summary = "Entwicklerportal, offizielle SDKs, Webhooks, Echtzeit-APIs und Modulverträge.", evidence = Array.Empty<string>() },
                        new { version = "0.9.4.0", codename = "", title = "OpenGenesisLINK Generation 1", status = "planned", checklist = (object)null, summary = "Konsolidierungsmeilenstein für die erste vollständige OpenGenesisLINK-Plattformgeneration.", evidence = Array.Empty<string>() }
                    }
                },
                x_nexverse_idempotency = new
                {
                    header = "Idempotency-Key",
                    maximum_key_length = 128,
                    replay_header = "Idempotency-Replayed",
                    protected_operations = new[]
                    {
                        "POST /api/v1/users",
                        "POST /api/v1/regions",
                        "PATCH /api/v1/regions/{regionId}/placement",
                        "POST /api/v1/regions/{regionId}/lifecycle"
                    }
                },
                x_nexverse_rate_limit = new
                {
                    headers = new[]
                    {
                        "RateLimit-Limit",
                        "RateLimit-Remaining",
                        "RateLimit-Reset",
                        "Retry-After"
                    },
                    status = 429
                },
                x_nexverse_filtering_sorting = new
                {
                    users = new
                    {
                        filters = new[] { "state" },
                        sort = new[] { "name", "created", "user_level", "state" },
                        order = new[] { "asc", "desc" }
                    },
                    regions = new
                    {
                        sort = new[] { "name", "size_x", "size_y" },
                        order = new[] { "asc", "desc" }
                    }
                },
                x_nexverse_pagination = new
                {
                    query_parameters = new[] { "limit", "offset" },
                    default_limit = 50,
                    maximum_limit = 100,
                    maximum_offset = 10000,
                    response_fields = new[]
                    {
                        "limit",
                        "offset",
                        "returned",
                        "has_more",
                        "next_offset"
                    }
                },
                x_nexverse_audience = new[] { "citizen", "admin" },
                x_nexverse_ai_instruction = new
                {
                    citizen = "Use only citizen-authorized endpoints and never assume administrative permissions.",
                    admin = "Administrative actions require explicit authenticated scopes and must produce audit events."
                }
            });
        }

        private string BeginRequest(IOSHttpResponse response, string action, string resource)
        {
            string correlationId =
                NexApiRequestContext.Ensure(response);

            NexMetricsRegistry.Default.IncrementCounter(
                "nexverse_world_api_public_requests_total",
                "Public NexVerse World API requests.",
                1,
                new Dictionary<string, string>
                {
                    ["route"] = resource
                });

            response.KeepAlive = false;

            m_AuditSink.Record(new NexAuditEvent(
                "anonymous",
                action,
                resource,
                correlationId));

            m_EventBus.Publish(new NexEvent(
                "api.request",
                "nexverse.world-api",
                new Dictionary<string, string>
                {
                    ["action"] = action,
                    ["resource"] = resource
                },
                correlationId));

            return correlationId;
        }

        private static bool RequireGet(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request != null && string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                return true;

            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.ContentType = "application/json";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = "method_not_allowed",
                message = "Dieser Endpunkt akzeptiert derzeit ausschließlich GET-Anfragen.",
                correlation_id = NexApiRequestContext.CurrentCorrelationId
            }, s_JsonOptions);
            return false;
        }

        private static object GetOperation(string summary)
        {
            Dictionary<string, object> operation =
                new Dictionary<string, object>
                {
                    ["summary"] = summary,
                    ["responses"] =
                        new Dictionary<string, object>
                        {
                            ["200"] =
                                new
                                {
                                    description =
                                        "Erfolgreiche Antwort"
                                },
                            ["429"] =
                                JsonResponse(
                                    "Rate-Limit überschritten",
                                    "Error")
                        },
                    ["security"] = Array.Empty<object>(),
                    ["x-nexverse-audience"] =
                        new[]
                        {
                            "citizen",
                            "admin",
                            "service"
                        },
                    ["x-nexverse-purpose"] = summary,
                    ["x-nexverse-ai-instruction"] =
                        "Dies ist eine öffentliche, schreibgeschützte Operation. Aus ihrer Verfügbarkeit dürfen keine zusätzlichen Rechte abgeleitet werden.",
                    ["x-nexverse-security-constraints"] =
                        new[]
                        {
                            "Keine Zugangsdaten senden, sofern eine Operation sie nicht ausdrücklich erfordert.",
                            "Rate-Limits und Korrelations-IDs beachten."
                        }
                };

            return new Dictionary<string, object>
            {
                ["get"] = operation
            };
        }

        private static object StatisticsOperation()
        {
            Dictionary<string, object> operation =
                new Dictionary<string, object>
                {
                    ["summary"] =
                        "Öffentliche Aggregatstatistik lesen; geschützte Online-Details optional mit statistics:read",
                    ["security"] = new object[]
                    {
                        new Dictionary<string, string[]>(),
                        new Dictionary<string, string[]>
                        {
                            ["bearerAuth"] = Array.Empty<string>()
                        },
                        new Dictionary<string, string[]>
                        {
                            ["apiKeyAuth"] = Array.Empty<string>()
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "Aggregierte oder authentifizierte Statistikantwort" },
                        ["401"] = JsonResponse("Ungültige oder erforderliche Authentifizierung", "Error"),
                        ["403"] = JsonResponse("Unzureichender Berechtigungsumfang", "Error"),
                        ["429"] = JsonResponse("Rate-Limit überschritten", "Error"),
                        ["503"] = JsonResponse("Statistikdaten oder geschützte Authentifizierung nicht verfügbar", "Error")
                    },
                    ["x-nexverse-scope"] = NexVerse.Core.Security.NexScopes.StatisticsRead,
                    ["x-nexverse-audience"] = new[] { "citizen", "admin", "service" },
                    ["x-nexverse-purpose"] =
                        "Liefert ohne Zugangsdaten ausschließlich aggregierte Grid-Kennzahlen. Mit gültigem statistics:read dürfen zusätzlich geschützte Online-, Regions- und Hypergrid-Detaildaten zurückgegeben werden.",
                    ["x-nexverse-ai-instruction"] =
                        "Für allgemeine Kennzahlen keine Zugangsdaten senden. Personenbezogene oder standortbezogene Detaildaten nur mit ausdrücklich bereitgestellter statistics:read-Autorisierung abrufen.",
                    ["x-nexverse-security-constraints"] = new[]
                    {
                        "Anonyme Antworten dürfen keine Namen, Principal-IDs, konkreten Online-Regionen oder Hypergrid-Herkunftslisten enthalten.",
                        "Geschützte Details erfordern statistics:read und einen TLS- oder gleichwertig geschützten Transport.",
                        "Bearer-Tokens und API-Schlüssel niemals offenlegen oder protokollieren."
                    }
                };

            return new Dictionary<string, object>
            {
                ["get"] = operation
            };
        }

        private static object CredentialPostOperation(
            string summary)
        {
            Dictionary<string, object> operation =
                new Dictionary<string, object>
                {
                    ["summary"] = summary,
                    ["security"] = Array.Empty<object>(),
                    ["responses"] =
                        new Dictionary<string, object>
                        {
                            ["200"] =
                                new
                                {
                                    description =
                                        "Erfolgreiche Antwort"
                                },
                            ["400"] =
                                JsonResponse(
                                    "Ungültige Anfrage",
                                    "Error"),
                            ["401"] =
                                JsonResponse(
                                    "Ungültige Einwohner-Zugangsdaten",
                                    "Error"),
                            ["429"] =
                                JsonResponse(
                                    "Rate-Limit überschritten",
                                    "Error"),
                            ["503"] =
                                JsonResponse(
                                    "Nativer Token-Dienst nicht verfügbar",
                                    "Error")
                        },
                    ["x-nexverse-audience"] =
                        new[]
                        {
                            "citizen",
                            "admin"
                        },
                    ["x-nexverse-purpose"] = summary,
                    ["x-nexverse-ai-instruction"] =
                        "Diesen Endpunkt nur verwenden, um mit ausdrücklich dafür bereitgestellten Zugangsdaten eine Einwohnersitzung zu erstellen. Passwörter niemals ableiten, wiederverwenden, speichern oder offenlegen.",
                    ["x-nexverse-security-constraints"] =
                        new[]
                        {
                            "TLS oder ein gleichwertig geschützter Transport ist zwingend erforderlich.",
                            "Das Passwort ausschließlich im JSON-Anfrageinhalt senden.",
                            "Passwort und zurückgegebenes Zugriffstoken niemals protokollieren, zwischenspeichern oder dauerhaft speichern."
                        }
                };

            return new Dictionary<string, object>
            {
                ["post"] = operation
            };
        }

        private static object AuthenticatedOperations(
            params (string method, string summary, string scope, string successCode)[] operations)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();

            foreach ((string method, string summary, string scope, string successCode) operationSpec in operations)
            {
                Dictionary<string, object> operation = new Dictionary<string, object>
                {
                    ["summary"] = operationSpec.summary,
                    ["security"] = new object[]
                    {
                        new Dictionary<string, string[]>
                        {
                            ["bearerAuth"] = Array.Empty<string>()
                        },
                        new Dictionary<string, string[]>
                        {
                            ["apiKeyAuth"] = Array.Empty<string>()
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        [operationSpec.successCode] = new { description = "Erfolgreiche Antwort" },
                        ["400"] = JsonResponse("Ungültige Anfrage", "Error"),
                        ["401"] = JsonResponse("Authentifizierung erforderlich", "Error"),
                        ["403"] = JsonResponse("Unzureichender Berechtigungsumfang", "Error"),
                        ["404"] = JsonResponse("Ressource nicht gefunden", "Error"),
                        ["429"] = JsonResponse("Rate-Limit überschritten", "Error")
                    }
                };

                if (!string.IsNullOrWhiteSpace(operationSpec.scope))
                    operation["x-nexverse-scope"] = operationSpec.scope;

                operation["x-nexverse-audience"] =
                    AudienceForScope(operationSpec.scope);
                operation["x-nexverse-purpose"] =
                    operationSpec.summary;
                operation["x-nexverse-ai-instruction"] =
                    AiInstructionForScope(operationSpec.scope);
                operation["x-nexverse-security-constraints"] =
                    SecurityConstraintsForScope(
                        operationSpec.scope);

                result[operationSpec.method] = operation;
            }

            return result;
        }

        private static string[] AudienceForScope(
            string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.IndexOf(
                    NexVerse.Core.Security.NexScopes.AdminAll,
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                !scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "admin" };
            }

            if (!string.IsNullOrWhiteSpace(scope) &&
                !scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "admin",
                    "service"
                };
            }

            return new[]
            {
                "citizen",
                "admin"
            };
        }

        private static string AiInstructionForScope(
            string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.IndexOf(
                    NexVerse.Core.Security.NexScopes.AdminAll,
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                !scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return
                    "Administrative Operation. Eine ausdrückliche authentifizierte Admin-Autorisierung verlangen; Berechtigungen niemals ableiten oder erweitern.";
            }

            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return
                    "Einwohner-Selbstbedienungsoperation. Standardmäßig den authentifizierten Benutzer verwenden; der Zugriff auf einen anderen Einwohner erfordert die dokumentierte Admin-Autorisierung.";
            }

            if (!string.IsNullOrWhiteSpace(scope))
            {
                return
                    "Operation mit Berechtigungsumfang. Vor der Ausführung prüfen, ob der authentifizierte Principal über den dokumentierten Scope verfügt.";
            }

            return
                "Authentifizierte Operation. Dem dokumentierten Authentifizierungsablauf folgen und niemals Autorisierung erfinden.";
        }

        private static string[] SecurityConstraintsForScope(
            string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.IndexOf(
                    NexVerse.Core.Security.NexScopes.AdminAll,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new[]
                {
                    "Administrative Autorisierung muss ausdrücklich vorliegen.",
                    "Bearer-Tokens, API-Schlüssel, Passwörter oder Client-Secrets niemals offenlegen.",
                    "Audit- und Korrelationsmetadaten bei zustandsändernden Aktionen erhalten."
                };
            }

            return new[]
            {
                "Bearer-Tokens, API-Schlüssel, Passwörter oder Client-Secrets niemals offenlegen.",
                "Dokumentierten Scope, Rate-Limits und Idempotenzanforderungen beachten."
            };
        }

        private static void ApplyJsonContract(
            Dictionary<string, object> paths,
            string path,
            string method,
            string requestSchema,
            string responseSchema,
            string successCode)
        {
            if (!paths.TryGetValue(path, out object pathValue) ||
                pathValue is not Dictionary<string, object> pathItem ||
                !pathItem.TryGetValue(method, out object operationValue) ||
                operationValue is not Dictionary<string, object> operation)
                return;

            if (!string.IsNullOrWhiteSpace(requestSchema))
            {
                operation["requestBody"] = new
                {
                    required = true,
                    content = new Dictionary<string, object>
                    {
                        ["application/json"] = new
                        {
                            schema = SchemaRef(requestSchema)
                        }
                    }
                };
            }

            if (!string.IsNullOrWhiteSpace(responseSchema) &&
                operation.TryGetValue(
                    "responses",
                    out object responsesValue) &&
                responsesValue is Dictionary<string, object> responses)
            {
                responses[successCode] =
                    JsonResponse(
                        "Erfolgreiche Antwort",
                        responseSchema);
            }
        }

        private static void AddOperationParameters(
            Dictionary<string, object> paths,
            string path,
            string method,
            params object[] parameters)
        {
            if (!paths.TryGetValue(path, out object pathValue) ||
                pathValue is not Dictionary<string, object> pathItem ||
                !pathItem.TryGetValue(method, out object operationValue) ||
                operationValue is not Dictionary<string, object> operation)
                return;

            operation["parameters"] = parameters;
        }

        private static object PathParameter(
            string name,
            string description)
        {
            return new
            {
                name,
                @in = "path",
                required = true,
                description,
                schema = new
                {
                    type = "string",
                    format = "uuid"
                }
            };
        }

        private static object StringPathParameter(
            string name,
            string description)
        {
            return new
            {
                name,
                @in = "path",
                required = true,
                description,
                schema = new
                {
                    type = "string",
                    minLength = 1
                }
            };
        }

        private static object QueryParameter(
            string name,
            bool required,
            string description)
        {
            return new
            {
                name,
                @in = "query",
                required,
                description,
                schema = new
                {
                    type = "string"
                }
            };
        }

        private static object IntegerQueryParameter(
            string name,
            bool required,
            int minimum,
            int? maximum,
            string description)
        {
            Dictionary<string, object> schema =
                new Dictionary<string, object>
                {
                    ["type"] = "integer",
                    ["minimum"] = minimum
                };

            if (maximum.HasValue)
                schema["maximum"] = maximum.Value;

            return new
            {
                name,
                @in = "query",
                required,
                description,
                schema
            };
        }

        private static object IntegerPathParameter(
            string name,
            int minimum,
            string description)
        {
            return new
            {
                name,
                @in = "path",
                required = true,
                description,
                schema = new
                {
                    type = "integer",
                    minimum
                }
            };
        }

        private static object HeaderParameter(
            string name,
            bool required,
            string description)
        {
            return new
            {
                name,
                @in = "header",
                required,
                description,
                schema = new
                {
                    type = "string"
                }
            };
        }

        private static object PaginationParameter(
            string name,
            int defaultValue,
            int minimum,
            int maximum)
        {
            return new
            {
                name,
                @in = "query",
                required = false,
                schema = new
                {
                    type = "integer",
                    @default = defaultValue,
                    minimum,
                    maximum
                }
            };
        }

        private static object JsonResponse(
            string description,
            string schemaName)
        {
            return new
            {
                description,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new
                    {
                        schema = SchemaRef(schemaName)
                    }
                }
            };
        }

        private static Dictionary<string, object> SchemaRef(
            string schemaName)
        {
            return new Dictionary<string, object>
            {
                ["$ref"] =
                    "#/components/schemas/" + schemaName
            };
        }

        private static object UuidArraySchema()
        {
            return new
            {
                type = "array",
                uniqueItems = true,
                items = new
                {
                    type = "string",
                    format = "uuid"
                }
            };
        }

        private static object EstateMutationSchema(
            bool create)
        {
            Dictionary<string, object> schema =
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] =
                        new Dictionary<string, object>
                        {
                            ["name"] = new { type = "string", maxLength = 64 },
                            ["owner_id"] = new { type = "string", format = "uuid" },
                            ["parent_estate_id"] = new { type = "integer", minimum = 0 },
                            ["managers"] = UuidArraySchema(),
                            ["allowed_residents"] = UuidArraySchema(),
                            ["banned_residents"] = UuidArraySchema(),
                            ["allowed_groups"] = UuidArraySchema(),
                            ["public_access"] = new { type = "boolean" },
                            ["allow_voice"] = new { type = "boolean" },
                            ["allow_direct_teleport"] = new { type = "boolean" },
                            ["estate_skip_scripts"] = new { type = "boolean" },
                            ["deny_anonymous"] = new { type = "boolean" },
                            ["deny_minors"] = new { type = "boolean" },
                            ["allow_environment_override"] = new { type = "boolean" }
                        }
                };

            if (create)
            {
                schema["required"] =
                    new[]
                    {
                        "name",
                        "owner_id"
                    };
            }

            return schema;
        }

        private static Dictionary<string, object> BuildSchemas()
        {
            object paginationRef = SchemaRef("Pagination");
            object userRef = SchemaRef("User");
            object regionRef = SchemaRef("Region");
            object gridRegionRef = SchemaRef("GridRegionPlacement");
            object gridCellRef = SchemaRef("GridCell");
            object apiKeyRef = SchemaRef("ApiKey");
            object auditEventRef = SchemaRef("AuditEvent");
            object estateRef = SchemaRef("Estate");
            object inventoryFolderRef = SchemaRef("InventoryFolder");
            object inventoryItemRef = SchemaRef("InventoryItem");
            object inventoryPermissionsRef = SchemaRef("InventoryPermissions");

            return new Dictionary<string, object>
            {
                ["Error"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "error",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["error"] = new { type = "string" },
                        ["message"] = new { type = "string" },
                        ["error_description"] = new { type = "string" },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["Pagination"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "limit",
                        "offset",
                        "returned",
                        "has_more"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["limit"] = new { type = "integer", minimum = 1, maximum = 100 },
                        ["offset"] = new { type = "integer", minimum = 0, maximum = 10000 },
                        ["returned"] = new { type = "integer", minimum = 0 },
                        ["has_more"] = new { type = "boolean" },
                        ["next_offset"] = new
                        {
                            type = new[] { "integer", "null" }
                        }
                    }
                },
                ["Position"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["x"] = new { type = "number", format = "float" },
                        ["y"] = new { type = "number", format = "float" },
                        ["z"] = new { type = "number", format = "float" }
                    }
                },
                ["Region"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "region_id",
                        "name",
                        "grid_x",
                        "grid_y",
                        "world_x",
                        "world_y",
                        "size_x",
                        "size_y"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["server_uri"] = new { type = "string" },
                        ["grid_x"] = new { type = "integer" },
                        ["grid_y"] = new { type = "integer" },
                        ["world_x"] = new { type = "integer" },
                        ["world_y"] = new { type = "integer" },
                        ["size_x"] = new { type = "integer" },
                        ["size_y"] = new { type = "integer" }
                    }
                },
                ["GridRegionPlacement"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "region_id",
                        "name",
                        "grid_x",
                        "grid_y",
                        "world_x",
                        "world_y",
                        "size_x",
                        "size_y",
                        "cells_x",
                        "cells_y",
                        "occupied",
                        "reserved"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["grid_x"] = new { type = "integer" },
                        ["grid_y"] = new { type = "integer" },
                        ["world_x"] = new { type = "integer" },
                        ["world_y"] = new { type = "integer" },
                        ["size_x"] = new { type = "integer", minimum = 256, maximum = 4096 },
                        ["size_y"] = new { type = "integer", minimum = 256, maximum = 4096 },
                        ["cells_x"] = new { type = "integer", minimum = 1 },
                        ["cells_y"] = new { type = "integer", minimum = 1 },
                        ["occupied"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["min_x"] = new { type = "integer" },
                                ["max_x"] = new { type = "integer" },
                                ["min_y"] = new { type = "integer" },
                                ["max_y"] = new { type = "integer" }
                            }
                        },
                        ["online"] = new { type = new[] { "boolean", "null" } },
                        ["reserved"] = new { type = "boolean" },
                        ["node_id"] = new { type = new[] { "string", "null" } },
                        ["node_state"] = new
                        {
                            type = new[] { "string", "null" },
                            @enum = new object[] { "online", "stale", "offline", null }
                        },
                        ["server_uri"] = new { type = "string" }
                    }
                },
                ["NodeRegion"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "region_id",
                        "name",
                        "server_uri",
                        "size_x",
                        "size_y",
                        "agent_count",
                        "last_seen"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["server_uri"] = new { type = "string" },
                        ["size_x"] = new { type = "integer", minimum = 0 },
                        ["size_y"] = new { type = "integer", minimum = 0 },
                        ["agent_count"] = new { type = "integer", minimum = 0 },
                        ["last_seen"] = new { type = "string", format = "date-time" }
                    }
                },
                ["LunaTextureDiagnosticsSummary"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "diagnostic_count",
                        "occurrence_count",
                        "regions_affected",
                        "last_seen",
                        "classifications"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["source"] = new { type = "string" },
                        ["node_count"] = new { type = "integer", minimum = 0 },
                        ["diagnostic_count"] = new { type = "integer", minimum = 0 },
                        ["occurrence_count"] = new { type = "integer", format = "int64", minimum = 0 },
                        ["regions_affected"] = new { type = "integer", minimum = 0 },
                        ["last_seen"] = new
                        {
                            type = new[] { "string", "null" },
                            format = "date-time"
                        },
                        ["classifications"] = new
                        {
                            type = "object",
                            additionalProperties = new
                            {
                                type = "integer",
                                format = "int64",
                                minimum = 0
                            }
                        }
                    }
                },
                ["Node"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "node_id",
                        "hostname",
                        "server_version",
                        "state",
                        "uptime_seconds",
                        "process_id",
                        "working_set_bytes",
                        "cpu_seconds",
                        "region_count",
                        "agent_count",
                        "managed_region_commands",
                        "luna_texture",
                        "last_seen",
                        "last_event_at",
                        "regions"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["node_id"] = new { type = "string" },
                        ["hostname"] = new { type = "string" },
                        ["server_version"] = new { type = "string" },
                        ["state"] = new
                        {
                            type = "string",
                            @enum = new[] { "online", "stale", "offline" }
                        },
                        ["uptime_seconds"] = new { type = "integer", format = "int64", minimum = 0 },
                        ["process_id"] = new { type = "integer", minimum = 0 },
                        ["working_set_bytes"] = new { type = "integer", format = "int64", minimum = 0 },
                        ["cpu_seconds"] = new { type = "number", format = "double", minimum = 0 },
                        ["region_count"] = new { type = "integer", minimum = 0 },
                        ["agent_count"] = new { type = "integer", minimum = 0 },
                        ["managed_region_commands"] = new { type = "boolean" },
                        ["luna_texture"] = SchemaRef("LunaTextureDiagnosticsSummary"),
                        ["last_seen"] = new { type = "string", format = "date-time" },
                        ["last_event_at"] = new { type = "string", format = "date-time" },
                        ["regions"] = new
                        {
                            type = "array",
                            items = SchemaRef("NodeRegion")
                        }
                    }
                },
                ["NodeListResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "generated_at",
                        "transport_enabled",
                        "stale_after_seconds",
                        "count",
                        "luna_texture",
                        "nodes",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["generated_at"] = new { type = "string", format = "date-time" },
                        ["transport_enabled"] = new { type = "boolean" },
                        ["stale_after_seconds"] = new { type = "integer", minimum = 10 },
                        ["count"] = new { type = "integer", minimum = 0 },
                        ["luna_texture"] = SchemaRef("LunaTextureDiagnosticsSummary"),
                        ["nodes"] = new
                        {
                            type = "array",
                            items = SchemaRef("Node")
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["NodeResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "generated_at",
                        "transport_enabled",
                        "stale_after_seconds",
                        "node",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["generated_at"] = new { type = "string", format = "date-time" },
                        ["transport_enabled"] = new { type = "boolean" },
                        ["stale_after_seconds"] = new { type = "integer", minimum = 10 },
                        ["node"] = SchemaRef("Node"),
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["InventoryPermissions"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "base_mask",
                        "current_mask",
                        "everyone_mask",
                        "group_mask",
                        "next_owner_mask"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["base_mask"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["current_mask"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["everyone_mask"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["group_mask"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["next_owner_mask"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L }
                    }
                },
                ["InventoryFolder"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "id",
                        "owner_id",
                        "parent_id",
                        "name",
                        "type",
                        "version"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["id"] = new { type = "string", format = "uuid" },
                        ["owner_id"] = new { type = "string", format = "uuid" },
                        ["parent_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["type"] = new { type = "integer" },
                        ["version"] = new { type = "integer" }
                    }
                },
                ["InventoryItem"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "id",
                        "owner_id",
                        "folder_id",
                        "asset_id",
                        "name",
                        "description",
                        "asset_type",
                        "inventory_type",
                        "creator_id",
                        "creation_date",
                        "flags",
                        "permissions"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["id"] = new { type = "string", format = "uuid" },
                        ["owner_id"] = new { type = "string", format = "uuid" },
                        ["folder_id"] = new { type = "string", format = "uuid" },
                        ["asset_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["description"] = new { type = "string" },
                        ["asset_type"] = new { type = "integer" },
                        ["inventory_type"] = new { type = "integer" },
                        ["creator_id"] = new { type = "string" },
                        ["creator_data"] = new { type = "string" },
                        ["creation_date"] = new { type = "integer", format = "int64" },
                        ["flags"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["permissions"] = inventoryPermissionsRef
                    }
                },
                ["InventoryTreeResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "owner_id",
                        "root",
                        "folders",
                        "folder_count"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["owner_id"] = new { type = "string", format = "uuid" },
                        ["root"] = new
                        {
                            oneOf = new object[]
                            {
                                inventoryFolderRef,
                                new { type = "null" }
                            }
                        },
                        ["folders"] = new
                        {
                            type = "array",
                            items = inventoryFolderRef
                        },
                        ["folder_count"] = new { type = "integer", minimum = 0 }
                    }
                },
                ["InventoryFolderContentResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "folder",
                        "folders",
                        "items"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["folder"] = inventoryFolderRef,
                        ["folders"] = new
                        {
                            type = "array",
                            items = inventoryFolderRef
                        },
                        ["items"] = new
                        {
                            type = "array",
                            items = inventoryItemRef
                        }
                    }
                },
                ["InventoryItemResponse"] = new
                {
                    type = "object",
                    required = new[] { "item" },
                    properties = new Dictionary<string, object>
                    {
                        ["item"] = inventoryItemRef
                    }
                },
                ["InventorySearchScan"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "folder_limit",
                        "item_limit",
                        "scanned_folders",
                        "scanned_items",
                        "truncated"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["folder_limit"] = new { type = "integer", minimum = 1 },
                        ["item_limit"] = new { type = "integer", minimum = 1 },
                        ["scanned_folders"] = new { type = "integer", minimum = 0 },
                        ["scanned_items"] = new { type = "integer", minimum = 0 },
                        ["truncated"] = new { type = "boolean" }
                    }
                },
                ["InventorySearchResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "query",
                        "limit",
                        "scan",
                        "folders",
                        "items",
                        "folder_count",
                        "item_count"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", maxLength = 255 },
                        ["limit"] = new { type = "integer", minimum = 1, maximum = 500 },
                        ["scan"] = SchemaRef("InventorySearchScan"),
                        ["folders"] = new
                        {
                            type = "array",
                            items = inventoryFolderRef
                        },
                        ["items"] = new
                        {
                            type = "array",
                            items = inventoryItemRef
                        },
                        ["folder_count"] = new { type = "integer", minimum = 0 },
                        ["item_count"] = new { type = "integer", minimum = 0 }
                    }
                },
                ["InventoryItemCreateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "folder_id",
                        "asset_id",
                        "name",
                        "base_permissions",
                        "current_permissions",
                        "next_owner_permissions"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["folder_id"] = new { type = "string", format = "uuid" },
                        ["asset_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string", minLength = 1, maxLength = 255 },
                        ["description"] = new { type = "string" },
                        ["inventory_type"] = new { type = "integer" },
                        ["base_permissions"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["current_permissions"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["next_owner_permissions"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["everyone_permissions"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["group_permissions"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L },
                        ["flags"] = new { type = "integer", format = "int64", minimum = 0L, maximum = 4294967295L }
                    }
                },
                ["InventoryItemCreateResponse"] = new
                {
                    type = "object",
                    required = new[] { "item", "asset" },
                    properties = new Dictionary<string, object>
                    {
                        ["item"] = inventoryItemRef,
                        ["asset"] = new
                        {
                            type = "object",
                            required = new[]
                            {
                                "id",
                                "type",
                                "content_type"
                            },
                            properties = new Dictionary<string, object>
                            {
                                ["id"] = new { type = "string" },
                                ["type"] = new { type = "integer" },
                                ["content_type"] = new { type = "string" }
                            }
                        }
                    }
                },
                ["InventoryLinkCreateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "folder_id",
                        "target_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["folder_id"] = new { type = "string", format = "uuid" },
                        ["target_id"] = new { type = "string", format = "uuid" },
                        ["target_kind"] = new
                        {
                            type = "string",
                            @enum = new[] { "item", "folder" },
                            @default = "item"
                        },
                        ["name"] = new { type = "string", minLength = 1, maxLength = 255 },
                        ["description"] = new { type = "string" }
                    }
                },
                ["InventoryLinkCreateResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "link",
                        "target_kind",
                        "target_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["link"] = inventoryItemRef,
                        ["target_kind"] = new
                        {
                            type = "string",
                            @enum = new[] { "item", "folder" }
                        },
                        ["target_id"] = new { type = "string", format = "uuid" }
                    }
                },
                ["Estate"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "estate_id",
                        "name",
                        "owner_id",
                        "parent_estate_id",
                        "region_count"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["estate_id"] = new { type = "integer", minimum = 1 },
                        ["name"] = new { type = "string" },
                        ["owner_id"] = new { type = "string", format = "uuid" },
                        ["parent_estate_id"] = new { type = "integer", minimum = 0 },
                        ["region_count"] = new { type = "integer", minimum = 0 }
                    }
                },
                ["EstateListResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "count",
                        "estates",
                        "pagination",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer", minimum = 0 },
                        ["estates"] = new
                        {
                            type = "array",
                            items = estateRef
                        },
                        ["pagination"] = SchemaRef("Pagination"),
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["EstateResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "estate",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["estate"] = estateRef,
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["EstatePolicies"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "public_access",
                        "allow_voice",
                        "allow_direct_teleport",
                        "estate_skip_scripts",
                        "deny_anonymous",
                        "deny_minors",
                        "allow_environment_override"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["public_access"] = new { type = "boolean" },
                        ["allow_voice"] = new { type = "boolean" },
                        ["allow_direct_teleport"] = new { type = "boolean" },
                        ["estate_skip_scripts"] = new { type = "boolean" },
                        ["deny_anonymous"] = new { type = "boolean" },
                        ["deny_minors"] = new { type = "boolean" },
                        ["allow_environment_override"] = new { type = "boolean" }
                    }
                },
                ["EstateManagement"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "estate_id",
                        "name",
                        "owner_id",
                        "parent_estate_id",
                        "managers",
                        "allowed_residents",
                        "banned_residents",
                        "allowed_groups",
                        "policies",
                        "region_count",
                        "region_ids"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["estate_id"] = new { type = "integer", minimum = 1 },
                        ["name"] = new { type = "string" },
                        ["owner_id"] = new { type = "string", format = "uuid" },
                        ["parent_estate_id"] = new { type = "integer", minimum = 0 },
                        ["managers"] = UuidArraySchema(),
                        ["allowed_residents"] = UuidArraySchema(),
                        ["banned_residents"] = UuidArraySchema(),
                        ["allowed_groups"] = UuidArraySchema(),
                        ["policies"] = SchemaRef("EstatePolicies"),
                        ["region_count"] = new { type = "integer", minimum = 0 },
                        ["region_ids"] = UuidArraySchema()
                    }
                },
                ["EstateCreateRequest"] = EstateMutationSchema(true),
                ["EstateUpdateRequest"] = EstateMutationSchema(false),
                ["EstateManagementResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "estate",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["estate"] = SchemaRef("EstateManagement"),
                        ["changed_fields"] = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["EstateRegionAssignmentResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "estate",
                        "region_id",
                        "already_linked",
                        "region_restart_required",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["estate"] = estateRef,
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["previous_estate_id"] = new { type = new[] { "integer", "null" } },
                        ["already_linked"] = new { type = "boolean" },
                        ["region_restart_required"] = new { type = "boolean" },
                        ["message"] = new { type = "string" },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["RegionCreateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "name",
                        "node_id",
                        "estate_id",
                        "grid_x",
                        "grid_y"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "string", maxLength = 128 },
                        ["node_id"] = new { type = "string", maxLength = 128 },
                        ["estate_id"] = new { type = "integer", minimum = 1 },
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["grid_x"] = new { type = "integer", minimum = 0 },
                        ["grid_y"] = new { type = "integer", minimum = 0 },
                        ["size_x"] = new { type = "integer", minimum = 256, maximum = 4096, @default = 256 },
                        ["size_y"] = new { type = "integer", minimum = 256, maximum = 4096, @default = 256 }
                    }
                },
                ["RegionPlacementRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "grid_x",
                        "grid_y"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["grid_x"] = new { type = "integer", minimum = 0 },
                        ["grid_y"] = new { type = "integer", minimum = 0 }
                    }
                },
                ["RegionLifecycleRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "action"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["action"] = new
                        {
                            type = "string",
                            @enum = new[] { "start", "stop", "restart" }
                        },
                        ["node_id"] = new
                        {
                            type = "string",
                            maxLength = 128,
                            description = "Für start erforderlich; bei stop/restart optional und muss dem aktuellen Host entsprechen."
                        }
                    }
                },
                ["RegionOperation"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "operation_id",
                        "operation",
                        "state",
                        "node_id",
                        "region_id",
                        "created_at",
                        "updated_at",
                        "details"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["operation_id"] = new { type = "string" },
                        ["operation"] = new { type = "string", @enum = new[] { "create", "move", "start", "stop", "restart" } },
                        ["state"] = new { type = "string", @enum = new[] { "queued", "accepted", "completed", "failed" } },
                        ["message"] = new { type = "string" },
                        ["node_id"] = new { type = "string" },
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["created_at"] = new { type = "string", format = "date-time" },
                        ["updated_at"] = new { type = "string", format = "date-time" },
                        ["details"] = new
                        {
                            type = "object",
                            additionalProperties = new { type = "string" }
                        }
                    }
                },
                ["RegionMutationAcceptedResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "operation",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["operation"] = SchemaRef("RegionOperation"),
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["RegionOperationResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "operation",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["operation"] = SchemaRef("RegionOperation"),
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["GridCell"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "grid_x",
                        "grid_y",
                        "world_x",
                        "world_y",
                        "status",
                        "region_ids"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["grid_x"] = new { type = "integer" },
                        ["grid_y"] = new { type = "integer" },
                        ["world_x"] = new { type = "integer" },
                        ["world_y"] = new { type = "integer" },
                        ["status"] = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "free",
                                "occupied",
                                "reserved",
                                "conflict"
                            }
                        },
                        ["region_id"] = new { type = new[] { "string", "null" }, format = "uuid" },
                        ["region_name"] = new { type = new[] { "string", "null" } },
                        ["region_ids"] = new
                        {
                            type = "array",
                            items = new { type = "string", format = "uuid" }
                        }
                    }
                },
                ["GridLayoutResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "cell_size_meters",
                        "bounds",
                        "counts",
                        "regions",
                        "cells",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["cell_size_meters"] = new { type = "integer", @const = 256 },
                        ["bounds"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["min_x"] = new { type = "integer" },
                                ["max_x"] = new { type = "integer" },
                                ["min_y"] = new { type = "integer" },
                                ["max_y"] = new { type = "integer" },
                                ["min_world_x"] = new { type = "integer" },
                                ["max_world_x"] = new { type = "integer" },
                                ["min_world_y"] = new { type = "integer" },
                                ["max_world_y"] = new { type = "integer" }
                            }
                        },
                        ["counts"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["cells"] = new { type = "integer" },
                                ["free"] = new { type = "integer" },
                                ["occupied"] = new { type = "integer" },
                                ["reserved"] = new { type = "integer" },
                                ["conflict"] = new { type = "integer" },
                                ["regions"] = new { type = "integer" }
                            }
                        },
                        ["regions"] = new
                        {
                            type = "array",
                            items = gridRegionRef
                        },
                        ["cells"] = new
                        {
                            type = "array",
                            items = gridCellRef
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["GridCellResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "cell",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["cell"] = gridCellRef,
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["GridPlacementValidationResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "valid",
                        "reason",
                        "origin",
                        "size",
                        "footprint",
                        "conflicts",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["valid"] = new { type = "boolean" },
                        ["reason"] = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "placement_available",
                                "hypergrid_reserved_band",
                                "region_overlap"
                            }
                        },
                        ["origin"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["grid_x"] = new { type = "integer" },
                                ["grid_y"] = new { type = "integer" },
                                ["world_x"] = new { type = "integer" },
                                ["world_y"] = new { type = "integer" }
                            }
                        },
                        ["size"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["size_x"] = new { type = "integer" },
                                ["size_y"] = new { type = "integer" },
                                ["cells_x"] = new { type = "integer" },
                                ["cells_y"] = new { type = "integer" }
                            }
                        },
                        ["footprint"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["min_x"] = new { type = "integer" },
                                ["max_x"] = new { type = "integer" },
                                ["min_y"] = new { type = "integer" },
                                ["max_y"] = new { type = "integer" },
                                ["min_world_x"] = new { type = "integer" },
                                ["max_world_x"] = new { type = "integer" },
                                ["min_world_y"] = new { type = "integer" },
                                ["max_world_y"] = new { type = "integer" }
                            }
                        },
                        ["conflicts"] = new
                        {
                            type = "array",
                            items = gridRegionRef
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["User"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "principal_id",
                        "first_name",
                        "last_name",
                        "username",
                        "display_name",
                        "is_display_name_default",
                        "user_level",
                        "active",
                        "account_state"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["principal_id"] = new { type = "string", format = "uuid" },
                        ["first_name"] = new { type = "string" },
                        ["last_name"] = new { type = "string" },
                        ["username"] = new { type = "string" },
                        ["display_name"] = new { type = "string", maxLength = 31 },
                        ["is_display_name_default"] = new { type = "boolean" },
                        ["display_name_changed"] = new { type = "integer", minimum = 0 },
                        ["display_name_next_update"] = new { type = "integer", minimum = 0 },
                        ["email"] = new { type = "string" },
                        ["user_level"] = new { type = "integer" },
                        ["user_flags"] = new { type = "integer" },
                        ["user_title"] = new { type = "string" },
                        ["user_country"] = new { type = "string" },
                        ["local_to_grid"] = new { type = "boolean" },
                        ["active"] = new { type = "boolean" },
                        ["account_state"] = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "active",
                                "locked",
                                "banned",
                                "deactivated",
                                "provisioning",
                                "provisioning_failed"
                            }
                        },
                        ["account_state_reason"] = new { type = "string" },
                        ["account_state_changed"] = new { type = "integer" },
                        ["created"] = new { type = "integer" }
                    }
                },
                ["UserCreateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "first_name",
                        "last_name",
                        "password",
                        "home_region"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["first_name"] = new { type = "string", maxLength = 64 },
                        ["last_name"] = new { type = "string", maxLength = 64 },
                        ["email"] = new { type = "string", maxLength = 64 },
                        ["password"] = new
                        {
                            type = "string",
                            minLength = 8,
                            maxLength = 256,
                            writeOnly = true
                        },
                        ["home_region"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["id"] = new { type = "string", format = "uuid" },
                                ["name"] = new { type = "string" },
                                ["position"] = SchemaRef("Position")
                            }
                        }
                    }
                },
                ["UserCreateResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "user",
                        "provisioning",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["user"] = userRef,
                        ["provisioning"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["ready"] = new { type = "boolean" },
                                ["authentication_initialized"] = new { type = "boolean" },
                                ["inventory_initialized"] = new { type = "boolean" },
                                ["home_initialized"] = new { type = "boolean" },
                                ["start_position_initialized"] = new { type = "boolean" },
                                ["state_finalized"] = new { type = "boolean" },
                                ["account_state"] = new { type = "string" },
                                ["home_region"] = regionRef
                            }
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["UserSearchResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer" },
                        ["users"] = new
                        {
                            type = "array",
                            items = userRef
                        },
                        ["pagination"] = paginationRef
                    }
                },
                ["RegionSearchResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer" },
                        ["regions"] = new
                        {
                            type = "array",
                            items = regionRef
                        },
                        ["pagination"] = paginationRef
                    }
                },
                ["StatisticsOnlineUser"] = new
                {
                    type = "object",
                    required = new[] { "type", "principal_id", "name", "region_id", "region_name" },
                    properties = new Dictionary<string, object>
                    {
                        ["type"] = new { type = "string", @enum = new[] { "resident", "hypergrid" } },
                        ["principal_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["home_grid"] = new { type = new[] { "string", "null" } },
                        ["region_id"] = new { type = new[] { "string", "null" }, format = "uuid" },
                        ["region_name"] = new { type = "string" },
                        ["login_at"] = new { type = new[] { "string", "null" }, format = "date-time" }
                    }
                },
                ["StatisticsSummaryResponse"] = new
                {
                    type = "object",
                    required = new[] { "generated_at", "detail_level", "protected_details", "residents", "hypergrid", "online", "regions", "correlation_id" },
                    properties = new Dictionary<string, object>
                    {
                        ["generated_at"] = new { type = "string", format = "date-time" },
                        ["detail_level"] = new { type = "string", @enum = new[] { "aggregate", "authenticated" } },
                        ["protected_details"] = new { type = "boolean" },
                        ["residents"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["registered_total"] = new { type = "integer" },
                                ["active_accounts"] = new { type = "integer" },
                                ["restricted_accounts"] = new { type = "integer" },
                                ["registrations_last_7_days"] = new { type = "integer" },
                                ["registrations_last_30_days"] = new { type = "integer" },
                                ["online_now"] = new { type = "integer" },
                                ["active_last_24_hours"] = new { type = "integer" },
                                ["active_last_7_days"] = new { type = "integer" },
                                ["active_last_30_days"] = new { type = "integer" },
                                ["never_logged_in"] = new { type = "integer" }
                            }
                        },
                        ["hypergrid"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["online_now"] = new { type = "integer" },
                                ["known_visitors_total"] = new { type = "integer" },
                                ["visitors_last_7_days"] = new { type = "integer" },
                                ["visitors_last_30_days"] = new { type = "integer" },
                                ["known_home_grids"] = new { type = "integer" },
                                ["home_grids_online_now"] = new { type = "integer" }
                            }
                        },
                        ["online"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["total"] = new { type = "integer" },
                                ["users"] = new
                                {
                                    type = "array",
                                    items = SchemaRef("StatisticsOnlineUser")
                                }
                            }
                        },
                        ["regions"] = new { type = "array", items = new { type = "object" } },
                        ["hypergrid_home_grids"] = new { type = "array", items = new { type = "object" } },
                        ["account_states"] = new { type = "object" },
                        ["data_quality"] = new { type = "object" },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["UserUpdateRequest"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["email"] = new { type = "string", maxLength = 64 },
                        ["display_name"] = new
                        {
                            type = "string",
                            maxLength = 31,
                            description = "Nicht eindeutiger Anzeigename. Leerer Wert setzt auf den Standardnamen zurück. Selbständerungen unterliegen einer Sieben-Tage-Sperre; Administratoren können diese übersteuern."
                        },
                        ["user_title"] = new { type = "string", maxLength = 64 },
                        ["user_country"] = new { type = "string", maxLength = 64 }
                    }
                },
                ["AccountStateRequest"] = new
                {
                    type = "object",
                    required = new[] { "state" },
                    properties = new Dictionary<string, object>
                    {
                        ["state"] = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "active",
                                "locked",
                                "banned",
                                "deactivated"
                            }
                        },
                        ["reason"] = new { type = "string", maxLength = 255 }
                    }
                },
                ["UserLevelRequest"] = new
                {
                    type = "object",
                    required = new[] { "user_level" },
                    properties = new Dictionary<string, object>
                    {
                        ["user_level"] = new { type = "integer" }
                    }
                },
                ["ResidentSessionRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "username",
                        "password"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["username"] = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 129,
                            description = "Kanonischer NexVerse-Einwohner-Anmeldename, zum Beispiel Antonia.Porta oder eine einteilige Resident-Anmeldung."
                        },
                        ["password"] = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 256,
                            writeOnly = true
                        }
                    }
                },
                ["ResidentSessionResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "access_token",
                        "token_type",
                        "expires_in",
                        "scope",
                        "principal_id",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["access_token"] = new
                        {
                            type = "string",
                            description = "Natives, an den Kontolebenszyklus gebundenes NexVerse-Bearer-Token."
                        },
                        ["token_type"] = new
                        {
                            type = "string",
                            @enum = new[] { "Bearer" }
                        },
                        ["expires_in"] = new { type = "integer" },
                        ["scope"] = new { type = "string" },
                        ["principal_id"] = new
                        {
                            type = "string",
                            format = "uuid"
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["PasswordRequest"] = new
                {
                    type = "object",
                    required = new[] { "password" },
                    properties = new Dictionary<string, object>
                    {
                        ["password"] = new
                        {
                            type = "string",
                            minLength = 8,
                            maxLength = 256,
                            writeOnly = true
                        }
                    }
                },
                ["ApiKey"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["key_id"] = new { type = "string" },
                        ["name"] = new { type = "string" },
                        ["scopes"] = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        ["enabled"] = new { type = "boolean" },
                        ["created_at"] = new { type = "integer" },
                        ["updated_at"] = new { type = "integer" }
                    }
                },
                ["ApiKeyCreateRequest"] = new
                {
                    type = "object",
                    required = new[] { "scopes" },
                    properties = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "string" },
                        ["scopes"] = new
                        {
                            type = "array",
                            minItems = 1,
                            items = new { type = "string" }
                        }
                    }
                },
                ["ApiKeyCreateResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["api_key"] = new
                        {
                            type = "string",
                            writeOnly = true
                        },
                        ["api_key_note"] = new { type = "string" },
                        ["key"] = apiKeyRef,
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["ApiKeyStateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "key_id",
                        "enabled"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["key_id"] = new { type = "string" },
                        ["enabled"] = new { type = "boolean" }
                    }
                },
                ["ApiKeyListResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["keys"] = new
                        {
                            type = "array",
                            items = apiKeyRef
                        }
                    }
                },
                ["AuditEvent"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["event_id"] = new { type = "string", format = "uuid" },
                        ["timestamp"] = new { type = "string", format = "date-time" },
                        ["actor"] = new { type = "string" },
                        ["action"] = new { type = "string" },
                        ["resource"] = new { type = "string" },
                        ["correlation_id"] = new { type = "string" },
                        ["details"] = new
                        {
                            type = "object",
                            additionalProperties = new
                            {
                                type = "string"
                            }
                        }
                    }
                },
                ["AuditSearchResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer" },
                        ["events"] = new
                        {
                            type = "array",
                            items = auditEventRef
                        },
                        ["pagination"] = paginationRef
                    }
                }
            };
        }

        private static void WriteJson(IOSHttpResponse response, object payload)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_JsonOptions);
        }
    }
}
