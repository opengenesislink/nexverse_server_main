// SPDX-License-Identifier: MPL-2.0

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Standalone same-origin citizen map page. A city-portal menu item may link
    /// to or embed /api/v1/world-map/view without exposing privileged grid APIs.
    /// </summary>
    internal static class NexCitizenWorldMapPage
    {
        internal const string Html = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="dark">
<title>NexVerse Stadtportal · Weltkarte</title>
<style>
:root{color-scheme:dark;font:15px/1.5 system-ui,Arial,sans-serif;background:#07111e;color:#e9f1fa}
*{box-sizing:border-box}body{margin:0}button,input{font:inherit}
header{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:15px 21px;border-bottom:1px solid #22334a;background:#0b1727}
h1{font-size:1.18rem;margin:0;letter-spacing:.02em}header small{color:#9eb2c9}
main{display:grid;grid-template-columns:minmax(240px,300px) minmax(0,1fr);height:calc(100vh - 69px);min-height:420px}
aside{padding:20px;border-right:1px solid #22334a;background:#0c1828;overflow:auto}
h2{font-size:1rem;margin:0 0 10px}p{color:#9fb3ca}
form{display:flex;gap:6px;margin:14px 0}input{width:100%;min-width:0;padding:10px 12px;background:#111f31;border:1px solid #334964;border-radius:9px;color:#fff}
button{cursor:pointer;padding:8px 12px;color:#fff;border-radius:8px;background:#173047;border:1px solid #36516d}
button:hover{background:#224461}button:focus-visible,a:focus-visible,input:focus-visible{outline:2px solid #57c3f8;outline-offset:2px}
#results{display:grid;gap:8px}.result{width:100%;text-align:left;display:grid;gap:3px;padding:12px;border-color:#2a435b}
.result span{font-size:.79rem;color:#a0b5ca}.status{font-size:.83rem;min-height:32px}
section{position:relative;overflow:hidden;background:#10253a}
#world{position:absolute;inset:0;overflow:hidden;touch-action:none;cursor:grab}
#world.dragging{cursor:grabbing}
#layers{position:absolute;inset:0;overflow:hidden}
.tile,.region{position:absolute}.tile{border:1px solid rgba(125,157,185,.12);background:#153047;overflow:hidden}
.tile img{width:100%;height:100%;object-fit:cover;image-rendering:auto}
.region{border:1px solid rgba(105,208,255,.65);background:rgba(20,96,143,.14);pointer-events:none}
.region button{pointer-events:auto;position:absolute;top:4px;left:4px;max-width:calc(100% - 8px);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;background:rgba(7,17,30,.87);padding:3px 6px;font-size:.79rem;border:1px solid #4687af}
#controls{position:absolute;right:18px;top:16px;display:flex;gap:6px;z-index:4}
#info{position:absolute;left:18px;bottom:18px;right:18px;background:#0b1829ec;padding:14px 16px;border:1px solid #345372;border-radius:12px;max-width:400px;box-shadow:0 12px 30px #0008;display:none;z-index:5}
#info h3{margin:0 0 5px}#info a{color:#8bdaff}#info p{margin:4px 0 10px}
#coords{position:absolute;bottom:12px;right:17px;background:#081421cb;color:#b1c5d5;padding:4px 8px;border-radius:6px;font-size:.76rem}
.badge{font-size:.73rem;color:#91d8ff;text-transform:uppercase;letter-spacing:.14em}
@media(max-width:760px){main{grid-template-columns:1fr;grid-template-rows:200px minmax(400px,1fr);height:auto}aside{border-right:0;border-bottom:1px solid #22334a;padding:13px;overflow:auto}section{height:calc(100dvh - 270px);min-height:400px}header small{display:none}form{margin:6px 0}h2{display:none}#results{grid-template-columns:repeat(auto-fill,minmax(180px,1fr))}}
</style>
</head>
<body>
<header><div><div class="badge">Stadt NexVerse · OpenGenesisLINK</div><h1>Weltkarte</h1></div><small>Regionen entdecken · suchen · teleportieren</small></header>
<main>
<aside>
  <h2>Region finden</h2>
  <form id="searchForm" autocomplete="off"><input id="search" aria-label="Region suchen" placeholder="Regionsname suchen (mind. 2 Zeichen)" maxlength="80"><button type="submit">Suchen</button></form>
  <div id="status" class="status" role="status" aria-live="polite">Lade Weltkarte …</div>
  <div id="results" aria-label="Suchergebnisse"></div>
  <p>Die Karte zeigt veröffentlichte, aktive Regionen. Kartenkacheln werden vom OpenSim MapImageService geladen, sofern dieser verfügbar ist.</p>
</aside>
<section aria-label="Interaktive NexVerse-Weltkarte">
  <div id="world" role="application" aria-label="Weltkarte ziehen zum Verschieben"><div id="layers"></div></div>
  <div id="controls"><button id="north" title="Nach Norden">↑</button><button id="west" title="Nach Westen">←</button><button id="east" title="Nach Osten">→</button><button id="south" title="Nach Süden">↓</button><button id="zoomIn" title="Vergrößern">+</button><button id="zoomOut" title="Verkleinern">−</button></div>
  <div id="info" aria-live="polite"><h3 id="selectedName"></h3><p id="selectedDetails"></p><a id="teleport" rel="noopener noreferrer" target="_blank">In diese Region teleportieren ↗</a></div>
  <div id="coords" aria-live="polite">–</div>
</section>
</main>
<script>
(() => {
  'use strict';
  const $=id=>document.getElementById(id), world=$('world'), layers=$('layers');
  const status=$('status'), results=$('results'), info=$('info');
  let center={x:1000,y:1000}, axis=12, bounds=null, requestId=0, drag=null;
  const axisChoices=[3,5,8,12,16];
  const clamp=n=>Math.max(0,Math.min(8388606,Math.round(n)));
  function view(){
    const half=Math.floor(axis/2), minX=clamp(center.x-half), minY=clamp(center.y-half);
    return {min_x:minX,max_x:Math.min(8388606,minX+axis-1),min_y:minY,max_y:Math.min(8388606,minY+axis-1)};
  }
  function setStatus(t){status.textContent=t;}
  function add(tag,cl,parent,text){
    const e=document.createElement(tag);if(cl)e.className=cl;if(text!==undefined)e.textContent=text;
    parent.appendChild(e);return e;
  }
  function select(r){
    $('selectedName').textContent=r.name;
    $('selectedDetails').textContent='Raster '+r.grid_x+' / '+r.grid_y+' · '+r.size_x+' × '+r.size_y+' m · Reifegrad '+(['Allgemein','Moderat','Adult'][r.maturity]||r.maturity);
    const link=$('teleport');
    if(r.teleport_uri){link.href=r.teleport_uri;link.style.display='inline';}
    else{link.removeAttribute('href');link.style.display='none';}
    info.style.display='block';
  }
  function draw(data){
    if(!data.bounds)return;
    bounds=data.bounds;
    const b=bounds, rect=world.getBoundingClientRect();
    const cellsX=b.max_x-b.min_x+1,cellsY=b.max_y-b.min_y+1;
    const unit=Math.min(rect.width/cellsX,rect.height/cellsY);
    const width=cellsX*unit,height=cellsY*unit, left=(rect.width-width)/2,top=(rect.height-height)/2;
    layers.replaceChildren();
    const pos=(x,y)=>({x:left+(x-b.min_x)*unit,y:top+(b.max_y-y)*unit});
    for(let y=b.min_y;y<=b.max_y;y++){
      for(let x=b.min_x;x<=b.max_x;x++){
        const p=pos(x,y),cell=add('div','tile',layers);
        Object.assign(cell.style,{left:p.x+'px',top:p.y+'px',width:unit+'px',height:unit+'px'});
        const im=document.createElement('img');im.loading='lazy';im.decoding='async';im.alt='';
        im.src='/map/map-1-'+x+'-'+y+'-objects.jpg';
        im.onerror=()=>im.remove();cell.appendChild(im);
      }
    }
    for(const r of (data.regions||[])){
      const p=pos(r.grid_x,r.grid_y+r.cells_y-1);
      const card=add('div','region',layers);
      Object.assign(card.style,{left:p.x+'px',top:p.y+'px',width:(r.cells_x*unit)+'px',height:(r.cells_y*unit)+'px'});
      const button=add('button','',card,r.name);button.type='button';
      button.title=r.name+' ansehen';button.onclick=()=>select(r);
    }
    $('coords').textContent='Raster '+b.min_x+'–'+b.max_x+' / '+b.min_y+'–'+b.max_y+' · '+(data.regions||[]).length+' Regionen';
  }
  async function load(defaultView=false){
    const current=++requestId;
    const v=view();const p=new URLSearchParams(defaultView?{}:v);
    setStatus('Kartenbereich wird geladen …');
    try{
      const response=await fetch('/api/v1/world-map'+(p.size?'?'+p:''),{cache:'no-store'});
      if(!response.ok)throw Error('HTTP '+response.status);
      const data=await response.json();if(current!==requestId)return;
      if(!data.bounds){setStatus('Keine Standardregion gefunden. Bitte nach einer Region suchen.');return;}
      center={x:Math.floor((data.bounds.min_x+data.bounds.max_x)/2),y:Math.floor((data.bounds.min_y+data.bounds.max_y)/2)};
      draw(data);setStatus((data.regions||[]).length+' Regionen im aktuellen Ausschnitt.');
    }catch(error){if(current===requestId)setStatus('Kartenfehler: '+error.message);}
  }
  async function search(event){
    event.preventDefault();
    const q=$('search').value.trim();
    if(q.length<2){setStatus('Bitte mindestens zwei Zeichen eingeben.');return;}
    setStatus('Suche läuft …');results.replaceChildren();
    try{
      const response=await fetch('/api/v1/world-map?q='+encodeURIComponent(q),{cache:'no-store'});
      if(!response.ok)throw Error('HTTP '+response.status);
      const data=await response.json(), found=data.regions||[];
      setStatus(found.length+' passende Regionen gefunden.');
      for(const r of found){
        const button=add('button','result',results);button.type='button';
        add('strong','',button,r.name);add('span','',button,'Raster '+r.grid_x+' / '+r.grid_y);
        button.onclick=()=>{center={x:r.grid_x,y:r.grid_y};select(r);load();};
      }
      if(found.length===1)results.firstChild.click();
    }catch(error){setStatus('Suche fehlgeschlagen: '+error.message);}
  }
  $('searchForm').addEventListener('submit',search);
  $('north').onclick=()=>{center.y=clamp(center.y+3);load();};
  $('south').onclick=()=>{center.y=clamp(center.y-3);load();};
  $('east').onclick=()=>{center.x=clamp(center.x+3);load();};
  $('west').onclick=()=>{center.x=clamp(center.x-3);load();};
  $('zoomIn').onclick=()=>{axis=axisChoices[Math.max(0,axisChoices.indexOf(axis)-1)];load();};
  $('zoomOut').onclick=()=>{axis=axisChoices[Math.min(axisChoices.length-1,axisChoices.indexOf(axis)+1)];load();};
  world.addEventListener('pointerdown',e=>{if(e.button!==0||e.target.closest('button'))return;drag={x:e.clientX,y:e.clientY,centerX:center.x,centerY:center.y};world.setPointerCapture(e.pointerId);world.classList.add('dragging');});
  world.addEventListener('pointerup',e=>{
    if(!drag)return;
    const moved=Math.hypot(e.clientX-drag.x,e.clientY-drag.y);
    if(moved>7){
      const size=Math.min(world.clientWidth,world.clientHeight)/axis;
      center.x=clamp(drag.centerX-(e.clientX-drag.x)/size);
      center.y=clamp(drag.centerY+(e.clientY-drag.y)/size);
      layers.style.transform='';load();
    }
    drag=null;world.classList.remove('dragging');
  });
  world.addEventListener('pointercancel',()=>{drag=null;layers.style.transform='';world.classList.remove('dragging');});
  world.addEventListener('pointermove',e=>{
    if(!drag)return;
    layers.style.transform='translate('+(e.clientX-drag.x)+'px,'+(e.clientY-drag.y)+'px)';
  });
  world.addEventListener('wheel',e=>{e.preventDefault();if(e.deltaY<0)$('zoomIn').click();else $('zoomOut').click();},{passive:false});
  addEventListener('resize',()=>{if(bounds)load();});
  load(true);
})();
</script>
</body></html>
""";
    }
}
