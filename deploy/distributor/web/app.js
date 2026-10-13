"use strict";
const $ = (id) => document.getElementById(id);
let snapshot = null, selectedTag = null, repository = "all", refreshing = false, submitting = false, warningNotice = false, logView = "auto";
const expandedPackages = new Map();
const date = (value) => {
  if (!value) return "—";
  const d = typeof value === "number" ? new Date(value * 1000) : new Date(value);
  return Number.isNaN(d.getTime()) ? String(value) : d.toLocaleString(undefined, {day:"2-digit",month:"short",hour:"2-digit",minute:"2-digit"});
};
const size = (bytes) => bytes >= 1048576 ? `${(bytes/1048576).toFixed(1)} MiB` : bytes >= 1024 ? `${(bytes/1024).toFixed(1)} KiB` : `${bytes} B`;
const interval = (value) => /^\d+min$/.test(value) ? `${parseInt(value,10)} minutes` : value;
function cell(value, className = "") { const td = document.createElement("td"); td.textContent = value; td.className = className; return td; }
function empty(body, message) { const tr = document.createElement("tr"), td = cell(message,"empty"); td.colSpan=5; tr.append(td); body.append(tr); }
function notice(message, error = false) { $("notice").textContent=message; $("notice").hidden=!message; $("notice").classList.toggle("error",error); }
async function request(path, options = {}) {
  let response, text;
  try { response = await fetch(`./api/${path}`, {...options, credentials:"same-origin", cache:"no-store", signal:AbortSignal.timeout(15000)}); text=await response.text(); }
  catch(error) { throw new Error(`${error.name==="TimeoutError"?"Request timed out":"Browser could not fetch distributor data"}. Refresh the page or open the output link to check access.`); }
  let data;
  try { data=JSON.parse(text); if(!data || typeof data!=="object" || Array.isArray(data))throw new Error("invalid response"); }
  catch(error) { throw new Error(`HTTP ${response.status}: expected JSON; check the proxy route and login.`); }
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}
function compareVersions(a,b) {
  const left=a.version.split(".").map(Number), right=b.version.split(".").map(Number);
  for(let i=0;i<Math.max(left.length,right.length);i++) { const difference=(right[i] || 0)-(left[i] || 0); if(difference)return difference; }
  return a.version.localeCompare(b.version);
}
function packages() {
  const body=$("packages"), focusedKey=document.activeElement?.dataset.packageKey; body.replaceChildren();
  const query=$("filter").value.trim().toLowerCase(), groups=new Map();
  for(const item of snapshot?.packages || []) {
    if(repository!=="all" && item.repository!==repository)continue;
    const key=`${item.repository}/${item.name}`;
    if(!groups.has(key))groups.set(key,[]);
    groups.get(key).push(item);
  }
  let visible=0;
  for(const [key,versions] of groups) {
    versions.sort(compareVersions);
    const matches=versions.filter(item=>`${item.name} ${item.version}`.toLowerCase().includes(query));
    if(!matches.length)continue;
    visible++;
    const expandedKey=`${key}\u0000${query}`;
    const latest=versions[0], expanded=expandedPackages.has(expandedKey) ? expandedPackages.get(expandedKey) : Boolean(query), row=document.createElement("tr");
    row.className="package-group";
    const name=cell(""), button=document.createElement("button"), count=document.createElement("span");
    button.dataset.packageKey=key; button.className="package-toggle"; button.textContent=`${expanded?"▾":"▸"} ${latest.name}`;
    button.setAttribute("aria-expanded",String(expanded));
    button.setAttribute("aria-label",`${expanded?"Hide":"Show"} versions for ${latest.name} in ${latest.repository}`);
    count.className="version-count"; count.textContent=`${versions.length} version${versions.length===1?"":"s"}`;
    name.append(button,count);
    const toggle=()=>{expandedPackages.set(expandedKey,!expanded);packages();};
    button.addEventListener("click",toggle);
    row.addEventListener("click",event=>{if(!event.target.closest("button"))toggle();});
    row.append(cell(latest.repository),name,cell(latest.version),cell(size(latest.bytes)),cell(date(latest.published)));body.append(row);
    if(focusedKey===key)button.focus();
    if(expanded)for(const item of matches) {
      const child=document.createElement("tr"); child.className="package-version";
      child.append(cell(""),cell("Released version"),cell(item.version),cell(size(item.bytes)),cell(date(item.published)));body.append(child);
    }
  }
  if(!visible)empty(body,"No published packages match this filter.");
}
function builds() {
  const body=$("builds"); body.replaceChildren();
  if (!selectedTag || !snapshot.builds.some(item=>item.tag===selectedTag)) selectedTag=snapshot.builds.find(item=>item.status==="building")?.tag || snapshot.builds[0]?.tag || null;
  for (const item of snapshot.builds) {
    const tr=document.createElement("tr"); tr.classList.toggle("selected-row",item.tag===selectedTag);
    const status=cell(""); const label=document.createElement("span"); label.textContent=item.status.charAt(0).toUpperCase()+item.status.slice(1); label.className=`status ${["building","complete","failed","interrupted"].includes(item.status)?item.status:""}`; status.append(label); status.title=item.error;
    const action=cell(""); const button=document.createElement("button"); button.textContent="View log"; button.setAttribute("aria-label",`View log for ${item.tag}`);
    button.addEventListener("click",()=>{selectedTag=item.tag; logView="auto"; builds(); updateLog();}); action.append(button);
    tr.append(cell(item.tag),cell(item.commit.slice(0,7),"commit"),status,cell(date(item.started)),action); body.append(tr);
  }
  if (!snapshot.builds.length) empty(body,"No active or recent tagged builds. Run now checks for release tags.");
}
async function updateLog() {
  $("log-title").textContent=selectedTag ? `Build log · ${selectedTag}` : "Build log";
  $("log-hint").textContent=selectedTag ? "Live phase output · refreshes every 5 seconds" : "Select a build to view its log";
  $("log-open").hidden=!selectedTag;
  if (!selectedTag) { $("log").textContent="No build selected."; $("phase-summary").textContent=""; $("phase-diagnostic").hidden=true; return; }
  const tag=selectedTag, view=logView;
  const path=`build-output?build=${encodeURIComponent(tag.replace("/","-"))}&view=${encodeURIComponent(logView)}`;
  $("log-open").href=`./api/${path}`;
  try { const data=await request(path); if(tag!==selectedTag || view!==logView)return;
    const select=$("log-view"); select.replaceChildren();
    for(const [value,label] of [["auto","Current phase"],["controller","Controller history"],...(data.phases || []).map((phase,index)=>[String(index),`${phase.name} · ${phase.status}`])]) {
      const option=document.createElement("option"); option.value=value; option.textContent=label; select.append(option);
    }
    select.value=logView;
    const phase=(data.phases || [])[data.selected], build=snapshot?.builds.find(item=>item.tag===tag);
    const elapsed=phase ? `${Math.floor(phase.elapsed/60)}m ${Math.floor(phase.elapsed%60)}s` : "";
    const age=phase?.updated ? Math.max(0,Math.floor(Date.now()/1000-phase.updated)) : null;
    $("phase-summary").textContent=phase ? `Build ${build?.status || "unknown"} · ${phase.name} · ${phase.status} · ${elapsed} · ${age===null?"waiting for output":`last output ${age}s ago`}` : "Detailed phase output is available for builds started with the updated controller.";
    $("phase-diagnostic").hidden=!data.diagnostic;
    $("phase-diagnostic").textContent=data.diagnostic ? `${data.diagnostic.includes("falling back to tcg")?"Software emulation: KVM unavailable in this guest.\n":"Guest diagnostics\n"}${data.diagnostic}` : "";
    const log=$("log"), atBottom=log.scrollTop+log.clientHeight>=log.scrollHeight-20;
    if (log.textContent!==data.text) { log.textContent=data.text; if(atBottom)log.scrollTop=log.scrollHeight; }
  } catch(error) { if(tag===selectedTag)$("log").textContent=error.message; }
}
async function refresh() {
  if(refreshing)return; refreshing=true;
  try {
    snapshot=await request("status"); $("connection").textContent="Connected"; $("connection").classList.add("online");
    $("interval").textContent=`${snapshot.timer.interval ? `Every ${interval(snapshot.timer.interval)}` : "Not configured"}${snapshot.timer.state!=="active"?" · "+snapshot.timer.state:""}`;
    $("last").textContent=date(snapshot.timer.last_run); $("next").textContent=snapshot.timer.next_run ? date(snapshot.timer.next_run) : snapshot.busy ? "After current run" : "—";
    $("run").disabled=submitting || snapshot.busy || snapshot.service.LoadState!=="loaded"; $("run").textContent=snapshot.busy ? "Running…" : "Run now";
    if(snapshot.warnings.length){notice(snapshot.warnings.join("\n"),true);warningNotice=true;}
    else if(warningNotice){notice("");warningNotice=false;}
    builds(); packages(); $("refreshed").textContent=`Last refreshed: ${date(snapshot.refreshed)}`;
    await updateLog();
  } catch(error) { $("connection").textContent="Disconnected"; $("connection").classList.remove("online"); $("run").disabled=true; notice(error.message,true); warningNotice=true; }
  finally {refreshing=false;}
}
$("run").addEventListener("click",async()=>{
  if(!snapshot || submitting)return; submitting=true; $("run").disabled=true;
  try { const data=await request("run",{method:"POST",headers:{"Content-Type":"application/json","X-Zenith-CSRF":snapshot.csrf},body:"{}"}); notice(data.message); }
  catch(error){notice(error.message,true);} finally{submitting=false; await refresh();}
});
$("log-view").addEventListener("change",()=>{logView=$("log-view").value; updateLog();});
$("filter").addEventListener("input",packages);
document.querySelectorAll("[data-repo]").forEach(button=>button.addEventListener("click",()=>{
  repository=button.dataset.repo; document.querySelectorAll("[data-repo]").forEach(item=>{const active=item===button;item.classList.toggle("selected",active);item.setAttribute("aria-pressed",String(active));}); packages();
}));
refresh(); setInterval(()=>{if(!document.hidden)refresh();},5000);
document.addEventListener("visibilitychange",()=>{if(!document.hidden)refresh();});
