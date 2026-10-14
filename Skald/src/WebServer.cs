using System;
using System.Net;
using System.Text;
using System.Threading;

namespace Skald
{
    /// <summary>Tiny HTTP server on a background thread. Never touches game objects; only reads EventLog.</summary>
    internal sealed class WebServer
    {
        readonly HttpListener _listener = new HttpListener();
        readonly string _prefix;
        Thread _thread;
        volatile bool _running;

        public WebServer(string bind, int port)
        {
            _prefix = $"http://{bind}:{port}/";
            _listener.Prefixes.Add(_prefix);
        }

        public void Start()
        {
            try
            {
                _listener.Start();
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "Skald-HTTP" };
                _thread.Start();
                Plugin.Log.LogInfo("Dashboard listening on " + _prefix);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Dashboard disabled, could not listen on {_prefix}: {ex.Message}");
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); _listener.Close(); } catch { }
        }

        void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (!_running) return; continue; }
                try { Handle(ctx); }
                catch (Exception ex) { Plugin.Log.LogWarning("HTTP error: " + ex.Message); }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            long t0 = Perf.Now();
            try { Route(ctx, path); }
            finally { Perf.Request(path, t0, (int)Math.Min(int.MaxValue, ctx.Response.ContentLength64)); }
        }

        static void Route(HttpListenerContext ctx, string path)
        {
            if (path == "/api/perf")
            {
                Write(ctx, "application/json", Perf.Json());
                return;
            }
            if (path.StartsWith("/api/admin/"))
            {
                HandleAdmin(ctx, path.Substring("/api/admin/".Length));
                return;
            }
            if (path == "/api/bosses")
                Write(ctx, "application/json", Bosses.Json());
            else if (path == "/api/flags")
                Write(ctx, "application/json", Flags.Json());
            else if (path == "/api/status")
                Write(ctx, "application/json", Admin.StatusJson());
            else if (path == "/api/events")
                Write(ctx, "application/json", EventLog.RecentJson(500));
            else if (path == "/" || path == "/index.html")
                Write(ctx, "text/html; charset=utf-8", Page);
            else
            {
                ctx.Response.StatusCode = 404;
                Write(ctx, "text/plain", "not found");
            }
        }

        static void HandleAdmin(HttpListenerContext ctx, string action)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 405;
                Write(ctx, "text/plain", "use POST");
                return;
            }
            if (!Admin.CheckToken(ctx.Request.Headers["X-Skald-Token"]))
            {
                ctx.Response.StatusCode = 403;
                Write(ctx, "application/json", "{\"ok\":false,\"error\":\"wrong or missing admin token\"}");
                return;
            }
            string who = "dashboard admin @" + ctx.Request.RemoteEndPoint?.Address;
            switch (action)
            {
                case "save":
                    Admin.RequestSave(who);
                    break;
                case "restart":
                    int.TryParse(ctx.Request.QueryString["delay"], out int delay);
                    Admin.RequestRestart(delay, who);
                    break;
                case "cancel":
                    Admin.RequestCancel(who);
                    break;
                case "dismiss":
                    if (!Flags.Dismiss(ctx.Request.QueryString["player"], ctx.Request.QueryString["reason"]))
                    {
                        ctx.Response.StatusCode = 404;
                        Write(ctx, "application/json", "{\"ok\":false,\"error\":\"no such flag\"}");
                        return;
                    }
                    break;
                case "trust":
                    Flags.SetTrusted(ctx.Request.QueryString["player"], ctx.Request.QueryString["on"] == "1");
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    Write(ctx, "application/json", "{\"ok\":false,\"error\":\"unknown action\"}");
                    return;
            }
            Write(ctx, "application/json", "{\"ok\":true}");
        }

        static void Write(HttpListenerContext ctx, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = type;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        const string Page = @"<!doctype html>
<html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>
<title>Skald</title>
<style>
:root{--bg:#f6f4ef;--fg:#1d1b17;--mut:#6b655a;--line:#ddd6c8;--acc:#8a5a14}
@media (prefers-color-scheme:dark){:root{--bg:#16140f;--fg:#ece6da;--mut:#9a927f;--line:#332e25;--acc:#e0a84a}}
body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif}
main{max-width:1100px;margin:0 auto;padding:16px}
h1{font-size:20px;margin:4px 0 2px}p{color:var(--mut);margin:0 0 12px}
select{background:var(--bg);color:var(--fg);border:1px solid var(--line);padding:4px 6px;border-radius:6px}
table{width:100%;border-collapse:collapse;margin-top:10px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--mut);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.04em}
td.t{white-space:nowrap;color:var(--mut);font-variant-numeric:tabular-nums}
.k{font-weight:600;color:var(--acc)}
.admin{border:1px solid var(--line);border-radius:10px;padding:12px;margin:12px 0}
.admin h2{font-size:15px;margin:0 0 8px}.row{display:flex;flex-wrap:wrap;gap:8px;align-items:center}
input,button{font:inherit;background:var(--bg);color:var(--fg);border:1px solid var(--line);border-radius:6px;padding:5px 9px}
button{cursor:pointer}button.warn{border-color:var(--acc);color:var(--acc);font-weight:600}
#cd{font-weight:600;color:var(--acc)}
:root{--bad:#b3261e;--badbg:#fbe9e7}
@media (prefers-color-scheme:dark){:root{--bad:#ff8a80;--badbg:#3a1714}}
.banner{border-radius:10px;padding:12px 14px;margin:12px 0;border:1px solid var(--acc);display:flex;flex-direction:column;gap:8px}
.banner.fight{border-color:var(--bad);background:var(--badbg)}
.banner b{font-size:16px}.bar{height:10px;border-radius:5px;background:var(--line);overflow:hidden}
.bar i{display:block;height:100%;background:var(--bad)}
.chips{display:flex;flex-wrap:wrap;gap:6px;margin:6px 0 10px}.chip{border:1px solid var(--line);border-radius:999px;padding:3px 10px;font-size:13px;color:var(--mut)}
.chip.done{border-color:var(--acc);color:var(--acc);font-weight:600}
h2.sec{font-size:15px;margin:18px 0 4px}
.flags{border:1px solid var(--bad);background:var(--badbg);border-radius:10px;padding:12px;margin:12px 0}
.flags h2{font-size:15px;margin:0 0 8px;color:var(--bad)}
.flags.calm{border-color:var(--line);background:transparent}.flags.calm h2{color:var(--mut)}
.flags ul{margin:0;padding-left:18px}.flags li{margin:2px 0}
.flags button{font-size:12px;padding:2px 8px;margin-left:6px}.trusted{color:var(--mut)}.trusted .flag{color:var(--mut)}
.flag{color:var(--bad);font-weight:700}
tr.cheat td{background:var(--badbg)}tr.cheat td.k{color:var(--bad)}#msg{color:var(--mut)}
.wrap{overflow-x:auto}
</style></head><body><main>
<h1>Skald: server audit log</h1>
<p>Live events from this server. Refreshes every 5 seconds.</p>
<section class=admin><h2>Admin</h2>
<div class=row><input id=tok type=password placeholder='Admin token (BepInEx/config/skald.servermetrics.cfg)' size=34>
<button id=bsave>Save now</button>
<select id=delay><option value=0>Now</option><option value=60 selected>In 1 minute</option><option value=300>In 5 minutes</option><option value=600>In 10 minutes</option></select>
<button id=brestart class=warn>Save &amp; restart</button><button id=bcancel>Cancel restart</button></div>
<p class=row><span id=cd></span><span id=msg></span></p></section>
<div id=bossbanners></div>
<section class=flags id=flagbox hidden><h2>&#9873; Flagged players</h2>
<p>From Valheim's own cheat tracking plus server-side checks (cheat tools, teleports, bosses spawned away from their altar). Nothing is blocked; this is for admins to review. Dismissed flags come back if it happens again.</p>
<div id=flaglist></div></section>
<h2 class=sec>Bosses</h2><div class=chips id=prog></div>
<div class=wrap><table><thead><tr><th>When</th><th>Boss</th><th>Outcome</th><th>Fight time</th><th>Killing blow</th><th>Fighters</th><th>Summoned by</th></tr></thead>
<tbody id=bossrows></tbody></table></div>
<h2 class=sec>Event log</h2>
<label>Show <select id=f><option value=''>everything</option><option>chest_take</option><option>chest_add</option>
<option>death</option><option>grave_take</option><option>built</option><option>removed</option><option>destroyed</option><option>cheat_flag</option><option>admin_restart</option><option>admin_save</option>
<option>boss_summoned</option><option>boss_killed</option><option>boss_defeated</option><option>join</option><option>leave</option></select></label>
<div class=wrap><table><thead><tr><th>When</th><th>Event</th><th>Who</th><th>What</th><th>Details</th><th>Where</th></tr></thead>
<tbody id=rows></tbody></table></div>
</main><script>
const esc=s=>(s==null?'':String(s)).replace(/[&<>""]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c]));
const lastHtml={};
function setHtml(id,html){if(lastHtml[id]===html)return;lastHtml[id]=html;document.getElementById(id).innerHTML=html;}
let flagged=new Set();
async function loadFlags(){
  try{
    const fl=await (await fetch('/api/flags')).json();
    const open=fl.filter(f=>!f.trusted&&f.reasons.some(r=>!r.dismissed)), trusted=fl.filter(f=>f.trusted);
    flagged=new Set(open.map(f=>f.player.toLowerCase()));
    const box=document.getElementById('flagbox');box.hidden=open.length===0&&trusted.length===0;box.classList.toggle('calm',open.length===0);
    const admin=!!tok.value, q=encodeURIComponent;
    setHtml('flaglist',open.map(f=>
      `<p><span class=flag>&#9873; ${esc(f.player)}</span>${f.actorId?' <span class=t>('+esc(f.actorId)+')</span>':''}`+
      (admin?`<button data-a='trust' data-p='${q(f.player)}'>Trust (known admin)</button><button data-a='dismiss' data-p='${q(f.player)}'>Dismiss all</button>`:'')+`</p><ul>`+
      f.reasons.filter(r=>!r.dismissed).map(r=>`<li><b>${esc(r.reason)}</b> &middot; ${r.count}&times; &middot; last ${new Date(r.last).toLocaleString()}`+
        (r.example?` &middot; <i>${esc(r.example)}</i>`:'')+
        (admin?`<button data-a='dismiss' data-p='${q(f.player)}' data-r='${q(r.reason)}'>Dismiss</button>`:'')+'</li>').join('')+'</ul>').join('')+
      (trusted.length?`<p class=trusted>Trusted: `+trusted.map(f=>`<span class=flag>&#9873;</span> ${esc(f.player)} (${f.reasons.reduce((n,r)=>n+r.count,0)} flags)`+
        (admin?`<button data-a='untrust' data-p='${q(f.player)}'>Untrust</button>`:'')).join(' &middot; ')+'</p>':''));
  }catch(e){}
}
document.getElementById('flaglist').onclick=async ev=>{
  const b=ev.target.closest('button');if(!b)return;
  const p=b.dataset.p, r=b.dataset.r, a=b.dataset.a;
  const url=a==='dismiss'?`/api/admin/dismiss?player=${p}`+(r?`&reason=${r}`:''):`/api/admin/trust?player=${p}&on=${a==='trust'?1:0}`;
  try{const res=await fetch(url,{method:'POST',headers:{'X-Skald-Token':tok.value}});const j=await res.json();
    if(!j.ok)document.getElementById('msg').textContent='Refused: '+j.error;}catch(e){}
  loadFlags();load();
};
const who=a=>a&&flagged.has(String(a).toLowerCase())?`<span class=flag title='Cheat flags on this character'>&#9873;</span> ${esc(a)}`:esc(a);
async function load(){
  await loadFlags();
  try{
    const ev=await (await fetch('/api/events')).json(), f=document.getElementById('f').value;
    setHtml('rows',ev.filter(e=>!f||e.type===f).map(e=>
      `<tr${e.type==='cheat_flag'?' class=cheat':''}><td class=t>${new Date(e.t).toLocaleString()}</td><td class=k>${esc(e.type)}</td><td>${who(e.actor)}</td>`+
      `<td>${esc(e.target)}</td><td>${esc(e.detail)}</td><td class=t>${e.pos.every(v=>v===0)?'':e.pos.map(Math.round).join(', ')}</td></tr>`).join(''));
  }catch(e){}
}
const tok=document.getElementById('tok');try{tok.value=localStorage.getItem('skaldTok')||''}catch(e){}
tok.onchange=()=>{try{localStorage.setItem('skaldTok',tok.value)}catch(e){}};
async function admin(action,q){
  const m=document.getElementById('msg');
  if(action==='restart'&&!confirm('Save the world and restart the server? Players get an in-game countdown.'))return;
  try{const r=await fetch('/api/admin/'+action+(q||''),{method:'POST',headers:{'X-Skald-Token':tok.value}});
    const j=await r.json();m.textContent=j.ok?'Sent: '+action:'Refused: '+j.error;}catch(e){m.textContent='Server not reachable (restarting?)';}
  status();
}
let counting=false;
async function status(){
  try{const s=await (await fetch('/api/status')).json();counting=s.restartSecondsLeft!=null;
    document.getElementById('cd').textContent=counting?'Restart in '+s.restartSecondsLeft+'s':'';}
  catch(e){counting=true;document.getElementById('cd').textContent='Server offline or restarting…';}
  setTimeout(status,counting?1000:5000);
}
document.getElementById('bsave').onclick=()=>admin('save');
document.getElementById('brestart').onclick=()=>admin('restart','?delay='+document.getElementById('delay').value);
document.getElementById('bcancel').onclick=()=>admin('cancel');
status();
const mmss=s=>Math.floor(s/60)+':'+String(s%60).padStart(2,'0');
async function bosses(){
  try{
    const b=await (await fetch('/api/bosses')).json();
    fighting=b.active.some(f=>f.fighting);
    setHtml('bossbanners',b.active.map(f=>
      `<div class='banner${f.fighting?' fight':''}'><b>${f.fighting?'&#9876; Boss fight in progress':'&#9760; Boss alive, nobody fighting'}: ${esc(f.boss)}</b>`+
      (f.hpPct!=null?`<div class=bar><i style='width:${f.hpPct}%'></i></div><span>${f.hpPct}% HP`:'<span>')+
      ` &middot; ${f.fighting?'fight time '+mmss(f.seconds):'at '+f.pos[0]+', '+f.pos[2]}`+
      (f.fighters.length?` &middot; fighters: ${f.fighters.map(who).join(', ')}`:'')+
      (f.summoner?` &middot; summoned by ${who(f.summoner)}`:'')+'</span></div>').join(''));
    setHtml('prog',b.progression.map(p=>
      `<span class='chip${p.defeated?' done':''}'>${p.defeated?'&#10003; ':''}${esc(p.boss)}</span>`).join(''));
    setHtml('bossrows',b.history.length?b.history.map(f=>
      `<tr><td class=t>${new Date(f.end||f.start).toLocaleString()}</td><td class=k>${esc(f.boss)}</td><td>${esc(f.outcome)}</td>`+
      `<td class=t>${mmss(f.seconds)}</td><td>${f.killer?who(f.killer):''}</td><td>${f.fighters.map(who).join(', ')}</td><td>${f.summoner?who(f.summoner):''}</td></tr>`).join('')
      :'<tr><td colspan=7 class=t>No boss fights recorded yet.</td></tr>');
  }catch(e){}
  setTimeout(bosses,fighting?2000:5000);
}
let fighting=false;bosses();
document.getElementById('f').onchange=load;load();setInterval(load,5000);
</script></body></html>";
    }
}
