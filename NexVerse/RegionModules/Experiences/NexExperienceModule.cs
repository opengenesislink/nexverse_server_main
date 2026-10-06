// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Experiences
{
    [Extension(
        Path = "/OpenSim/RegionModules",
        NodeName = "RegionModule",
        Id = "NexExperienceModule")]
    public sealed class NexExperienceModule :
        IExperienceModule,
        ISharedRegionModule
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexExperienceModule));

        private readonly object m_Sync = new object();
        private readonly List<Scene> m_Scenes = new List<Scene>();

        private bool m_Enabled;
        private string m_WorldApiBaseUrl = string.Empty;
        private string m_ApiKey = string.Empty;
        private int m_RequestTimeoutMilliseconds = 3000;
        private HttpClient m_Http;

        public string Name => "NexExperienceModule";
        public Type ReplaceableInterface => typeof(IExperienceModule);

        public void Initialise(IConfigSource config)
        {
            IConfig section =
                config?.Configs["NexExperiencesViewer"];

            if (section == null ||
                !section.GetBoolean("Enabled", false))
            {
                m_Enabled = false;
                return;
            }

            m_WorldApiBaseUrl =
                section.GetString("WorldApiBaseUrl", string.Empty)
                    .Trim()
                    .TrimEnd('/');

            m_ApiKey =
                section.GetString("ApiKey", string.Empty)
                    .Trim();

            m_RequestTimeoutMilliseconds =
                Math.Clamp(
                    section.GetInt("RequestTimeoutMilliseconds", 3000),
                    500,
                    30000);

            if (!Uri.TryCreate(
                    m_WorldApiBaseUrl,
                    UriKind.Absolute,
                    out Uri baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp &&
                 baseUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "[NEX-EXPERIENCES]: WorldApiBaseUrl must be an absolute HTTP(S) URL.");
            }

            if (string.IsNullOrWhiteSpace(m_ApiKey))
                throw new InvalidOperationException(
                    "[NEX-EXPERIENCES]: ApiKey is required when enabled.");

            m_Http =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromMilliseconds(
                            m_RequestTimeoutMilliseconds)
                };

            m_Enabled = true;

            m_Log.InfoFormat(
                "[NEX-EXPERIENCES]: Central Experience adapter enabled for {0}.",
                m_WorldApiBaseUrl);
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            lock (m_Sync)
                m_Scenes.Add(scene);

            scene.RegisterModuleInterface<IExperienceModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null)
                return;

            scene.UnregisterModuleInterface<IExperienceModule>(this);

            lock (m_Sync)
                m_Scenes.Remove(scene);
        }

        public void Close()
        {
            lock (m_Sync)
                m_Scenes.Clear();

            m_Http?.Dispose();
            m_Http = null;
        }

        public UUID ResolveExperience(UUID scriptItemId)
        {
            if (!m_Enabled || scriptItemId.IsZero())
                return UUID.Zero;

            try
            {
                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/experiences/script/resolve?script_id=" +
                        Uri.EscapeDataString(scriptItemId.ToString()));

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (!response.IsSuccessStatusCode)
                    return UUID.Zero;

                using JsonDocument document =
                    JsonDocument.Parse(
                        response.Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult());

                if (!document.RootElement.TryGetProperty(
                        "experience_id",
                        out JsonElement value) ||
                    value.ValueKind != JsonValueKind.String ||
                    !UUID.TryParse(value.GetString(), out UUID experienceId))
                {
                    return UUID.Zero;
                }

                return experienceId;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Resolve for script {0} failed: {1}",
                    scriptItemId,
                    e.Message);
                return UUID.Zero;
            }
        }

        public bool TryGetExperienceDetails(
            UUID experienceId,
            out string name,
            out UUID ownerId,
            out UUID groupId,
            out int maturity,
            out bool enabled)
        {
            name = string.Empty;
            ownerId = UUID.Zero;
            groupId = UUID.Zero;
            maturity = 0;
            enabled = false;

            if (!m_Enabled || experienceId.IsZero())
                return false;

            try
            {
                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/experiences/script/details?experience_id=" +
                        Uri.EscapeDataString(
                            experienceId.ToString()));

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (!response.IsSuccessStatusCode)
                    return false;

                using JsonDocument document =
                    JsonDocument.Parse(
                        response.Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult());

                if (!document.RootElement.TryGetProperty(
                        "experience",
                        out JsonElement experience))
                {
                    return false;
                }

                name =
                    experience.TryGetProperty("name", out JsonElement nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                if (experience.TryGetProperty("owner_id", out JsonElement ownerElement) &&
                    ownerElement.ValueKind == JsonValueKind.String)
                {
                    UUID.TryParse(ownerElement.GetString(), out ownerId);
                }

                if (experience.TryGetProperty("group_id", out JsonElement groupElement) &&
                    groupElement.ValueKind == JsonValueKind.String)
                {
                    UUID.TryParse(groupElement.GetString(), out groupId);
                }

                string maturityName =
                    experience.TryGetProperty("maturity", out JsonElement maturityElement) &&
                    maturityElement.ValueKind == JsonValueKind.String
                        ? maturityElement.GetString() ?? "general"
                        : "general";

                maturity =
                    maturityName.Equals("adult", StringComparison.OrdinalIgnoreCase)
                        ? 2
                        : maturityName.Equals("moderate", StringComparison.OrdinalIgnoreCase)
                            ? 1
                            : 0;

                enabled =
                    experience.TryGetProperty("enabled", out JsonElement enabledElement) &&
                    enabledElement.ValueKind == JsonValueKind.True;

                return true;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Details for {0} failed: {1}",
                    experienceId,
                    e.Message);
                return false;
            }
        }

        public bool HasExperiencePermission(
            UUID scriptItemId,
            UUID residentId,
            UUID parcelId,
            out UUID experienceId,
            out string reason)
        {
            experienceId = UUID.Zero;
            reason = string.Empty;

            if (!m_Enabled)
            {
                reason = "Experience service is disabled.";
                return false;
            }

            if (scriptItemId.IsZero() || residentId.IsZero())
            {
                reason = "Script and resident UUID are required.";
                return false;
            }

            try
            {
                using HttpRequestMessage permissionRequest =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/experiences/script/permission?script_id=" +
                        Uri.EscapeDataString(scriptItemId.ToString()) +
                        "&resident_id=" +
                        Uri.EscapeDataString(residentId.ToString()));

                using HttpResponseMessage permissionResponse =
                    m_Http.Send(permissionRequest);

                if (!permissionResponse.IsSuccessStatusCode)
                {
                    reason = ReadErrorMessage(permissionResponse);
                    return false;
                }

                using JsonDocument permissionDocument =
                    JsonDocument.Parse(
                        permissionResponse.Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult());

                JsonElement root =
                    permissionDocument.RootElement;

                if (root.TryGetProperty("experience_id", out JsonElement experienceElement) &&
                    experienceElement.ValueKind == JsonValueKind.String)
                {
                    UUID.TryParse(
                        experienceElement.GetString(),
                        out experienceId);
                }

                bool residentAllowed =
                    root.TryGetProperty("allowed", out JsonElement allowedElement) &&
                    allowedElement.ValueKind == JsonValueKind.True;

                if (!residentAllowed)
                {
                    reason = "Resident has not granted this experience.";
                    return false;
                }

                if (!parcelId.IsZero())
                {
                    using HttpRequestMessage locationRequest =
                        CreateRequest(
                            HttpMethod.Get,
                            "/api/v1/experiences/script/location?script_id=" +
                            Uri.EscapeDataString(scriptItemId.ToString()) +
                            "&parcel_id=" +
                            Uri.EscapeDataString(parcelId.ToString()));

                    using HttpResponseMessage locationResponse =
                        m_Http.Send(locationRequest);

                    if (!locationResponse.IsSuccessStatusCode)
                    {
                        reason = ReadErrorMessage(locationResponse);
                        return false;
                    }

                    using JsonDocument locationDocument =
                        JsonDocument.Parse(
                            locationResponse.Content
                                .ReadAsStringAsync()
                                .GetAwaiter()
                                .GetResult());

                    bool locationAllowed =
                        locationDocument.RootElement.TryGetProperty(
                            "allowed",
                            out JsonElement locationAllowedElement) &&
                        locationAllowedElement.ValueKind == JsonValueKind.True;

                    if (!locationAllowed)
                    {
                        reason = "Experience is blocked by parcel/estate policy.";
                        return false;
                    }
                }

                return !experienceId.IsZero();
            }
            catch (Exception e)
            {
                reason = "Experience permission check failed.";
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Permission for script {0}/resident {1} failed: {2}",
                    scriptItemId,
                    residentId,
                    e.Message);
                return false;
            }
        }

        public bool AgentInExperience(
            UUID scriptItemId,
            UUID residentId) =>
            HasExperiencePermission(
                scriptItemId,
                residentId,
                UUID.Zero,
                out _,
                out _);

        public bool CreateKeyValue(
            UUID scriptItemId,
            string key,
            string value,
            out string error) =>
            KeyValue(
                scriptItemId,
                "create",
                key,
                value,
                false,
                string.Empty,
                0,
                0,
                out _,
                out _,
                out error);

        public bool ReadKeyValue(
            UUID scriptItemId,
            string key,
            out string value,
            out string error) =>
            KeyValue(
                scriptItemId,
                "read",
                key,
                string.Empty,
                false,
                string.Empty,
                0,
                0,
                out value,
                out _,
                out error);

        public bool UpdateKeyValue(
            UUID scriptItemId,
            string key,
            string value,
            bool checkOriginal,
            string originalValue,
            out bool retryMismatch,
            out string error)
        {
            retryMismatch = false;

            bool success =
                KeyValue(
                    scriptItemId,
                    "update",
                    key,
                    value,
                    checkOriginal,
                    originalValue,
                    0,
                    0,
                    out _,
                    out string code,
                    out error);

            retryMismatch =
                string.Equals(
                    code,
                    "retry_update",
                    StringComparison.OrdinalIgnoreCase);

            return success;
        }

        public bool DeleteKeyValue(
            UUID scriptItemId,
            string key,
            out string error) =>
            KeyValue(
                scriptItemId,
                "delete",
                key,
                string.Empty,
                false,
                string.Empty,
                0,
                0,
                out _,
                out _,
                out error);

        public bool GetKeyValueStats(
            UUID scriptItemId,
            out long usedBytes,
            out long quotaBytes,
            out int keyCount,
            out string error)
        {
            usedBytes = 0;
            quotaBytes = 0;
            keyCount = 0;

            if (!KeyValue(
                    scriptItemId,
                    "stats",
                    string.Empty,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    out string result,
                    out _,
                    out error))
            {
                return false;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(result);

                JsonElement root = document.RootElement;
                usedBytes =
                    root.TryGetProperty("used_bytes", out JsonElement used) &&
                    used.TryGetInt64(out long usedValue)
                        ? usedValue
                        : 0L;
                quotaBytes =
                    root.TryGetProperty("quota_bytes", out JsonElement quota) &&
                    quota.TryGetInt64(out long quotaValue)
                        ? quotaValue
                        : 0L;
                keyCount =
                    root.TryGetProperty("key_count", out JsonElement count) &&
                    count.TryGetInt32(out int countValue)
                        ? countValue
                        : 0;

                return true;
            }
            catch
            {
                error = "Invalid Experience K/V stats response.";
                return false;
            }
        }

        public bool ListKeyValueKeys(
            UUID scriptItemId,
            int start,
            int count,
            out string[] keys,
            out string error)
        {
            keys = Array.Empty<string>();

            if (!KeyValue(
                    scriptItemId,
                    "keys",
                    string.Empty,
                    string.Empty,
                    false,
                    string.Empty,
                    start,
                    count,
                    out string result,
                    out _,
                    out error))
            {
                return false;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(result);

                if (!document.RootElement.TryGetProperty(
                        "keys",
                        out JsonElement array) ||
                    array.ValueKind != JsonValueKind.Array)
                {
                    error = "Invalid Experience K/V keys response.";
                    return false;
                }

                List<string> output =
                    new List<string>();

                foreach (JsonElement element in array.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        output.Add(element.GetString() ?? string.Empty);
                }

                keys = output.ToArray();
                return true;
            }
            catch
            {
                error = "Invalid Experience K/V keys response.";
                return false;
            }
        }

        private bool KeyValue(
            UUID scriptItemId,
            string operation,
            string key,
            string value,
            bool checkOriginal,
            string originalValue,
            int start,
            int count,
            out string result,
            out string code,
            out string error)
        {
            result = string.Empty;
            code = string.Empty;
            error = string.Empty;

            if (!m_Enabled || scriptItemId.IsZero())
            {
                error = "Experience service is unavailable.";
                return false;
            }

            try
            {
                byte[] payload =
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            script_id = scriptItemId.ToString(),
                            operation,
                            key,
                            value = value ?? string.Empty,
                            check_original = checkOriginal,
                            original_value = originalValue ?? string.Empty,
                            start,
                            count
                        });

                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Post,
                        "/api/v1/experiences/script/kv");

                request.Content =
                    new ByteArrayContent(payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response =
                    m_Http.Send(request);

                string body =
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    error = ReadErrorMessage(response, body);
                    return false;
                }

                using JsonDocument document =
                    JsonDocument.Parse(body);

                bool success =
                    document.RootElement.TryGetProperty(
                        "success",
                        out JsonElement successElement) &&
                    successElement.ValueKind == JsonValueKind.True;

                if (document.RootElement.TryGetProperty(
                        "result",
                        out JsonElement resultElement))
                {
                    result =
                        resultElement.ValueKind == JsonValueKind.String
                            ? resultElement.GetString() ?? string.Empty
                            : resultElement.GetRawText();
                }

                if (document.RootElement.TryGetProperty(
                        "code",
                        out JsonElement codeElement) &&
                    codeElement.ValueKind == JsonValueKind.String)
                {
                    code =
                        codeElement.GetString() ??
                        string.Empty;
                }

                if (!success)
                {
                    error =
                        document.RootElement.TryGetProperty(
                            "message",
                            out JsonElement messageElement) &&
                        messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString() ?? "Experience key/value operation was rejected."
                            : "Experience key/value operation was rejected.";
                }

                return success;
            }
            catch (Exception e)
            {
                error = "Experience key/value service unavailable.";
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: K/V {0} for script {1} failed: {2}",
                    operation,
                    scriptItemId,
                    e.Message);
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(
            HttpMethod method,
            string path)
        {
            HttpRequestMessage request =
                new HttpRequestMessage(
                    method,
                    m_WorldApiBaseUrl +
                    path);

            request.Headers.TryAddWithoutValidation(
                "X-NexVerse-Api-Key",
                m_ApiKey);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));

            return request;
        }

        private static string ReadErrorMessage(
            HttpResponseMessage response,
            string body = null)
        {
            try
            {
                string json =
                    body ??
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty(
                        "message",
                        out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return
                        message.GetString() ??
                        "Experience request failed.";
                }
            }
            catch
            {
            }

            return
                "Experience request failed (" +
                (int)response.StatusCode +
                ").";
        }
    }
}
