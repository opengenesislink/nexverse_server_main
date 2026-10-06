// SPDX-License-Identifier: MPL-2.0

using System;
using System.Net;
using System.Text;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    internal sealed class NexDiscoveryWebPage
    {
        private readonly string m_PublicBaseUrl;

        public NexDiscoveryWebPage(string publicBaseUrl)
        {
            m_PublicBaseUrl =
                string.IsNullOrWhiteSpace(publicBaseUrl)
                    ? "https://world.stadt-nexverse.de"
                    : publicBaseUrl.TrimEnd('/');
        }

        public void Search(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            WriteHtml(
                response,
                Page(
                    "NexVerse Suche",
                    SearchBody()));
        }

        public void Destinations(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            WriteHtml(
                response,
                Page(
                    "NexVerse Destination Guide",
                    DestinationBody()));
        }

        private string SearchBody()
        {
            string api =
                Js(m_PublicBaseUrl + "/api/v1/search");

            return $@"
<main class='shell'>
  <header>
    <div>
      <p class='eyebrow'>OpenGenesisLINK · Stadt NexVerse</p>
      <h1>Suche</h1>
      <p class='muted'>Einwohner, Gruppen, Regionen, Orte, Land, Events, Classifieds und Experiences.</p>
    </div>
  </header>

  <form id='searchForm' class='searchbar'>
    <input id='query' type='search' placeholder='NexVerse durchsuchen ...' autocomplete='off'>
    <select id='type'>
      <option value='all'>Alles</option>
      <option value='people'>Einwohner</option>
      <option value='groups'>Gruppen</option>
      <option value='regions'>Regionen</option>
      <option value='places'>Orte</option>
      <option value='parcels'>Parzellen</option>
      <option value='land'>Land</option>
      <option value='events'>Events</option>
      <option value='classifieds'>Classifieds</option>
      <option value='experiences'>Experiences</option>
    </select>
    <button type='submit'>Suchen</button>
  </form>

  <div id='status' class='status'>Bereit.</div>
  <section id='results' class='cards'></section>
</main>

<script>
const API = '{api}';
const params = new URLSearchParams(location.search);
const q = document.getElementById('query');
const type = document.getElementById('type');
const status = document.getElementById('status');
const results = document.getElementById('results');

q.value = params.get('q') || params.get('query') || '';
let incomingType = (params.get('type') || '').toLowerCase();
if (incomingType && [...type.options].some(o => o.value === incomingType)) type.value = incomingType;

function esc(v) {{
  return String(v ?? '').replace(/[&<>"']/g, c => ({{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}})[c]);
}}

function maturity() {{
  const m = (params.get('maturity') || '').toLowerCase();
  if (m === '13' || m === 'g') return 'general';
  if (m === '21' || m === 'gm') return 'mature';
  if (m === '42' || m === 'gma') return '';
  return '';
}}

async function runSearch() {{
  const query = q.value.trim();
  const selected = type.value;
  const u = new URL(API);
  u.searchParams.set('q', query);
  if (selected !== 'all') u.searchParams.set('types', selected);
  const mat = maturity();
  if (mat) u.searchParams.set('maturity', mat);
  u.searchParams.set('limit', '100');

  status.textContent = 'Suche laeuft ...';
  results.replaceChildren();

  try {{
    const r = await fetch(u.toString(), {{credentials:'omit'}});
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const data = await r.json();
    const hits = data.results || [];
    status.textContent = hits.length + ' Treffer';

    if (!hits.length) {{
      results.innerHTML = "<div class='empty'>Keine Treffer gefunden.</div>";
      return;
    }}

    for (const hit of hits) {{
      const card = document.createElement('article');
      card.className = 'card';
      const teleport = hit.teleport_uri
        ? "<a class='action' href='" + esc(hit.teleport_uri) + "'>Teleport</a>"
        : "";
      card.innerHTML =
        "<div class='tag'>" + esc(hit.type || 'result') + "</div>" +
        "<h2>" + esc(hit.name || hit.username || hit.id || 'Eintrag') + "</h2>" +
        "<p>" + esc(hit.description || hit.region_name || hit.username || '') + "</p>" +
        teleport;
      results.appendChild(card);
    }}
  }} catch (e) {{
    status.textContent = 'Suche derzeit nicht verfuegbar.';
    results.innerHTML = "<div class='empty'>NexSearch konnte nicht geladen werden.</div>";
  }}
}}

document.getElementById('searchForm').addEventListener('submit', e => {{
  e.preventDefault();
  runSearch();
}});

if (q.value) runSearch();
</script>";
        }

        private string DestinationBody()
        {
            string api =
                Js(m_PublicBaseUrl + "/api/v1/destinations");

            return $@"
<main class='shell'>
  <header>
    <div>
      <p class='eyebrow'>OpenGenesisLINK · Stadt NexVerse</p>
      <h1>Destination Guide</h1>
      <p class='muted'>Ausgewaehlte Orte und Regionen in NexVerse.</p>
    </div>
  </header>

  <div class='searchbar'>
    <input id='query' type='search' placeholder='Destination suchen ...' autocomplete='off'>
    <select id='sort'>
      <option value='popularity'>Beliebt</option>
      <option value='newest'>Neu</option>
      <option value='name'>Name</option>
    </select>
    <button id='reload' type='button'>Anzeigen</button>
  </div>

  <div id='status' class='status'>Destinationen werden geladen ...</div>
  <section id='results' class='cards'></section>
</main>

<script>
const API = '{api}';
const q = document.getElementById('query');
const sort = document.getElementById('sort');
const status = document.getElementById('status');
const results = document.getElementById('results');

function esc(v) {{
  return String(v ?? '').replace(/[&<>"']/g, c => ({{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}})[c]);
}}

async function load() {{
  const u = new URL(API);
  if (q.value.trim()) u.searchParams.set('q', q.value.trim());
  u.searchParams.set('sort', sort.value);
  u.searchParams.set('limit', '100');

  status.textContent = 'Destinationen werden geladen ...';
  results.replaceChildren();

  try {{
    const r = await fetch(u.toString(), {{credentials:'omit'}});
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const data = await r.json();
    const items = data.destinations || [];
    status.textContent = items.length + ' Destinationen';

    if (!items.length) {{
      results.innerHTML = "<div class='empty'>Noch keine freigegebenen Destinationen vorhanden.</div>";
      return;
    }}

    for (const item of items) {{
      const card = document.createElement('article');
      card.className = 'card';
      const teleport = item.teleport_uri
        ? "<a class='action' href='" + esc(item.teleport_uri) + "'>Teleport</a>"
        : "";
      card.innerHTML =
        "<div class='tag'>" + esc(item.category || 'Destination') + "</div>" +
        "<h2>" + esc(item.title || item.name || 'Destination') + "</h2>" +
        "<p>" + esc(item.description || item.region_name || '') + "</p>" +
        teleport;
      results.appendChild(card);
    }}
  }} catch (e) {{
    status.textContent = 'Destination Guide derzeit nicht verfuegbar.';
    results.innerHTML = "<div class='empty'>NexDestinationGuide konnte nicht geladen werden.</div>";
  }}
}}

document.getElementById('reload').addEventListener('click', load);
q.addEventListener('keydown', e => {{ if (e.key === 'Enter') load(); }});
load();
</script>";
        }

        private static string Page(string title, string body)
        {
            return @"<!doctype html>
<html lang='de'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>" + Html(title) + @"</title>
<style>
:root{color-scheme:dark;background:#080b12;color:#eef3ff;font-family:Inter,system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}
*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at top,#182033 0,#080b12 52%);min-height:100vh}
.shell{max-width:1100px;margin:0 auto;padding:34px 24px 60px}header{display:flex;justify-content:space-between;gap:20px;margin-bottom:24px}
h1{font-size:34px;margin:4px 0 8px}.eyebrow{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:#7ea7ff;margin:0}.muted{color:#aab4c8;margin:0}
.searchbar{display:grid;grid-template-columns:minmax(0,1fr) 180px auto;gap:10px;margin:24px 0}
input,select,button{border:1px solid #2b3751;background:#111827;color:#eef3ff;border-radius:10px;padding:12px 14px;font:inherit}
button,.action{cursor:pointer;background:#315efb;border-color:#315efb;color:white;font-weight:700}
.status{color:#91a0ba;font-size:13px;margin:8px 0 16px}.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(245px,1fr));gap:14px}
.card,.empty{border:1px solid #26324a;background:rgba(17,24,39,.9);border-radius:14px;padding:18px}.card h2{font-size:18px;margin:8px 0}.card p{color:#b6c0d4;min-height:38px}
.tag{font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:#83a8ff}.action{display:inline-block;text-decoration:none;padding:8px 12px;border-radius:8px;margin-top:8px}
@media(max-width:680px){.searchbar{grid-template-columns:1fr}.shell{padding:24px 15px}}
</style>
</head>
<body>" + body + @"</body>
</html>";
        }

        private static bool RequireGet(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (string.Equals(
                    request?.HttpMethod,
                    "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            response.StatusCode =
                (int)HttpStatusCode.MethodNotAllowed;
            response.ContentType =
                "text/plain; charset=utf-8";
            response.RawBuffer =
                Encoding.UTF8.GetBytes(
                    "GET is required.");
            return false;
        }

        private static void WriteHtml(
            IOSHttpResponse response,
            string html)
        {
            response.StatusCode =
                (int)HttpStatusCode.OK;
            response.ContentType =
                "text/html; charset=utf-8";
            response.RawBuffer =
                Encoding.UTF8.GetBytes(
                    html ?? string.Empty);
        }

        private static string Html(string value) =>
            (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace(""", "&quot;")
                .Replace("'", "&#39;");

        private static string Js(string value) =>
            (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("'", "\\'");
    }
}
