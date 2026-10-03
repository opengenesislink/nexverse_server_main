// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// NexVerse WebProfileV3 projection over the authoritative UserAccount and
    /// OpenSim profile stores. This deliberately reuses IProfilesData so viewer
    /// profiles and web/API profiles never diverge.
    /// </summary>
    internal sealed class NexWebProfileV3Api
    {
        private readonly IUserAccountService m_Accounts;
        private readonly IProfilesData m_Profiles;
        private readonly NexApiAuthenticator m_Authenticator;

        public NexWebProfileV3Api(
            IUserAccountService accounts,
            IProfilesData profiles,
            NexApiAuthenticator authenticator)
        {
            m_Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            m_Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        }

        public void Handle(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string[] segments = (httpRequest?.UriPath ?? string.Empty).Trim('/').Split('/');
            // /api/v1/profiles/{uuid}
            if (segments.Length != 4 ||
                !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
                !segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) ||
                !segments[2].Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
                !UUID.TryParse(segments[3], out UUID userId))
            {
                WriteJson(httpResponse, HttpStatusCode.NotFound, new { error = "profile_not_found" });
                return;
            }

            UserAccount account = m_Accounts.GetUserAccount(UUID.Zero, userId);
            if (account == null)
            {
                WriteJson(httpResponse, HttpStatusCode.NotFound, new { error = "profile_not_found" });
                return;
            }

            if (string.Equals(httpRequest.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                Get(account, httpResponse);
                return;
            }

            if (string.Equals(httpRequest.HttpMethod, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                Patch(account, httpRequest.InputStream, httpRequest, httpResponse);
                return;
            }

            httpResponse.AddHeader("Allow", "GET, PATCH");
            WriteJson(httpResponse, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
        }

        private void Get(UserAccount account, IOSHttpResponse response)
        {
            UserProfileProperties profile = new UserProfileProperties { UserId = account.PrincipalID };
            string error = string.Empty;
            m_Profiles.GetAvatarProperties(ref profile, ref error);

            WriteJson(response, HttpStatusCode.OK, new
            {
                id = account.PrincipalID.ToString(),
                username = account.Username,
                display_name = account.EffectiveDisplayName,
                profile_image = profile.ImageId.ToString(),
                about = profile.AboutText ?? string.Empty,
                web_url = profile.WebUrl ?? string.Empty,
                interests = new
                {
                    want_to_mask = profile.WantToMask,
                    want_to_text = profile.WantToText ?? string.Empty,
                    skills_mask = profile.SkillsMask,
                    skills_text = profile.SkillsText ?? string.Empty,
                    language = profile.Language ?? string.Empty
                },
                partner_id = profile.PartnerId.IsZero() ? null : profile.PartnerId.ToString(),
                visibility = profile.PublishProfile ? "public" : "private",
                mature = profile.PublishMature,
                picks = m_Profiles.GetAvatarPicks(account.PrincipalID),
                classifieds = m_Profiles.GetClassifiedRecords(account.PrincipalID)
            });
        }

        private void Patch(
            UserAccount account,
            Stream body,
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!m_Authenticator.TryAuthenticate(
                    request,
                    NexScopes.ProfileWrite,
                    out NexPrincipal principal,
                    out UserAccount authenticatedAccount,
                    out int status,
                    out string authError))
            {
                WriteJson(response, (HttpStatusCode)status, new { error = authError });
                return;
            }

            bool self = UUID.TryParse(principal.Subject, out UUID subjectId) && subjectId == account.PrincipalID;
            if (!self && !principal.HasScope(NexScopes.AdminAll))
            {
                WriteJson(response, HttpStatusCode.Forbidden, new { error = "profile_owner_required" });
                return;
            }

            UserProfileProperties profile = new UserProfileProperties { UserId = account.PrincipalID };
            string storeError = string.Empty;
            m_Profiles.GetAvatarProperties(ref profile, ref storeError);

            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("about", out JsonElement about))
                    profile.AboutText = Limit(about.GetString(), 510);

                if (root.TryGetProperty("web_url", out JsonElement webUrl))
                    profile.WebUrl = Limit(webUrl.GetString(), 255);

                if (root.TryGetProperty("profile_image", out JsonElement image) &&
                    UUID.TryParse(image.GetString(), out UUID imageId))
                    profile.ImageId = imageId;

                if (root.TryGetProperty("visibility", out JsonElement visibility))
                    profile.PublishProfile = string.Equals(visibility.GetString(), "public", StringComparison.OrdinalIgnoreCase);

                if (root.TryGetProperty("mature", out JsonElement mature) &&
                    (mature.ValueKind == JsonValueKind.True || mature.ValueKind == JsonValueKind.False))
                    profile.PublishMature = mature.GetBoolean();

                if (root.TryGetProperty("interests", out JsonElement interests) &&
                    interests.ValueKind == JsonValueKind.Object)
                {
                    if (interests.TryGetProperty("want_to_mask", out JsonElement wantMask) && wantMask.TryGetInt32(out int wm))
                        profile.WantToMask = wm;
                    if (interests.TryGetProperty("want_to_text", out JsonElement wantText))
                        profile.WantToText = Limit(wantText.GetString(), 510);
                    if (interests.TryGetProperty("skills_mask", out JsonElement skillsMask) && skillsMask.TryGetInt32(out int sm))
                        profile.SkillsMask = sm;
                    if (interests.TryGetProperty("skills_text", out JsonElement skillsText))
                        profile.SkillsText = Limit(skillsText.GetString(), 510);
                    if (interests.TryGetProperty("language", out JsonElement language))
                        profile.Language = Limit(language.GetString(), 255);
                }
            }
            catch (JsonException)
            {
                WriteJson(response, HttpStatusCode.BadRequest, new { error = "invalid_json" });
                return;
            }

            if (!m_Profiles.UpdateAvatarProperties(ref profile, ref storeError) ||
                !m_Profiles.UpdateAvatarInterests(profile, ref storeError))
            {
                WriteJson(response, HttpStatusCode.InternalServerError, new { error = "profile_update_failed" });
                return;
            }

            Get(account, response);
        }

        private static string Limit(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static void WriteJson(IOSHttpResponse response, HttpStatusCode status, object payload)
        {
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            response.KeepAlive = false;
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.RawBuffer = bytes;
        }
    }
}
