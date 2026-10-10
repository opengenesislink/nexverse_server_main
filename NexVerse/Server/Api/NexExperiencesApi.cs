// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.Experiences;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexExperiencesApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly NexApiAuthenticator m_Authenticator;
        private readonly IUserAccountService m_Users;
        private readonly INexAuditSink m_Audit;
        private readonly NexExperienceStore m_Store;

        public NexExperiencesApi(
            NexApiAuthenticator authenticator,
            IUserAccountService users,
            INexAuditSink audit,
            NexExperienceStore store)
        {
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_Users = users ?? throw new ArgumentNullException(nameof(users));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
            string method = request?.HttpMethod ?? string.Empty;

            if (path == "/api/v1/experiences")
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.ExperiencesRead, out _, out _))
                        return;

                    try
                    {
                        string query = request?.QueryString?["q"] ?? string.Empty;
                        int offset = QueryInt(request, "offset", 0, 0, 1000000);
                        int limit = QueryInt(request, "limit", 100, 1, 500);

                        WriteJson(response, new
                        {
                            experiences = m_Store.Search(query, offset, limit).Select(Payload).ToArray(),
                            offset,
                            limit,
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }

                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                        return;

                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        // The experiences:manage scope alone must not turn
                        // a service API key into an arbitrary resident identity.
                        // Only admin:* service principals may supply owner_id.
                        if (account == null && !principal.HasScope(NexScopes.AdminAll))
                            throw new UnauthorizedAccessException(
                                "Only admin:* service principals may create experiences for a resident.");

                        Guid owner =
                            account != null
                                ? account.PrincipalID.Guid
                                : BodyGuid(body, "owner_id");

                        if (account != null &&
                            body.TryGetProperty("owner_id", out JsonElement ownerElement) &&
                            ownerElement.ValueKind == JsonValueKind.String &&
                            Guid.TryParse(ownerElement.GetString(), out Guid requestedOwner) &&
                            requestedOwner != owner &&
                            !principal.HasScope(NexScopes.AdminAll))
                        {
                            throw new UnauthorizedAccessException("Resident tokens may create only self-owned experiences.");
                        }

                        Guid explicitId =
                            BodyOptionalGuid(
                                body,
                                "experience_id");

                        NexExperience created =
                            m_Store.Create(
                                owner,
                                BodyOptionalGuid(body, "group_id"),
                                BodyString(body, "name"),
                                BodyString(body, "description"),
                                ParseMaturity(BodyString(body, "maturity")),
                                principal.Subject,
                                explicitId == Guid.Empty
                                    ? null
                                    : explicitId);

                        Audit(principal, "experience.created", created.ExperienceId, response);

                        WriteJson(response, new
                        {
                            experience = Payload(created),
                            correlation_id = Correlation(response)
                        }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }

                    return;
                }

                MethodNotAllowed(response, "GET or POST");
                return;
            }

            // Internal viewer discovery, reached through the simulator's
            // authenticated API key, never directly advertised to Firestorm.
            // Exposes only public metadata (NOT resident ACLs, staff roles,
            // K/V data or administrative service credentials).
            // Bounded bulk metadata endpoint for a trusted simulator. The
            // Firestorm viewer receives only the safe subset constructed by
            // its per-agent capability, never the API key or full ACL payload.
            if (path == "/api/v1/experiences/script/info")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;
                try
                {
                    string ids = request?.QueryString?["ids"] ?? string.Empty;
                    if (ids.Length > 4000)
                        throw new ArgumentException("Experience id list is too long.");
                    string[] tokens = ids.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length == 0 || tokens.Length > 64)
                        throw new ArgumentException("A bounded list of experience IDs is required.");
                    List<object> found = new();
                    List<string> missing = new();
                    HashSet<Guid> seen = new();
                    foreach (string token in tokens)
                    {
                        if (!Guid.TryParse(token, out Guid id) || id == Guid.Empty)
                            throw new ArgumentException("Invalid experience UUID.");
                        if (!seen.Add(id))
                            continue;
                        NexExperience e = m_Store.Get(id);
                        if (e == null)
                        {
                            missing.Add(id.ToString("D"));
                            continue;
                        }
                        found.Add(new
                        {
                            experience_id = e.ExperienceId.ToString("D"),
                            name = e.Name,
                            description = e.Description,
                            owner_id = e.OwnerId.ToString("D"),
                            group_id = e.GroupId.ToString("D"),
                            maturity = (int)e.Maturity,
                            enabled = e.Enabled
                        });
                    }
                    WriteJson(response, new
                    {
                        experiences = found,
                        error_ids = missing,
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (path == "/api/v1/experiences/script/search")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;
                try
                {
                    string query = request?.QueryString?["q"] ?? string.Empty;
                    int offset = QueryInt(request, "offset", 0, 0, 1000000);
                    int limit = QueryInt(request, "limit", 20, 1, 50);
                    WriteJson(response, new
                    {
                        experiences = m_Store.Search(query, offset, limit).Select(e => new
                        {
                            experience_id = e.ExperienceId.ToString("D"),
                            name = e.Name,
                            description = e.Description,
                            owner_id = e.OwnerId.ToString("D"),
                            group_id = e.GroupId.ToString("D"),
                            maturity = (int)e.Maturity,
                            enabled = e.Enabled
                        }).ToArray(),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (path == "/api/v1/experiences/script/resolve")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;

                try
                {
                    Guid script = QueryGuid(request, "script_id");
                    Guid experience = m_Store.ResolveScript(script);
                    WriteJson(response, new
                    {
                        script_id = script.ToString("D"),
                        experience_id = experience == Guid.Empty ? null : experience.ToString("D"),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (path == "/api/v1/experiences/script/details")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;

                try
                {
                    Guid experience =
                        QueryOptionalGuid(request, "experience_id");

                    if (experience == Guid.Empty)
                    {
                        Guid script =
                            QueryGuid(request, "script_id");
                        experience =
                            m_Store.ResolveScript(script);
                    }

                    NexExperience details =
                        experience == Guid.Empty
                            ? null
                            : m_Store.Get(experience);

                    if (details == null)
                        throw new KeyNotFoundException("Experience was not found.");

                    WriteJson(response, new
                    {
                        experience = Payload(details),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (path == "/api/v1/experiences/script/permission")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;

                try
                {
                    Guid script = QueryGuid(request, "script_id");
                    Guid resident = QueryGuid(request, "resident_id");
                    Guid experience = m_Store.ResolveScript(script);
                    NexExperiencePermissionStatus status =
                        experience == Guid.Empty
                            ? NexExperiencePermissionStatus.None
                            : m_Store.GetResidentPermission(experience, resident);

                    WriteJson(response, new
                    {
                        script_id = script.ToString("D"),
                        experience_id = experience == Guid.Empty ? null : experience.ToString("D"),
                        resident_id = resident.ToString("D"),
                        status = status.ToString().ToLowerInvariant(),
                        allowed = status == NexExperiencePermissionStatus.Allowed,
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (path == "/api/v1/experiences/script/location")
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;

                try
                {
                    Guid script = QueryGuid(request, "script_id");
                    Guid experience = m_Store.ResolveScript(script);
                    Guid estate = QueryOptionalGuid(request, "estate_id");
                    Guid parcel = QueryOptionalGuid(request, "parcel_id");

                    WriteJson(response, new
                    {
                        experience_id = experience == Guid.Empty ? null : experience.ToString("D"),
                        allowed = experience != Guid.Empty && m_Store.IsLocationAllowed(experience, estate, parcel),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (path == "/api/v1/experiences/script/kv")
            {
                if (!RequireMethod(method, "POST", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesScript, out _, out _))
                    return;
                if (!TryBody(request, response, out JsonElement body))
                    return;

                try
                {
                    Guid script = BodyGuid(body, "script_id");
                    Guid experience = m_Store.ResolveScript(script);
                    if (experience == Guid.Empty)
                        throw new InvalidOperationException("Script is not bound to an experience.");

                    string operation = BodyString(body, "operation").ToLowerInvariant();
                    string key = BodyString(body, "key");
                    string value = BodyRawString(body, "value");
                    bool success = true;
                    string code = string.Empty;
                    object result = string.Empty;
                    string message = string.Empty;

                    switch (operation)
                    {
                        case "create":
                            success = m_Store.CreateKeyValue(experience, script, key, value);
                            if (!success)
                            {
                                code = "storage_exception";
                                message = "Key already exists.";
                            }
                            else
                            {
                                result = value;
                            }
                            break;

                        case "read":
                            success = m_Store.TryReadKeyValue(experience, script, key, out string readValue);
                            if (success)
                                result = readValue;
                            else
                            {
                                code = "key_not_found";
                                message = "Key does not exist.";
                            }
                            break;

                        case "update":
                            success = m_Store.UpdateKeyValue(
                                experience,
                                script,
                                key,
                                value,
                                BodyBool(body, "check_original", false),
                                BodyRawString(body, "original_value"),
                                out bool retryMismatch);
                            if (success)
                                result = value;
                            else if (retryMismatch)
                            {
                                code = "retry_update";
                                message = "Checked update failed because the stored value changed.";
                            }
                            break;

                        case "delete":
                            success = m_Store.DeleteKeyValue(experience, script, key);
                            if (!success)
                            {
                                code = "key_not_found";
                                message = "Key does not exist.";
                            }
                            break;

                        case "stats":
                            result = new
                            {
                                used_bytes = m_Store.GetDataSize(experience, script),
                                quota_bytes = NexExperienceStore.MaxStoreBytes,
                                key_count = m_Store.GetKeyCount(experience, script)
                            };
                            break;

                        case "keys":
                            int start = BodyNonNegativeInt(body, "start", 0);
                            int count = BodyPositiveInt(body, "count", 100);
                            IReadOnlyList<string> keys =
                                m_Store.ListKeys(
                                    experience,
                                    script,
                                    start,
                                    count);
                            if (keys.Count == 0 &&
                                start >= m_Store.GetKeyCount(experience, script) &&
                                m_Store.GetKeyCount(experience, script) > 0)
                            {
                                success = false;
                                code = "key_not_found";
                                message = "Key index is outside the stored key range.";
                            }
                            result = new { keys };
                            break;

                        default:
                            throw new ArgumentException("operation must be create, read, update, delete, stats or keys.");
                    }

                    WriteJson(response, new
                    {
                        success,
                        code,
                        message,
                        result,
                        experience_id = experience.ToString("D"),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            const string prefix = "/api/v1/experiences/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                NotFound(response);
                return;
            }

            string relative = path.Substring(prefix.Length);
            string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 ||
                !Guid.TryParse(parts[0], out Guid experienceId) ||
                experienceId == Guid.Empty)
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_experience_id", "A valid experience UUID is required.");
                return;
            }

            if (parts.Length == 1)
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.ExperiencesRead, out _, out _))
                        return;

                    try
                    {
                        NexExperience experience = m_Store.Get(experienceId) ??
                            throw new KeyNotFoundException("Experience was not found.");
                        WriteJson(response, new
                        {
                            experience = Payload(experience),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                        return;
                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        Guid actor = ActorId(principal, account, body);
                        NexExperience current = m_Store.Get(experienceId) ??
                            throw new KeyNotFoundException("Experience was not found.");

                        bool enabled =
                            body.TryGetProperty(
                                "enabled",
                                out JsonElement enabledElement)
                                ? enabledElement.ValueKind == JsonValueKind.True
                                    ? true
                                    : enabledElement.ValueKind == JsonValueKind.False
                                        ? false
                                        : throw new ArgumentException("enabled must be boolean.")
                                : current.Enabled;

                        NexExperience updated =
                            m_Store.UpdateProfile(
                                experienceId,
                                actor,
                                BodyString(body, "name").Length == 0 ? current.Name : BodyString(body, "name"),
                                body.TryGetProperty("description", out _) ? BodyString(body, "description") : current.Description,
                                body.TryGetProperty("group_id", out _) ? BodyOptionalGuid(body, "group_id") : current.GroupId,
                                body.TryGetProperty("maturity", out _) ? ParseMaturity(BodyString(body, "maturity")) : current.Maturity,
                                enabled,
                                principal.Subject);

                        Audit(principal, "experience.updated", experienceId, response);

                        WriteJson(response, new
                        {
                            experience = Payload(updated),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                        return;

                    try
                    {
                        if (account == null && !principal.HasScope(NexScopes.AdminAll))
                            throw new UnauthorizedAccessException(
                                "Only admin:* service principals may delete experiences as a resident.");
                        Guid actor =
                            account != null
                                ? account.PrincipalID.Guid
                                : QueryGuid(request, "actor_id");

                        m_Store.Delete(experienceId, actor, principal.Subject);
                        Audit(principal, "experience.deleted", experienceId, response);
                        WriteJson(response, new { deleted = true, correlation_id = Correlation(response) });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                MethodNotAllowed(response, "GET, PUT or DELETE");
                return;
            }

            if (parts.Length == 2 && parts[1].Equals("logs", StringComparison.OrdinalIgnoreCase))
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesRead, out _, out _))
                    return;

                try
                {
                    WriteJson(response, new
                    {
                        logs = m_Store.GetLogs(
                            experienceId,
                            QueryInt(request, "offset", 0, 0, 1000000),
                            QueryInt(request, "limit", 100, 1, 500)),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (parts.Length == 4 &&
                parts[1].Equals("roles", StringComparison.OrdinalIgnoreCase))
            {
                if (!RequireMethod(method, "PUT", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                    return;
                if (!Guid.TryParse(parts[3], out Guid resident) || resident == Guid.Empty)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_resident_id", "A valid resident UUID is required.");
                    return;
                }
                if (!TryBody(request, response, out JsonElement body))
                    return;

                try
                {
                    NexExperience updated = m_Store.SetRole(
                        experienceId,
                        ActorId(principal, account, body),
                        resident,
                        parts[2],
                        BodyBool(body, "enabled", true),
                        principal.Subject);

                    Audit(principal, "experience.role.updated", experienceId, response);
                    WriteJson(response, new { experience = Payload(updated), correlation_id = Correlation(response) });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (parts.Length == 3 &&
                parts[1].Equals("residents", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(parts[2], out Guid permissionResident))
            {
                if (!RequireMethod(method, "PUT", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                    return;
                if (!TryBody(request, response, out JsonElement body))
                    return;

                try
                {
                    string raw = BodyString(body, "status");
                    if (!Enum.TryParse(raw, true, out NexExperiencePermissionStatus status) ||
                        !Enum.IsDefined(typeof(NexExperiencePermissionStatus), status))
                    {
                        throw new ArgumentException("status must be none, allowed or blocked.");
                    }

                    NexExperience updated = m_Store.SetResidentPermission(
                        experienceId,
                        ActorId(principal, account, body),
                        permissionResident,
                        status,
                        principal.Subject);

                    Audit(principal, "experience.permission.updated", experienceId, response);
                    WriteJson(response, new { experience = Payload(updated), correlation_id = Correlation(response) });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (parts.Length == 4 &&
                parts[1].Equals("policies", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(parts[3], out Guid location))
            {
                if (!RequireMethod(method, "PUT", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                    return;
                if (!TryBody(request, response, out JsonElement body))
                    return;

                try
                {
                    NexExperience updated = m_Store.SetLocationPolicy(
                        experienceId,
                        ActorId(principal, account, body),
                        parts[2],
                        location,
                        BodyBool(body, "allowed", true),
                        BodyBool(body, "enabled", true),
                        principal.Subject);

                    Audit(principal, "experience.policy.updated", experienceId, response);
                    WriteJson(response, new { experience = Payload(updated), correlation_id = Correlation(response) });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (parts.Length == 4 &&
                parts[1].Equals("scripts", StringComparison.OrdinalIgnoreCase) &&
                parts[3].Equals("bind", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(parts[2], out Guid scriptId))
            {
                if (!RequireMethod(method, "POST", response))
                    return;
                if (!Authenticate(request, response, NexScopes.ExperiencesManage, out NexPrincipal principal, out UserAccount account))
                    return;
                if (!TryBody(request, response, out JsonElement body))
                    return;

                try
                {
                    m_Store.BindScript(
                        experienceId,
                        ActorId(principal, account, body),
                        scriptId,
                        principal.Subject);

                    Audit(principal, "experience.script.bound", experienceId, response);
                    WriteJson(response, new
                    {
                        bound = true,
                        experience_id = experienceId.ToString("D"),
                        script_id = scriptId.ToString("D"),
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            NotFound(response);
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string scope,
            out NexPrincipal principal,
            out UserAccount account)
        {
            if (m_Authenticator.TryAuthenticate(
                request,
                scope,
                out principal,
                out account,
                out int statusCode,
                out string error))
            {
                return true;
            }

            response.AddHeader("WWW-Authenticate", "Bearer");
            WriteError(response, (HttpStatusCode)statusCode, error, "Authentication or " + scope + " authorization is required.");
            return false;
        }

        private static Guid ActorId(
            NexPrincipal principal,
            UserAccount account,
            JsonElement body)
        {
            if (account != null)
                return account.PrincipalID.Guid;

            // A scoped service key has no resident identity. Do not allow an
            // arbitrary client-supplied actor_id to impersonate the Experience
            // owner. Explicit admin:* credentials are required for delegation.
            if (!principal.HasScope(NexScopes.AdminAll))
                throw new UnauthorizedAccessException(
                    "Experience management as another resident requires admin:*.");
            Guid actor = BodyOptionalGuid(body, "actor_id");
            if (actor == Guid.Empty)
                throw new ArgumentException(
                    "actor_id is required for administrator/service management calls.");
            return actor;
        }

        private void Audit(
            NexPrincipal principal,
            string action,
            Guid experienceId,
            IOSHttpResponse response)
        {
            m_Audit.Record(new NexAuditEvent(
                principal.Subject,
                action,
                "experience:" + experienceId.ToString("D"),
                Correlation(response),
                new Dictionary<string, string>()));
        }

        private static object Payload(NexExperience e) =>
            new
            {
                experience_id = e.ExperienceId.ToString("D"),
                name = e.Name,
                description = e.Description,
                owner_id = e.OwnerId.ToString("D"),
                group_id = e.GroupId == Guid.Empty ? null : e.GroupId.ToString("D"),
                maturity = e.Maturity.ToString().ToLowerInvariant(),
                enabled = e.Enabled,
                admins = e.Admins.Select(x => x.ToString("D")).ToArray(),
                contributors = e.Contributors.Select(x => x.ToString("D")).ToArray(),
                allowed_residents = e.AllowedResidents.Select(x => x.ToString("D")).ToArray(),
                blocked_residents = e.BlockedResidents.Select(x => x.ToString("D")).ToArray(),
                allowed_estates = e.AllowedEstates.Select(x => x.ToString("D")).ToArray(),
                blocked_estates = e.BlockedEstates.Select(x => x.ToString("D")).ToArray(),
                allowed_parcels = e.AllowedParcels.Select(x => x.ToString("D")).ToArray(),
                blocked_parcels = e.BlockedParcels.Select(x => x.ToString("D")).ToArray(),
                key_value_count = e.KeyValues.Count,
                created_at = e.CreatedAt,
                updated_at = e.UpdatedAt
            };

        private static NexExperienceMaturity ParseMaturity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return NexExperienceMaturity.General;

            if (!Enum.TryParse(value, true, out NexExperienceMaturity maturity) ||
                !Enum.IsDefined(typeof(NexExperienceMaturity), maturity))
            {
                throw new ArgumentException("maturity must be general, moderate or adult.");
            }

            return maturity;
        }

        private static bool TryBody(IOSHttpRequest request, IOSHttpResponse response, out JsonElement body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(request.InputStream);
                body = document.RootElement.Clone();
                if (body.ValueKind == JsonValueKind.Object)
                    return true;
            }
            catch
            {
            }

            body = default;
            WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "A valid JSON object is required.");
            return false;
        }

        private static string BodyString(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement e) || e.ValueKind != JsonValueKind.String)
                return string.Empty;
            return (e.GetString() ?? string.Empty).Trim();
        }

        private static string BodyRawString(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement e) ||
                e.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return e.GetString() ?? string.Empty;
        }

        private static Guid BodyGuid(JsonElement body, string name)
        {
            Guid id = BodyOptionalGuid(body, name);
            if (id == Guid.Empty)
                throw new ArgumentException(name + " must be a non-zero UUID.");
            return id;
        }

        private static Guid BodyOptionalGuid(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement e) || e.ValueKind == JsonValueKind.Null)
                return Guid.Empty;
            if (e.ValueKind != JsonValueKind.String || !Guid.TryParse(e.GetString(), out Guid id))
                throw new ArgumentException(name + " must be a UUID.");
            return id;
        }

        private static bool BodyBool(JsonElement body, string name, bool defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement e))
                return defaultValue;
            if (e.ValueKind == JsonValueKind.True)
                return true;
            if (e.ValueKind == JsonValueKind.False)
                return false;
            throw new ArgumentException(name + " must be boolean.");
        }

        private static int BodyNonNegativeInt(
            JsonElement body,
            string name,
            int defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement element))
                return defaultValue;
            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt32(out int value) ||
                value < 0)
            {
                throw new ArgumentException(name + " must be a non-negative integer.");
            }
            return value;
        }

        private static int BodyPositiveInt(
            JsonElement body,
            string name,
            int defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement element))
                return defaultValue;
            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt32(out int value) ||
                value < 1)
            {
                throw new ArgumentException(name + " must be a positive integer.");
            }
            return value;
        }

        private static int QueryInt(IOSHttpRequest request, string name, int defaultValue, int min, int max)
        {
            string raw = (request?.QueryString?[name] ?? string.Empty).Trim();
            if (raw.Length == 0)
                return defaultValue;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(name);
            }
            return value;
        }

        private static Guid QueryGuid(IOSHttpRequest request, string name)
        {
            Guid id = QueryOptionalGuid(request, name);
            if (id == Guid.Empty)
                throw new ArgumentException(name + " must be a non-zero UUID.");
            return id;
        }

        private static Guid QueryOptionalGuid(IOSHttpRequest request, string name)
        {
            string raw = (request?.QueryString?[name] ?? string.Empty).Trim();
            if (raw.Length == 0)
                return Guid.Empty;
            if (!Guid.TryParse(raw, out Guid id))
                throw new ArgumentException(name + " must be a UUID.");
            return id;
        }

        private static bool RequireMethod(string actual, string expected, IOSHttpResponse response)
        {
            if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;
            MethodNotAllowed(response, expected);
            return false;
        }

        private static string Correlation(IOSHttpResponse response) =>
            NexApiRequestContext.Ensure(response);

        private static void WriteFailure(IOSHttpResponse response, Exception e)
        {
            if (e is UnauthorizedAccessException)
            {
                WriteError(response, HttpStatusCode.Forbidden, "experience_forbidden", e.Message);
                return;
            }
            if (e is KeyNotFoundException)
            {
                WriteError(response, HttpStatusCode.NotFound, "experience_not_found", e.Message);
                return;
            }
            if (e is ArgumentException || e is InvalidOperationException)
            {
                WriteError(response, HttpStatusCode.BadRequest, "experience_validation_failed", e.Message);
                return;
            }

            WriteError(response, HttpStatusCode.InternalServerError, "experience_internal_error", "The experience operation failed.");
        }

        private static void MethodNotAllowed(IOSHttpResponse response, string expected) =>
            WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", expected + " is required.");

        private static void NotFound(IOSHttpResponse response) =>
            WriteError(response, HttpStatusCode.NotFound, "not_found", "The requested experience endpoint was not found.");

        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string error, string message) =>
            WriteJson(response, new
            {
                error,
                message,
                correlation_id = Correlation(response)
            }, status);

        private static void WriteJson(IOSHttpResponse response, object payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_Json);
        }
    }
}
