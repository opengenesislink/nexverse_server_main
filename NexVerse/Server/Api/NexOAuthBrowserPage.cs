// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using NexVerse.Core.Security;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    internal static class NexOAuthBrowserPage
    {
        public static void WriteLoginConsent(
            IOSHttpResponse response,
            NexOAuthClient client,
            string responseType,
            string redirectUri,
            string[] scopes,
            string state,
            string nonce,
            string codeChallenge,
            string codeChallengeMethod,
            string errorMessage = "")
        {
            string clientName = string.IsNullOrWhiteSpace(client?.Name)
                ? "Unbenannte Anwendung"
                : client.Name.Trim();

            string redirectDisplay = redirectUri;
            if (Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri redirect))
                redirectDisplay = redirect.GetLeftPart(UriPartial.Authority);

            StringBuilder scopeItems = new StringBuilder();
            foreach (string scope in scopes ?? Array.Empty<string>())
            {
                scopeItems.Append("<li><strong>")
                    .Append(Html(ScopeTitle(scope)))
                    .Append("</strong><span>")
                    .Append(Html(ScopeDescription(scope)))
                    .Append("</span></li>");
            }

            if (scopeItems.Length == 0)
            {
                scopeItems.Append("<li><strong>Keine zusätzlichen Berechtigungen</strong><span>Die Anwendung fordert keine zusätzlichen NexVerse-Berechtigungen an.</span></li>");
            }

            string errorBlock = string.IsNullOrWhiteSpace(errorMessage)
                ? string.Empty
                : "<div class=\"error\">" + Html(errorMessage) + "</div>";

            string html = @"<!doctype html>
<html lang=""de"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>NexVerse Anmeldung und Zugriff</title>
<style>
:root{color-scheme:dark;--bg:#071019;--panel:#101c28;--line:#294057;--text:#eef6fc;--muted:#9cb0c2;--accent:#67c8ff;--good:#73dda8;--danger:#ff8b8b}
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px;background:radial-gradient(circle at 80% 0,#15324a 0,transparent 38%),var(--bg);color:var(--text);font:15px/1.5 system-ui,-apple-system,BlinkMacSystemFont,""Segoe UI"",sans-serif}
.card{width:min(680px,100%);background:linear-gradient(180deg,#132436,#0c1722);border:1px solid var(--line);border-radius:16px;padding:28px;box-shadow:0 24px 70px rgba(0,0,0,.38)}
.brand{font-size:13px;text-transform:uppercase;letter-spacing:.12em;color:var(--accent);font-weight:800}.title{font-size:28px;line-height:1.18;margin:8px 0}.lead{color:var(--muted);margin:0 0 22px}.app{padding:14px;border:1px solid var(--line);border-radius:11px;background:#0b1621;margin-bottom:18px}.app strong{display:block;font-size:17px}.app span{display:block;color:var(--muted);font-size:12px;margin-top:4px;word-break:break-all}
.permissions{margin:0 0 20px;padding:0;list-style:none}.permissions li{padding:11px 0;border-bottom:1px solid var(--line)}.permissions li:last-child{border-bottom:0}.permissions strong,.permissions span{display:block}.permissions span{color:var(--muted);font-size:13px;margin-top:2px}
label{display:block;color:var(--muted);font-size:13px;margin:12px 0 5px}input{width:100%;background:#08131e;color:var(--text);border:1px solid var(--line);border-radius:9px;padding:11px 12px}.actions{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:20px}button{border-radius:9px;padding:11px 14px;font:inherit;font-weight:700;cursor:pointer}.approve{border:1px solid #31775c;background:#174a36;color:#c9f8e0}.deny{border:1px solid #694040;background:#30191c;color:#ffd4d4}.error{margin:0 0 16px;padding:11px 12px;border:1px solid #784747;background:#321b1f;color:#ffd1d1;border-radius:9px}.note{margin-top:16px;color:var(--muted);font-size:12px}.scope-admin{color:var(--danger)}
@media(max-width:600px){.card{padding:20px}.actions{grid-template-columns:1fr}.title{font-size:24px}}
</style>
</head>
<body>
<main class=""card"">
<div class=""brand"">NexVerse Welt-API</div>
<h1 class=""title"">Anmelden und Zugriff erlauben</h1>
<p class=""lead"">Eine Anwendung möchte auf dein NexVerse-Konto zugreifen. Prüfe die angeforderten Berechtigungen, bevor du den Zugriff freigibst.</p>
<div class=""app""><strong>" + Html(clientName) + @"</strong><span>Weiterleitung nach Freigabe: " + Html(redirectDisplay) + @"</span></div>
" + errorBlock + @"
<h2>Angeforderte Berechtigungen</h2>
<ul class=""permissions"">" + scopeItems + @"</ul>
<form method=""post"" action=""/oauth/authorize"" autocomplete=""on"">
<input type=""hidden"" name=""response_type"" value=""" + Attr(responseType) + @""">
<input type=""hidden"" name=""client_id"" value=""" + Attr(client?.ClientId ?? string.Empty) + @""">
<input type=""hidden"" name=""redirect_uri"" value=""" + Attr(redirectUri) + @""">
<input type=""hidden"" name=""scope"" value=""" + Attr(string.Join(" ", scopes ?? Array.Empty<string>())) + @""">
<input type=""hidden"" name=""state"" value=""" + Attr(state) + @""">
<input type=""hidden"" name=""nonce"" value=""" + Attr(nonce) + @""">
<input type=""hidden"" name=""code_challenge"" value=""" + Attr(codeChallenge) + @""">
<input type=""hidden"" name=""code_challenge_method"" value=""" + Attr(codeChallengeMethod) + @""">
<label for=""username"">NexVerse-Benutzername</label>
<input id=""username"" name=""username"" type=""text"" autocomplete=""username"" required maxlength=""128"" placeholder=""z. B. Antonia.Porta"">
<label for=""password"">Passwort</label>
<input id=""password"" name=""password"" type=""password"" autocomplete=""current-password"" required maxlength=""256"">
<div class=""actions"">
<button class=""approve"" type=""submit"" name=""decision"" value=""approve"">Zugriff erlauben</button>
<button class=""deny"" type=""submit"" name=""decision"" value=""deny"" formnovalidate>Ablehnen</button>
</div>
</form>
<p class=""note"">Dein Passwort wird nur zur unmittelbaren NexVerse-Anmeldung verwendet und weder an die Anwendung weitergegeben noch in der Weiterleitungs-URL gespeichert.</p>
</main>
</body>
</html>";

            WriteHtml(response, html, HttpStatusCode.OK);
        }

        public static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string title,
            string message)
        {
            string html = @"<!doctype html>
<html lang=""de""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>NexVerse Autorisierung</title>
<style>:root{color-scheme:dark}body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px;background:#071019;color:#eef6fc;font:15px/1.5 system-ui,sans-serif}.box{max-width:620px;border:1px solid #334b60;border-radius:14px;background:#101c28;padding:26px}.eyebrow{color:#67c8ff;font-weight:800;text-transform:uppercase;letter-spacing:.1em;font-size:12px}h1{margin:7px 0 10px}p{color:#a7b8c7}</style>
</head><body><main class=""box""><div class=""eyebrow"">NexVerse Welt-API</div><h1>" +
                Html(title) + "</h1><p>" + Html(message) + "</p></main></body></html>";

            WriteHtml(response, html, status);
        }

        private static void WriteHtml(
            IOSHttpResponse response,
            string html,
            HttpStatusCode status)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "text/html; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("Pragma", "no-cache");
            response.AddHeader("Referrer-Policy", "no-referrer");
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.AddHeader("X-Frame-Options", "DENY");
            response.AddHeader(
                "Content-Security-Policy",
                "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'");
            response.RawBuffer = Encoding.UTF8.GetBytes(html ?? string.Empty);
        }

        private static string ScopeTitle(string scope)
        {
            return scope switch
            {
                NexScopes.OpenId => "Identität bestätigen",
                NexScopes.Profile => "Grundlegende Profilinformationen",
                NexScopes.OfflineAccess => "Dauerhafte Sitzung",
                NexScopes.UsersRead => "Einwohnerkonto lesen",
                NexScopes.UsersWrite => "Einwohnerkonto bearbeiten",
                NexScopes.InventoryRead => "Inventar lesen",
                NexScopes.InventoryWrite => "Inventar bearbeiten",
                NexScopes.FriendsManage => "Freundschaften verwalten",
                NexScopes.RegionsRead => "Regionen lesen",
                NexScopes.RegionsManage => "Regionen verwalten",
                NexScopes.StatisticsRead => "Geschützte Statistik lesen",
                NexScopes.EstatesManage => "Estate verwalten",
                NexScopes.EconomyRead => "Wirtschaftsdaten lesen",
                NexScopes.EconomyTransfer => "NV$ übertragen",
                NexScopes.AdminAll => "Vollständiger administrativer Zugriff",
                _ => scope ?? "Unbekannte Berechtigung"
            };
        }

        private static string ScopeDescription(string scope)
        {
            return scope switch
            {
                NexScopes.OpenId => "Bestätigt deine NexVerse-Identität gegenüber der Anwendung.",
                NexScopes.Profile => "Erlaubt den Zugriff auf grundlegende Profildaten.",
                NexScopes.OfflineAccess => "Erlaubt der Anwendung, ihre Sitzung mit einem Aktualisierungstoken zu verlängern.",
                NexScopes.UsersRead => "Erlaubt das Lesen der freigegebenen Einwohner-Kontodaten.",
                NexScopes.UsersWrite => "Erlaubt zulässige Änderungen am Einwohnerkonto.",
                NexScopes.InventoryRead => "Erlaubt das Lesen freigegebener Inventardaten.",
                NexScopes.InventoryWrite => "Erlaubt zulässige Änderungen am Inventar.",
                NexScopes.FriendsManage => "Erlaubt die Verwaltung freigegebener Freundschaftsfunktionen.",
                NexScopes.RegionsRead => "Erlaubt das Lesen von Regionsinformationen.",
                NexScopes.RegionsManage => "Erlaubt Verwaltungsaktionen an Regionen.",
                NexScopes.StatisticsRead => "Erlaubt den Zugriff auf geschützte Anwesenheits- und Regionsdetails.",
                NexScopes.EstatesManage => "Erlaubt Estate-Verwaltungsaktionen.",
                NexScopes.EconomyRead => "Erlaubt das Lesen freigegebener Wirtschaftsdaten.",
                NexScopes.EconomyTransfer => "Erlaubt freigegebene NV$-Übertragungen.",
                NexScopes.AdminAll => "Gewährt weitreichende administrative Rechte. Nur freigeben, wenn du der Anwendung vollständig vertraust.",
                _ => "Von der Anwendung angeforderter NexVerse-Berechtigungsumfang."
            };
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string Attr(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
