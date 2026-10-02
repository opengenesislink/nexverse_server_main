// SPDX-License-Identifier: MPL-2.0

using System;
using System.Net;
using System.Text;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    internal sealed class NexApiDocsPage
    {
        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            response.KeepAlive = false;

            if (request == null ||
                !string.Equals(
                    request.HttpMethod,
                    "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode =
                    (int)HttpStatusCode.MethodNotAllowed;
                response.ContentType =
                    "text/plain; charset=utf-8";
                response.RawBuffer =
                    Encoding.UTF8.GetBytes(
                        "GET ist erforderlich.\n");
                return;
            }

            response.StatusCode =
                (int)HttpStatusCode.OK;
            response.ContentType =
                "text/html; charset=utf-8";
            response.AddHeader(
                "Cache-Control",
                "no-store");
            response.AddHeader(
                "X-Content-Type-Options",
                "nosniff");
            response.AddHeader(
                "Referrer-Policy",
                "no-referrer");
            response.AddHeader(
                "Content-Security-Policy",
                "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'");
            response.RawBuffer =
                Encoding.UTF8.GetBytes(s_Html);
        }

        private const string s_Html = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>NexVerse Welt-API-Kontrollzentrum</title>
<style>
:root{color-scheme:dark;--bg:#071019;--panel:#0f1a26;--panel2:#142335;--line:#24384d;--text:#e9f1f8;--muted:#8fa5b8;--accent:#66c7ff;--accent2:#8be0c2;--good:#62d49b;--warn:#ffd166;--danger:#ff7d7d;--shadow:0 18px 55px rgba(0,0,0,.28)}
*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:radial-gradient(circle at 85% 0,#112b3d 0,transparent 34%),var(--bg);color:var(--text);font:14px/1.5 system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}
button,input,textarea{font:inherit}button{cursor:pointer}
.shell{min-height:100vh;display:grid;grid-template-columns:250px 1fr}
nav{position:sticky;top:0;height:100vh;padding:24px 18px;border-right:1px solid var(--line);background:rgba(8,15,24,.94);backdrop-filter:blur(12px)}
.brand{padding:0 8px 24px}.brand strong{display:block;font-size:19px;letter-spacing:.2px}.brand span{color:var(--muted);font-size:12px}
.navbtn{width:100%;text-align:left;margin:4px 0;padding:10px 12px;border:1px solid transparent;border-radius:9px;color:var(--muted);background:transparent}
.navbtn:hover,.navbtn.active{color:var(--text);background:var(--panel2);border-color:var(--line)}
.navmeta{position:absolute;left:26px;right:26px;bottom:24px;color:var(--muted);font-size:12px}
main{min-width:0;padding:34px}.page{display:none;max-width:1280px;margin:0 auto}.page.active{display:block}
.hero{display:flex;justify-content:space-between;gap:24px;align-items:flex-end;margin-bottom:24px}.eyebrow{color:var(--accent);font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.12em}.hero h1{font-size:32px;line-height:1.15;margin:7px 0 8px}.hero p{margin:0;color:var(--muted);max-width:760px}
.pill{display:inline-flex;align-items:center;gap:7px;border:1px solid var(--line);border-radius:999px;padding:6px 10px;background:var(--panel);font-size:12px;color:var(--muted)}.dot{width:8px;height:8px;border-radius:50%;background:var(--warn)}.dot.good{background:var(--good)}.dot.bad{background:var(--danger)}
.cards{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:20px 0 26px}.card,.section{border:1px solid var(--line);background:linear-gradient(180deg,rgba(20,35,53,.95),rgba(13,24,35,.95));border-radius:12px;box-shadow:var(--shadow)}
.card{padding:16px}.card .label{color:var(--muted);font-size:12px}.card .value{font-size:21px;font-weight:700;margin-top:6px;word-break:break-word}.card .sub{color:var(--muted);font-size:12px;margin-top:4px}
.section{padding:20px;margin-bottom:18px}.section h2{margin:0 0 5px;font-size:19px}.sectionlead{color:var(--muted);margin:0 0 16px}
.changegrid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.change{border:1px solid var(--line);background:#0b1621;border-radius:10px;padding:14px}.changehead{display:flex;justify-content:space-between;gap:12px;align-items:start}.change h3{font-size:15px;margin:0 0 7px}.change p{color:var(--muted);margin:0}.meta{font-size:12px;color:var(--muted);margin-top:10px}.tag{display:inline-block;border-radius:999px;padding:3px 7px;margin:2px 4px 2px 0;background:#1b3044;color:#cfe7f8;font-size:11px}.tag.good{background:#153727;color:#aaf0ca}
.filters{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px}.filter{border:1px solid var(--line);background:#0b1621;color:var(--muted);border-radius:999px;padding:6px 10px}.filter.active{border-color:var(--accent);color:var(--text)}
.version{display:grid;grid-template-columns:150px 1fr;gap:16px;padding:14px 0;border-bottom:1px solid var(--line)}.version:last-child{border-bottom:0}.version strong{display:block}.version ul{margin:6px 0 0;padding-left:18px;color:var(--muted)}
.roadmap-summary{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin-bottom:18px}.roadmap-list{display:grid;gap:12px}.milestone{border:1px solid var(--line);background:#0b1621;border-radius:11px;padding:16px}.milestone.current{border-color:var(--accent);box-shadow:0 0 0 1px rgba(102,199,255,.12)}.milestonehead{display:flex;justify-content:space-between;gap:16px;align-items:flex-start}.milestone h3{margin:0 0 4px;font-size:16px}.milestone p{margin:8px 0;color:var(--muted)}.milestone .evidence{margin:8px 0 0;padding-left:18px;color:var(--muted)}.status{display:inline-block;border:1px solid var(--line);border-radius:999px;padding:3px 8px;font-size:11px;text-transform:uppercase;letter-spacing:.06em}.status-released{color:#bdf4dc;border-color:#2d7053;background:#113326}.status-active{color:#b9ecff;border-color:#2c6f91;background:#102c3e}.status-advanced{color:#bdf4dc;border-color:#2d7053;background:#113326}.status-started,.status-foundation{color:#ffe6a6;border-color:#7c6131;background:#342914}.status-planned{color:#aebdca;background:#111b25}.progressline{margin-top:12px}.progressmeta{display:flex;justify-content:space-between;gap:12px;color:var(--muted);font-size:12px;margin-bottom:5px}.progressbar{height:7px;background:#07111b;border-radius:999px;overflow:hidden;border:1px solid var(--line)}.progressbar span{display:block;height:100%;background:linear-gradient(90deg,var(--accent),var(--accent2))}
.explorer{display:grid;grid-template-columns:minmax(290px,32%) 1fr;gap:16px}.endpointlist,.detail{min-width:0}.search{width:100%;background:#08131e;color:var(--text);border:1px solid var(--line);border-radius:9px;padding:10px;margin-bottom:10px}
.endpoint{padding:10px;margin:7px 0;border:1px solid var(--line);border-radius:9px;background:#0b1621;cursor:pointer}.endpoint:hover,.endpoint.active{border-color:var(--accent);background:var(--panel2)}
.method{display:inline-block;min-width:58px;font-weight:800;color:var(--accent)}.path{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px}.small{font-size:12px;color:var(--muted)}
input,textarea{width:100%;background:#08131e;color:var(--text);border:1px solid var(--line);border-radius:8px;padding:9px}textarea{min-height:150px;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;resize:vertical}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px}.label2{display:block;color:var(--muted);margin:8px 0 5px}pre{white-space:pre-wrap;word-break:break-word;background:#06101a;border:1px solid var(--line);padding:12px;border-radius:8px;max-height:350px;overflow:auto}
.action{background:#15324a;color:var(--text);border:1px solid #315a78;border-radius:8px;padding:8px 12px}.action:hover{border-color:var(--accent)}.row{display:flex;gap:10px;align-items:center}.row>*{flex:1}.hidden{display:none}.status-good{color:var(--good)}.status-bad{color:var(--danger)}
.empty{color:var(--muted);padding:18px;border:1px dashed var(--line);border-radius:9px;text-align:center}
.tablewrap{overflow:auto;border:1px solid var(--line);border-radius:9px}.datatable{width:100%;border-collapse:collapse;min-width:720px}.datatable th,.datatable td{padding:10px 12px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}.datatable th{font-size:12px;color:var(--muted);background:#0a1520}.datatable tr:last-child td{border-bottom:0}.datatable td:first-child{font-weight:600}.authnote{margin-top:10px;color:var(--muted);font-size:12px}
@media(max-width:1100px){.cards,.roadmap-summary{grid-template-columns:repeat(2,1fr)}.changegrid{grid-template-columns:1fr}.explorer{grid-template-columns:1fr}.endpointlist{max-height:420px;overflow:auto}}
@media(max-width:760px){.shell{display:block}nav{position:static;height:auto;border-right:0;border-bottom:1px solid var(--line)}.navmeta{display:none}.navbtn{display:inline-block;width:auto}.brand{padding-bottom:12px}main{padding:22px 14px}.hero{display:block}.hero .pill{margin-top:14px}.cards,.roadmap-summary{grid-template-columns:1fr}.grid2,.version{grid-template-columns:1fr}.milestonehead{display:block}.milestonehead .status{margin-top:8px}}
</style>
</head>
<body>
<div class="shell">
<nav>
<div class="brand"><strong>NexVerse API</strong><span>Welt-API-Kontrollzentrum</span></div>
<button class="navbtn active" data-page="overview">Übersicht</button>
<button class="navbtn" data-page="statistics">Statistik</button>
<button class="navbtn" data-page="changes">Was ist neu?</button>
<button class="navbtn" data-page="versions">Versionen</button>
<button class="navbtn" data-page="roadmap">Entwicklungsplan</button>
<button class="navbtn" data-page="explorer">API-Endpunkte</button>
<div class="navmeta" id="navmeta">Live-Daten werden geladen…</div>
</nav>
<main>
<section class="page active" id="page-overview">
<div class="hero">
<div><div class="eyebrow">NexVerse Welt-API</div><h1>API-Status auf einen Blick</h1><p>Live-Übersicht über Serverversion, API-Vertrag, aktuelle Erweiterungen und verfügbare Endpunkte.</p></div>
<div class="pill"><span class="dot" id="healthDot"></span><span id="healthText">Prüfe API-Status…</span></div>
</div>
<div class="cards">
<div class="card"><div class="label">Server</div><div class="value" id="serverVersion">–</div><div class="sub" id="milestone">Meilenstein –</div></div>
<div class="card"><div class="label">API</div><div class="value" id="apiVersion">–</div><div class="sub">Aktueller OpenAPI-3.1-Vertrag</div></div>
<div class="card"><div class="label">Endpunkte</div><div class="value" id="endpointCount">–</div><div class="sub" id="operationCount">– Operationen</div></div>
<div class="card"><div class="label">Letzte Änderung</div><div class="value" id="lastChangeDate">–</div><div class="sub" id="lastChangeTitle">Keine Versionshinweise</div></div>
</div>
<div class="section">
<h2>Neu in der aktuellen Version</h2><p class="sectionlead">Die Einträge werden direkt aus <code>x_nexverse_changelog</code> im laufenden OpenAPI-Dokument gelesen und deutsch dargestellt.</p>
<div class="changegrid" id="overviewChanges"></div>
</div>
<div class="section">
<h2>Schnellzugriff</h2><p class="sectionlead">Die wichtigsten technischen Oberflächen des laufenden Servers.</p>
<div class="filters">
<button class="filter" data-goto="changes">Alle Neuerungen</button>
<button class="filter" data-goto="versions">Versionshistorie</button>
<button class="filter" data-goto="roadmap">Entwicklungsplan / Projektstatus</button>
<button class="filter" data-goto="explorer">Endpunktübersicht</button>
</div>
</div>
</section>


<section class="page" id="page-statistics">
<div class="hero">
<div><div class="eyebrow">Live-Statistik</div><h1>Grid-Statistik</h1><p>Aktuelle Einwohner-, Aktivitäts-, Anwesenheits- und Hypergrid-Kennzahlen direkt aus den laufenden NexVerse-Diensten.</p></div>
<div class="pill"><span class="dot" id="statsDot"></span><span id="statsStatus">Noch nicht geladen</span></div>
</div>
<div class="section">
<h2>Zugriff</h2><p class="sectionlead">Aggregierte Kennzahlen sind ohne Anmeldung verfügbar. Personenbezogene Online-, Regions- und Hypergrid-Details bleiben geschützt und erfordern ein Zugriffstoken oder einen API-Schlüssel mit <code>statistics:read</code> über einen geschützten Transport.</p>
<div class="grid2">
<div><label class="label2">Zugriffstoken (Bearer)</label><input id="statsBearer" type="password" autocomplete="off" placeholder="Optional"></div>
<div><label class="label2">X-NexVerse-Api-Key</label><input id="statsApiKey" type="password" autocomplete="off" placeholder="Optional"></div>
</div>
<div class="row" style="margin-top:10px"><button class="action" id="loadStats">Statistik laden</button><button class="action" id="clearStats">Zugangsdaten löschen</button></div>
<div class="authnote">Die Zugangsdaten bleiben nur in dieser geöffneten Seite und werden nicht gespeichert.</div>
</div>
<div class="cards">
<div class="card"><div class="label">Registriert</div><div class="value" id="statRegistered">–</div><div class="sub">lokale Einwohnerkonten</div></div>
<div class="card"><div class="label">Gerade online</div><div class="value" id="statOnlineTotal">–</div><div class="sub">Einwohner + Hypergrid</div></div>
<div class="card"><div class="label">Einwohner online</div><div class="value" id="statResidentsOnline">–</div><div class="sub">auf diesem Grid</div></div>
<div class="card"><div class="label">Aktiv letzte 7 Tage</div><div class="value" id="statActive7">–</div><div class="sub">eindeutige Einwohner</div></div>
<div class="card"><div class="label">Aktiv letzte 30 Tage</div><div class="value" id="statActive30">–</div><div class="sub">eindeutige Einwohner</div></div>
<div class="card"><div class="label">Hypergrid online</div><div class="value" id="statHgOnline">–</div><div class="sub">fremde Besucher jetzt</div></div>
<div class="card"><div class="label">HG-Besucher bekannt</div><div class="value" id="statHgKnown">–</div><div class="sub">eindeutige Besucher</div></div>
<div class="card"><div class="label">HG letzte 30 Tage</div><div class="value" id="statHg30">–</div><div class="sub">eindeutige Besucher</div></div>
</div>
<div class="section">
<h2>Gerade online</h2><p class="sectionlead">Lokale Einwohner und Hypergrid-Besucher mit aktuell ermittelter Region.</p>
<div class="tablewrap"><table class="datatable"><thead><tr><th>Name</th><th>Typ</th><th>Region</th><th>Heimat-Grid</th><th>Login</th></tr></thead><tbody id="statsOnlineBody"></tbody></table></div>
</div>
<div class="section">
<h2>Hypergrid-Herkunft</h2><p class="sectionlead">Bekannte eindeutige Besucher nach Heimat-Grid. Dies sind keine kumulierten Besuchssessions.</p>
<div class="tablewrap"><table class="datatable"><thead><tr><th>Heimat-Grid</th><th>Bekannt</th><th>Online</th><th>7 Tage</th><th>30 Tage</th></tr></thead><tbody id="statsHgBody"></tbody></table></div>
</div>
<div class="section">
<h2>Regionen jetzt</h2><p class="sectionlead">Aktuelle Belegung nach Region, getrennt nach lokalen Einwohnern und Hypergrid-Besuchern.</p>
<div class="tablewrap"><table class="datatable"><thead><tr><th>Region</th><th>Gesamt</th><th>Einwohner</th><th>Hypergrid</th></tr></thead><tbody id="statsRegionBody"></tbody></table></div>
</div>
<div class="section">
<h2>Datenbasis</h2><pre id="statsQuality">Statistik noch nicht geladen.</pre>
</div>
</section>

<section class="page" id="page-changes">
<div class="hero"><div><div class="eyebrow">Änderungsprotokoll</div><h1>Was ist neu?</h1><p>Nachvollziehbare, maschinenlesbare Änderungen der World API und ihrer Plattform-Grundlagen.</p></div></div>
<div class="section">
<div class="filters" id="changeFilters"></div>
<div class="changegrid" id="allChanges"></div>
</div>
</section>

<section class="page" id="page-versions">
<div class="hero"><div><div class="eyebrow">Kompatibilität</div><h1>Versionen</h1><p>API-Versionen, Server-Linien, Codenamen und veröffentlichte Highlights.</p></div></div>
<div class="section" id="versionList"></div>
</section>

<section class="page" id="page-roadmap">
<div class="hero"><div><div class="eyebrow">Projektstatus</div><h1>NexVerse-Entwicklungsplan</h1><p>Meilenstein-Status auf Basis des veröffentlichten Entwicklungsplans und konkreter Implementierungsnachweise. Prozentwerte werden nur angezeigt, wenn der Meilenstein tatsächlich eine gepflegte Checkbox-Checkliste besitzt.</p></div></div>
<div class="roadmap-summary">
<div class="card"><div class="label">Aktueller Meilenstein</div><div class="value" id="roadmapCurrent">–</div><div class="sub" id="roadmapCodename">–</div></div>
<div class="card"><div class="label">Aktiv</div><div class="value" id="roadmapActiveCount">–</div><div class="sub">aktueller Produkt-Meilenstein</div></div>
<div class="card"><div class="label">Vorgezogen / begonnen</div><div class="value" id="roadmapStartedCount">–</div><div class="sub">vorgezogen, begonnen oder Grundlage</div></div>
<div class="card"><div class="label">Geplant</div><div class="value" id="roadmapPlannedCount">–</div><div class="sub">noch nicht als implementiert beansprucht</div></div>
</div>
<div class="section">
<h2>Statusmodell</h2>
<p class="sectionlead">Die Statusbegriffe vermeiden erfundene Gesamtfortschritte. „Vorgezogen“ bedeutet, dass Arbeit aus einem späteren Meilenstein bereits früher umgesetzt wurde, nicht dass der Meilenstein vollständig abgeschlossen ist.</p>
<div class="filters" id="roadmapLegend"></div>
</div>
<div class="roadmap-list" id="roadmapList"></div>
</section>

<section class="page" id="page-explorer">
<div class="hero"><div><div class="eyebrow">OpenAPI 3.1</div><h1>API-Endpunkte prüfen</h1><p>Durchsuche den aktuellen Vertrag, prüfe Berechtigungsumfänge und führe autorisierte Anfragen direkt gegen denselben Server aus.</p></div></div>
<div class="explorer">
<div class="endpointlist section">
<input class="search" id="search" placeholder="Endpunkt, Methode, Berechtigung suchen…">
<div id="endpoints"></div>
</div>
<div class="detail">
<div class="section hidden" id="detail">
<div class="row"><h2 id="title"></h2><span id="deprecated"></span></div>
<p id="summary" class="sectionlead"></p>
<div id="badges"></div>
<div class="grid2">
<div><label class="label2">Anfrage-URL</label><input id="url"></div>
<div><label class="label2">HTTP-Methode</label><input id="method" readonly></div>
</div>
<h3>Parameter</h3><pre id="parameters"></pre>
<h3>Anfrageschema</h3><pre id="requestSchema"></pre>
<h3>KI-/Automatisierungshinweise</h3><pre id="aiInstruction"></pre>
<h3>Antworten / Fehler</h3><pre id="responses"></pre>
</div>
<div class="section">
<h2>Authentifizierung</h2>
<div class="grid2">
<div><label class="label2">Zugriffstoken (Bearer)</label><input id="bearer" type="password" autocomplete="off" placeholder="Optional"></div>
<div><label class="label2">X-NexVerse-Api-Key</label><input id="apiKey" type="password" autocomplete="off" placeholder="Optional"></div>
</div>
<label class="label2">Idempotency-Key</label><input id="idem" autocomplete="off" placeholder="Optional">
</div>
<div class="section hidden" id="runner">
<h2>Live-Anfrage</h2>
<label class="label2">JSON-Anfrageinhalt</label><textarea id="body" spellcheck="false"></textarea>
<div class="row" style="margin-top:10px"><button class="action" id="run">Anfrage ausführen</button><button class="action" id="clear">Zugangsdaten löschen</button></div>
<h3 style="margin-top:18px">Antwort</h3><div id="status" class="small"></div><pre id="output">Noch keine Anfrage ausgeführt.</pre>
</div>
</div>
</div>
</section>
</main>
</div>

<script>
'use strict';
let spec=null,entries=[],selected=null,changes=[];
const $=id=>document.getElementById(id);
const methods=new Set(['get','post','put','patch','delete','options','head']);
const statusLabels={released:'Veröffentlicht',active:'Aktiv',advanced:'Vorgezogen',started:'Begonnen',foundation:'Grundlage',planned:'Geplant',implemented:'Umgesetzt',development:'Entwicklung',release:'Veröffentlicht'};
const categoryLabels={analytics:'Statistik',observability:'Beobachtbarkeit',security:'Sicherheit',api:'API',identity:'Identität',platform:'Plattform',runtime:'Laufzeit',scripting:'Skripting'};
const audienceLabels={citizen:'Einwohner',admin:'Administration',service:'Dienst'};
const languageLabels={'de-DE':'Deutsch (Deutschland)'};
const accountStateLabels={active:'aktiv',locked:'gesperrt',banned:'gebannt',deactivated:'deaktiviert',provisioning:'Provisionierung',provisioning_failed:'Provisionierung fehlgeschlagen'};
function statusLabel(value){return statusLabels[value]||value||''}
function categoryLabel(value){return categoryLabels[value]||value||''}
function audienceLabel(value){return audienceLabels[value]||value||''}
function languageLabel(value){return languageLabels[value]||value||''}

function showPage(name){
  document.querySelectorAll('.page').forEach(x=>x.classList.toggle('active',x.id==='page-'+name));
  document.querySelectorAll('.navbtn').forEach(x=>x.classList.toggle('active',x.dataset.page===name));
  window.scrollTo({top:0,behavior:'smooth'});
}
document.querySelectorAll('.navbtn').forEach(x=>x.addEventListener('click',()=>showPage(x.dataset.page)));
document.querySelectorAll('[data-goto]').forEach(x=>x.addEventListener('click',()=>showPage(x.dataset.goto)));

function resolveSchema(schema){
  if(!schema)return null;
  if(schema.$ref){
    const name=schema.$ref.split('/').pop();
    return spec.components?.schemas?.[name]||schema;
  }
  return schema;
}
function exampleFor(schema,depth=0){
  if(!schema||depth>5)return null;
  if(schema.$ref)return exampleFor(resolveSchema(schema),depth+1);
  if(schema.example!==undefined)return schema.example;
  if(schema.enum?.length)return schema.enum[0];
  const type=Array.isArray(schema.type)?schema.type.find(x=>x!=='null'):schema.type;
  if(type==='object'||schema.properties){
    const o={};
    for(const [k,v] of Object.entries(schema.properties||{})){
      if((schema.required||[]).includes(k)||depth<2)o[k]=exampleFor(v,depth+1);
    }
    return o;
  }
  if(type==='array')return [];
  if(type==='boolean')return false;
  if(type==='integer'||type==='number')return schema.minimum??0;
  if(schema.format==='uuid')return '00000000-0000-0000-0000-000000000000';
  if(schema.format==='date-time')return new Date(0).toISOString();
  return '';
}
function requestSchema(op){return op.requestBody?.content?.['application/json']?.schema||null}
function responseSchemas(op){
  const result={};
  for(const [code,response] of Object.entries(op.responses||{})){
    result[code]={description:response.description||'',schema:resolveSchema(response.content?.['application/json']?.schema)};
  }
  return result;
}
function buildEntries(){
  entries=[];
  for(const [path,item] of Object.entries(spec.paths||{})){
    for(const [method,op] of Object.entries(item||{})){
      if(!methods.has(method.toLowerCase()))continue;
      entries.push({path,method:method.toUpperCase(),op});
    }
  }
  entries.sort((a,b)=>a.path.localeCompare(b.path)||a.method.localeCompare(b.method));
}
function renderEndpointList(){
  const q=$('search').value.trim().toLowerCase();
  const host=$('endpoints');host.replaceChildren();
  for(const entry of entries){
    const scope=entry.op['x-nexverse-scope']||'';
    const hay=[entry.method,entry.path,entry.op.summary||'',scope].join(' ').toLowerCase();
    if(q&&!hay.includes(q))continue;
    const div=document.createElement('div');div.className='endpoint'+(selected===entry?' active':'');
    const top=document.createElement('div');
    const m=document.createElement('span');m.className='method';m.textContent=entry.method;
    const p=document.createElement('span');p.className='path';p.textContent=entry.path;
    top.append(m,p);div.append(top);
    const summary=document.createElement('div');summary.className='small';summary.textContent=entry.op.summary||'';
    div.append(summary);div.addEventListener('click',()=>selectEntry(entry));host.append(div);
  }
}
function selectEntry(entry){
  selected=entry;renderEndpointList();$('detail').classList.remove('hidden');$('runner').classList.remove('hidden');
  $('title').textContent=entry.method+' '+entry.path;$('summary').textContent=entry.op.summary||'';$('method').value=entry.method;$('url').value=entry.path;
  $('deprecated').textContent=entry.op.deprecated?'VERALTET':'';$('deprecated').className=entry.op.deprecated?'tag':'';
  const badges=$('badges');badges.replaceChildren();
  const scope=entry.op['x-nexverse-scope'];
  if(scope){const b=document.createElement('span');b.className='tag';b.textContent='Berechtigung: '+scope;badges.append(b)}
  for(const audience of entry.op['x-nexverse-audience']||[]){const b=document.createElement('span');b.className='tag';b.textContent='Zielgruppe: '+audienceLabel(audience);badges.append(b)}
  for(const sec of entry.op.security||[]){const names=Object.keys(sec);if(names.length){const b=document.createElement('span');b.className='tag';b.textContent='Authentifizierung: '+names.join(' oder ');badges.append(b)}}
  $('parameters').textContent=JSON.stringify(entry.op.parameters||[],null,2);
  const req=requestSchema(entry.op);$('requestSchema').textContent=req?JSON.stringify(resolveSchema(req),null,2):'Kein JSON-Anfrageinhalt.';
  $('aiInstruction').textContent=JSON.stringify({zweck:entry.op['x-nexverse-purpose']||entry.op.summary||'',zielgruppe:(entry.op['x-nexverse-audience']||[]).map(audienceLabel),anweisung:entry.op['x-nexverse-ai-instruction']||'',sicherheitsvorgaben:entry.op['x-nexverse-security-constraints']||[],veraltet:!!entry.op.deprecated},null,2);
  $('responses').textContent=JSON.stringify(responseSchemas(entry.op),null,2);
  const ex=exampleFor(req);$('body').value=req&&ex!==null?JSON.stringify(ex,null,2):'';
}
function makeChange(item){
  const box=document.createElement('article');box.className='change';
  const head=document.createElement('div');head.className='changehead';
  const title=document.createElement('h3');title.textContent=item.title||'Änderung';
  const status=document.createElement('span');status.className='tag '+(item.status==='implemented'?'good':'');status.textContent=statusLabel(item.status);
  head.append(title,status);box.append(head);
  const summary=document.createElement('p');summary.textContent=item.summary||'';box.append(summary);
  const tags=document.createElement('div');tags.className='meta';
  for(const value of [item.date,item.version]){if(value){const t=document.createElement('span');t.className='tag';t.textContent=value;tags.append(t)}}if(item.category){const t=document.createElement('span');t.className='tag';t.textContent=categoryLabel(item.category);tags.append(t)}
  box.append(tags);
  if(item.endpoints?.length){const eps=document.createElement('div');eps.className='small';eps.style.marginTop='8px';eps.textContent='Betrifft: '+item.endpoints.join(', ');box.append(eps)}
  return box;
}
function renderChanges(category){
  const list=category&&category!=='all'?changes.filter(x=>x.category===category):changes;
  const all=$('allChanges');all.replaceChildren();
  if(!list.length){all.innerHTML='<div class="empty">Keine Änderungen für diesen Filter.</div>';return}
  list.forEach(x=>all.append(makeChange(x)));
}
function renderChangeFilters(){
  const host=$('changeFilters');host.replaceChildren();
  const cats=['all',...new Set(changes.map(x=>x.category).filter(Boolean))];
  cats.forEach((cat,index)=>{
    const b=document.createElement('button');b.className='filter'+(index===0?' active':'');b.textContent=cat==='all'?'Alle':categoryLabel(cat);
    b.addEventListener('click',()=>{host.querySelectorAll('.filter').forEach(x=>x.classList.remove('active'));b.classList.add('active');renderChanges(cat)});
    host.append(b);
  });
}
function renderVersions(){
  const host=$('versionList');host.replaceChildren();
  const history=spec['x_nexverse_version_history']||[];
  if(!history.length){host.innerHTML='<div class="empty">Keine Versionshistorie veröffentlicht.</div>';return}
  history.forEach(item=>{
    const row=document.createElement('div');row.className='version';
    const left=document.createElement('div');left.innerHTML='<strong></strong><span class="small"></span>';left.querySelector('strong').textContent='API '+(item.api_version||'');left.querySelector('span').textContent=(item.published?'Veröffentlicht: '+item.published:'Status: '+statusLabel(item.status));
    const right=document.createElement('div');const h=document.createElement('strong');h.textContent=[item.server_line,item.codename].filter(Boolean).join(' · ');right.append(h);
    const p=document.createElement('div');p.className='small';p.textContent=item.compatibility||'';right.append(p);
    if(item.highlights?.length){const ul=document.createElement('ul');item.highlights.forEach(v=>{const li=document.createElement('li');li.textContent=v;ul.append(li)});right.append(ul)}
    row.append(left,right);host.append(row);
  });
}
function renderRoadmap(){
  const roadmap=spec['x_nexverse_roadmap'];
  const host=$('roadmapList');host.replaceChildren();
  if(!roadmap?.milestones?.length){host.innerHTML='<div class="empty">Kein maschinenlesbarer Entwicklungsplan-Status veröffentlicht.</div>';return}
  $('roadmapCurrent').textContent=roadmap.current_milestone||'–';$('roadmapCodename').textContent=roadmap.current_codename?('Codename: '+roadmap.current_codename):'Aktive Entwicklung';
  const milestones=roadmap.milestones;
  $('roadmapActiveCount').textContent=milestones.filter(x=>x.status==='active').length;
  $('roadmapStartedCount').textContent=milestones.filter(x=>['advanced','started','foundation'].includes(x.status)).length;
  $('roadmapPlannedCount').textContent=milestones.filter(x=>x.status==='planned').length;
  const legend=$('roadmapLegend');legend.replaceChildren();
  for(const [key,value] of Object.entries(roadmap.status_model||{})){
    const item=document.createElement('span');item.className='tag';item.textContent=statusLabel(key)+' — '+value;legend.append(item);
  }
  milestones.forEach(item=>{
    const box=document.createElement('article');box.className='milestone'+(item.version===roadmap.current_milestone?' current':'');
    const head=document.createElement('div');head.className='milestonehead';
    const title=document.createElement('div');const h=document.createElement('h3');h.textContent=item.version+' · '+item.title;title.append(h);
    if(item.codename){const code=document.createElement('div');code.className='small';code.textContent='Codename: '+item.codename;title.append(code)}
    const status=document.createElement('span');status.className='status status-'+item.status;status.textContent=statusLabel(item.status);
    head.append(title,status);box.append(head);
    const p=document.createElement('p');p.textContent=item.summary||'';box.append(p);
    if(item.checklist&&item.checklist.total>0){
      const wrap=document.createElement('div');wrap.className='progressline';
      const meta=document.createElement('div');meta.className='progressmeta';
      const done=document.createElement('span');done.textContent='Entwicklungsplan-Checkliste: '+item.checklist.completed+' / '+item.checklist.total+' erledigt';
      const open=document.createElement('span');open.textContent=item.checklist.open+' offen';meta.append(done,open);
      const bar=document.createElement('div');bar.className='progressbar';const fill=document.createElement('span');
      const pct=Math.max(0,Math.min(100,(item.checklist.completed/item.checklist.total)*100));fill.style.width=pct+'%';bar.append(fill);wrap.append(meta,bar);box.append(wrap);
    }
    if(item.evidence?.length){
      const ul=document.createElement('ul');ul.className='evidence';item.evidence.forEach(v=>{const li=document.createElement('li');li.textContent=v;ul.append(li)});box.append(ul);
    }
    host.append(box);
  });
}

function statText(id,value){$(id).textContent=value===null||value===undefined?'–':String(value)}
function appendStatsRow(hostId,values){
  const host=$(hostId),tr=document.createElement('tr');
  values.forEach(value=>{const td=document.createElement('td');td.textContent=value===null||value===undefined||value===''?'–':String(value);tr.append(td)});
  host.append(tr);
}
function formatStatDate(value){
  if(!value)return '–';
  const d=new Date(value);
  return Number.isNaN(d.getTime())?String(value):d.toLocaleString('de-DE');
}
function renderStatistics(data){
  const residents=data.residents||{},hg=data.hypergrid||{},online=data.online||{};
  const protectedDetails=data.protected_details===true;
  statText('statRegistered',residents.registered_total);
  statText('statOnlineTotal',online.total);
  statText('statResidentsOnline',residents.online_now);
  statText('statActive7',residents.active_last_7_days);
  statText('statActive30',residents.active_last_30_days);
  statText('statHgOnline',hg.online_now);
  statText('statHgKnown',hg.known_visitors_total);
  statText('statHg30',hg.visitors_last_30_days);
  $('statsOnlineBody').replaceChildren();
  (online.users||[]).forEach(user=>appendStatsRow('statsOnlineBody',[
    user.name,
    user.type==='hypergrid'?'Hypergrid':'Einwohner',
    user.region_name||user.region_id,
    user.home_grid||'lokal',
    formatStatDate(user.login_at)
  ]));
  if(!(online.users||[]).length)appendStatsRow('statsOnlineBody',[protectedDetails?'Niemand online':'Geschützte Details – statistics:read erforderlich','','','','']);
  $('statsHgBody').replaceChildren();
  (data.hypergrid_home_grids||[]).forEach(item=>appendStatsRow('statsHgBody',[
    item.home_grid,item.known_visitors,item.online_now,item.last_7_days,item.last_30_days
  ]));
  if(!(data.hypergrid_home_grids||[]).length)appendStatsRow('statsHgBody',[protectedDetails?'Keine Hypergrid-Daten':'Geschützte Details – statistics:read erforderlich','','','','']);
  $('statsRegionBody').replaceChildren();
  (data.regions||[]).forEach(item=>appendStatsRow('statsRegionBody',[
    item.region_name||item.region_id,item.online_total,item.residents,item.hypergrid
  ]));
  if(!(data.regions||[]).length)appendStatsRow('statsRegionBody',[protectedDetails?'Keine Online-Belegung':'Geschützte Details – statistics:read erforderlich','','','']);
  const states=Object.fromEntries(Object.entries(data.account_states||{}).map(([k,v])=>[accountStateLabels[k]||k,v]));
  const quality=data.data_quality||{};
  const qualityValues={
    'UserAccountService aggregate':'UserAccountService-Aggregat',
    'GridUser/UserAccount fallback':'GridUser-/UserAccount-Ersatzquelle',
    'PresenceService':'PresenceService',
    'GridUser Online flag with five-day stale guard':'GridUser-Online-Status mit Fünf-Tage-Schutz gegen veraltete Daten',
    'Unique identities use the latest GridUser Login/Logout timestamps.':'Eindeutige Identitäten verwenden die neuesten GridUser-Zeitstempel für Anmeldung/Abmeldung.',
    'Known Hypergrid counts are distinct visitor/home-grid identities, not cumulative visit sessions.':'Bekannte Hypergrid-Zahlen sind eindeutige Besucher-/Heimat-Grid-Identitäten und keine kumulierten Besuchssitzungen.'
  };
  const qv=value=>qualityValues[value]||value;
  $('statsQuality').textContent=JSON.stringify({
    erzeugt_am:data.generated_at,
    kontostatus:states,
    datenqualitaet:{
      registrierungsquelle:qv(quality.registered_source),
      online_quelle:qv(quality.online_source),
      historische_aktivitaet:qv(quality.historical_activity),
      hypergrid_historie:qv(quality.hypergrid_history),
      exakte_besuchssitzungen_verfuegbar:quality.exact_visit_sessions_available
    }
  },null,2);
}
async function loadStatistics(){
  const headers={'Accept':'application/json'};
  const bearer=$('statsBearer').value.trim()||$('bearer').value.trim();
  const key=$('statsApiKey').value.trim()||$('apiKey').value.trim();
  if(bearer)headers.Authorization='Bearer '+bearer;
  if(key)headers['X-NexVerse-Api-Key']=key;
  $('statsStatus').textContent='Lade Statistik…';$('statsDot').className='dot';
  try{
    const res=await fetch('/api/v1/statistics/summary',{headers,cache:'no-store',credentials:'same-origin'});
    const txt=await res.text();let data=null;try{data=JSON.parse(txt)}catch{}
    if(!res.ok){
      $('statsStatus').textContent=res.status===401||res.status===403?'Authentifizierung erforderlich':'Statistik nicht verfügbar';
      $('statsDot').className='dot bad';
      $('statsQuality').textContent=data?JSON.stringify(data,null,2):txt;
      return;
    }
    renderStatistics(data||{});
    $('statsStatus').textContent='Live · '+(data.protected_details?'Details':'Aggregiert')+' · '+formatStatDate(data.generated_at);
    $('statsDot').className='dot good';
  }catch(err){
    $('statsStatus').textContent='Statistik nicht erreichbar';$('statsDot').className='dot bad';$('statsQuality').textContent=String(err);
  }
}

async function execute(){
  if(!selected)return;
  const headers={'Accept':'application/json'},bearer=$('bearer').value.trim(),key=$('apiKey').value.trim(),idem=$('idem').value.trim();
  if(bearer)headers.Authorization='Bearer '+bearer;if(key)headers['X-NexVerse-Api-Key']=key;if(idem)headers['Idempotency-Key']=idem;
  const body=$('body').value.trim(),opts={method:selected.method,headers};
  if(body&&!['GET','HEAD'].includes(selected.method)){headers['Content-Type']='application/json';opts.body=body}
  $('status').textContent='Anfrage läuft…';$('output').textContent='';
  try{
    const res=await fetch($('url').value,{...opts,credentials:'same-origin'}),txt=await res.text();
    $('status').textContent=res.status+' '+res.statusText+' | Korrelations-ID: '+(res.headers.get('X-Correlation-Id')||'-');$('status').className=res.ok?'small status-good':'small status-bad';
    let formatted=txt;try{formatted=JSON.stringify(JSON.parse(txt),null,2)}catch{}
    const headerLines=[];res.headers.forEach((v,k)=>headerLines.push(k+': '+v));$('output').textContent=headerLines.join('\n')+'\n\n'+formatted;
  }catch(err){$('status').textContent='Anfrage fehlgeschlagen';$('status').className='small status-bad';$('output').textContent=String(err)}
}
async function init(){
  try{
    const results=await Promise.all([
      fetch('/api/v1/openapi.json',{cache:'no-store'}).then(r=>r.json()),
      fetch('/api/v1/health',{cache:'no-store'}).then(r=>r.json()),
      fetch('/api/v1/version',{cache:'no-store'}).then(r=>r.json())
    ]);
    spec=results[0];const health=results[1],version=results[2];
    changes=spec['x_nexverse_changelog']||[];
    buildEntries();
    $('healthText').textContent=health.status==='ok'?'API online':'API Status: '+(health.status||'unbekannt');$('healthDot').className='dot '+(health.status==='ok'?'good':'bad');
    $('serverVersion').textContent=version.server_version||'–';$('milestone').textContent='Meilenstein '+(version.milestone||'–')+(version.milestone_title?' · '+version.milestone_title:'');$('apiVersion').textContent=version.api_version||spec.info?.version||'–';
    $('endpointCount').textContent=Object.keys(spec.paths||{}).length;$('operationCount').textContent=entries.length+' Operationen';
    $('navmeta').textContent=(version.server_version||'NexVerse')+' · '+(version.api_version||'API')+' · '+languageLabel(version.language||spec['x_nexverse_language']||'de-DE');
    if(changes.length){$('lastChangeDate').textContent=changes[0].date||'–';$('lastChangeTitle').textContent=changes[0].title||''}
    const overview=$('overviewChanges');overview.replaceChildren();changes.slice(0,4).forEach(x=>overview.append(makeChange(x)));if(!changes.length)overview.innerHTML='<div class="empty">Noch keine Versionshinweise veröffentlicht.</div>';
    renderChangeFilters();renderChanges('all');renderVersions();renderRoadmap();renderEndpointList();
    loadStatistics();
  }catch(err){
    $('healthText').textContent='API-Metadaten konnten nicht geladen werden';$('healthDot').className='dot bad';$('navmeta').textContent=String(err);
  }
}
$('search').addEventListener('input',renderEndpointList);$('run').addEventListener('click',execute);$('clear').addEventListener('click',()=>{$('bearer').value='';$('apiKey').value='';$('idem').value=''});
$('loadStats').addEventListener('click',loadStatistics);$('clearStats').addEventListener('click',()=>{$('statsBearer').value='';$('statsApiKey').value=''});
init();
</script>
</body>
</html>
""";
    }
}
