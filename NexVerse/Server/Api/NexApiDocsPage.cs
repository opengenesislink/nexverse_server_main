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
                        "GET is required.\n");
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
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>NexVerse World API Explorer</title>
<style>
:root{color-scheme:dark;--bg:#090d13;--panel:#111923;--panel2:#182331;--line:#26364a;--text:#e8f0f7;--muted:#92a6b9;--accent:#70c7ff;--good:#64d49a;--warn:#ffd166;--danger:#ff7b7b}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,sans-serif}
header{padding:24px 28px;border-bottom:1px solid var(--line);background:linear-gradient(135deg,#101a28,#0c1119)}
header h1{margin:0 0 6px;font-size:24px}header p{margin:0;color:var(--muted)}
main{display:grid;grid-template-columns:minmax(280px,32%) 1fr;min-height:calc(100vh - 100px)}
aside{border-right:1px solid var(--line);padding:18px;overflow:auto}.work{padding:22px;overflow:auto}
input,textarea,button{font:inherit}input,textarea{width:100%;background:#0b121b;color:var(--text);border:1px solid var(--line);border-radius:7px;padding:9px}
textarea{min-height:150px;font-family:ui-monospace,monospace;resize:vertical}button{background:#173047;color:var(--text);border:1px solid #31506b;border-radius:7px;padding:8px 12px;cursor:pointer}
button:hover{border-color:var(--accent)}.row{display:flex;gap:10px;align-items:center}.row>*{flex:1}
.endpoint{padding:10px;margin:8px 0;border:1px solid var(--line);border-radius:8px;background:var(--panel);cursor:pointer}
.endpoint:hover,.endpoint.active{border-color:var(--accent);background:var(--panel2)}
.method{display:inline-block;min-width:54px;font-weight:700;color:var(--accent)}.path{font-family:ui-monospace,monospace}
.small{font-size:12px;color:var(--muted)}.badge{display:inline-block;padding:2px 7px;border-radius:999px;background:#223247;color:#cfe7f8;margin:2px 4px 2px 0;font-size:12px}
section{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:16px;margin-bottom:16px}
h2,h3{margin-top:0}.grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}.label{display:block;color:var(--muted);margin:8px 0 5px}
pre{white-space:pre-wrap;word-break:break-word;background:#080d13;border:1px solid var(--line);padding:12px;border-radius:8px;max-height:360px;overflow:auto}
.status-good{color:var(--good)}.status-bad{color:var(--danger)}.hidden{display:none}
@media(max-width:900px){main{grid-template-columns:1fr}aside{border-right:0;border-bottom:1px solid var(--line);max-height:42vh}.grid{grid-template-columns:1fr}}
</style>
</head>
<body>
<header>
<h1>NexVerse World API Explorer</h1>
<p id="meta">Loading live OpenAPI contract…</p>
</header>
<main>
<aside>
<input id="search" placeholder="Search endpoint, method, scope…">
<div id="endpoints"></div>
</aside>
<div class="work">
<section id="intro">
<h2>Live API documentation</h2>
<p>This explorer reads <code>/api/v1/openapi.json</code> from the running NexVerse server. Credentials are held only in this page's memory and are not persisted by the explorer.</p>
<h3>API version history</h3>
<div id="versions" class="small">Loading version metadata…</div>
</section>
<section id="detail" class="hidden">
<div class="row"><h2 id="title"></h2><span id="deprecated"></span></div>
<p id="summary"></p>
<div id="badges"></div>
<div class="grid">
<div>
<label class="label">Request URL</label>
<input id="url">
</div>
<div>
<label class="label">Method</label>
<input id="method" readonly>
</div>
</div>
<h3>Parameters</h3>
<pre id="parameters"></pre>
<h3>Request schema</h3>
<pre id="requestSchema"></pre>
<h3>AI / automation guidance</h3>
<pre id="aiInstruction"></pre>
<h3>Responses / errors</h3>
<pre id="responses"></pre>
</section>

<section id="auth">
<h2>Authentication for live requests</h2>
<div class="grid">
<div><label class="label">Bearer token</label><input id="bearer" type="password" autocomplete="off" placeholder="Optional"></div>
<div><label class="label">X-NexVerse-Api-Key</label><input id="apiKey" type="password" autocomplete="off" placeholder="Optional"></div>
</div>
<label class="label">Idempotency-Key</label>
<input id="idem" autocomplete="off" placeholder="Optional; useful for POST /api/v1/users">
</section>

<section id="runner" class="hidden">
<h2>Live request</h2>
<label class="label">JSON body</label>
<textarea id="body" spellcheck="false"></textarea>
<div class="row" style="margin-top:10px">
<button id="run">Execute request</button>
<button id="clear">Clear credentials</button>
</div>
<h3 style="margin-top:18px">Response</h3>
<div id="status" class="small"></div>
<pre id="output">No request executed.</pre>
</section>
</div>
</main>
<script>
'use strict';
let spec=null,entries=[],selected=null;
const $=id=>document.getElementById(id);
const methods=new Set(['get','post','put','patch','delete','options','head']);

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
function requestSchema(op){
  return op.requestBody?.content?.['application/json']?.schema||null;
}
function responseSchemas(op){
  const result={};
  for(const [code,response] of Object.entries(op.responses||{})){
    result[code]={
      description:response.description||'',
      schema:resolveSchema(response.content?.['application/json']?.schema)
    };
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
function renderList(){
  const q=$('search').value.trim().toLowerCase();
  const host=$('endpoints');host.replaceChildren();
  for(const entry of entries){
    const scope=entry.op['x-nexverse-scope']||'';
    const hay=[entry.method,entry.path,entry.op.summary||'',scope].join(' ').toLowerCase();
    if(q&&!hay.includes(q))continue;
    const div=document.createElement('div');
    div.className='endpoint'+(selected===entry?' active':'');
    const top=document.createElement('div');
    const m=document.createElement('span');m.className='method';m.textContent=entry.method;
    const p=document.createElement('span');p.className='path';p.textContent=entry.path;
    top.append(m,p);div.append(top);
    const summary=document.createElement('div');summary.className='small';summary.textContent=entry.op.summary||'';
    div.append(summary);
    div.addEventListener('click',()=>selectEntry(entry));
    host.append(div);
  }
}
function selectEntry(entry){
  selected=entry;renderList();
  $('intro').classList.add('hidden');$('detail').classList.remove('hidden');$('runner').classList.remove('hidden');
  $('title').textContent=entry.method+' '+entry.path;
  $('summary').textContent=entry.op.summary||'';
  $('method').value=entry.method;
  $('url').value=entry.path;
  $('deprecated').textContent=entry.op.deprecated?'DEPRECATED':'';
  $('deprecated').className=entry.op.deprecated?'badge status-bad':'';
  const badges=$('badges');badges.replaceChildren();
  const scope=entry.op['x-nexverse-scope'];
  if(scope){const b=document.createElement('span');b.className='badge';b.textContent='scope: '+scope;badges.append(b)}
  for(const audience of entry.op['x-nexverse-audience']||[]){
    const b=document.createElement('span');b.className='badge';b.textContent='audience: '+audience;badges.append(b)
  }
  for(const sec of entry.op.security||[]){
    const names=Object.keys(sec);
    if(names.length){const b=document.createElement('span');b.className='badge';b.textContent='auth: '+names.join(' or ');badges.append(b)}
  }
  $('parameters').textContent=JSON.stringify(entry.op.parameters||[],null,2);
  const req=requestSchema(entry.op);
  $('requestSchema').textContent=req?JSON.stringify(resolveSchema(req),null,2):'No JSON request body.';
  $('aiInstruction').textContent=JSON.stringify({
    purpose:entry.op['x-nexverse-purpose']||entry.op.summary||'',
    audience:entry.op['x-nexverse-audience']||[],
    instruction:entry.op['x-nexverse-ai-instruction']||'',
    security_constraints:entry.op['x-nexverse-security-constraints']||[],
    deprecated:!!entry.op.deprecated
  },null,2);
  $('responses').textContent=JSON.stringify(responseSchemas(entry.op),null,2);
  const ex=exampleFor(req);
  $('body').value=req&&ex!==null?JSON.stringify(ex,null,2):'';
}
async function execute(){
  if(!selected)return;
  const headers={'Accept':'application/json'};
  const bearer=$('bearer').value.trim(),key=$('apiKey').value.trim(),idem=$('idem').value.trim();
  if(bearer)headers.Authorization='Bearer '+bearer;
  if(key)headers['X-NexVerse-Api-Key']=key;
  if(idem)headers['Idempotency-Key']=idem;
  const body=$('body').value.trim();
  const opts={method:selected.method,headers};
  if(body&&!['GET','HEAD'].includes(selected.method)){headers['Content-Type']='application/json';opts.body=body}
  $('status').textContent='Request running…';$('output').textContent='';
  try{
    const res=await fetch($('url').value,{...opts,credentials:'same-origin'});
    const text=await res.text();
    $('status').textContent=res.status+' '+res.statusText+' | correlation: '+(res.headers.get('X-Correlation-Id')||'-');
    $('status').className=res.ok?'small status-good':'small status-bad';
    let formatted=text;
    try{formatted=JSON.stringify(JSON.parse(text),null,2)}catch{}
    const headerLines=[];res.headers.forEach((v,k)=>headerLines.push(k+': '+v));
    $('output').textContent=headerLines.join('\n')+'\n\n'+formatted;
  }catch(err){
    $('status').textContent='Request failed';$('status').className='small status-bad';$('output').textContent=String(err);
  }
}
async function init(){
  try{
    const response=await fetch('/api/v1/openapi.json',{cache:'no-store'});
    spec=await response.json();
    $('meta').textContent=(spec.info?.title||'NexVerse World API')+' '+(spec.info?.version||'')+' · live contract · '+location.origin;
    const history=spec['x_nexverse_version_history']||[];
    const versionHost=$('versions');versionHost.replaceChildren();
    if(!history.length){versionHost.textContent='No version history published.'}
    for(const item of history){
      const line=document.createElement('div');
      line.className='endpoint';
      line.textContent=[item.api_version,item.server_line,item.codename,item.status,item.compatibility].filter(Boolean).join(' · ');
      versionHost.append(line);
    }
    buildEntries();renderList();
  }catch(err){
    $('meta').textContent='Unable to load live OpenAPI document: '+err;
  }
}
$('search').addEventListener('input',renderList);
$('run').addEventListener('click',execute);
$('clear').addEventListener('click',()=>{$('bearer').value='';$('apiKey').value='';$('idem').value=''});
init();
</script>
</body>
</html>
""";
    }
}
