import json, logging, os, re, time
from datetime import datetime, timezone
import azure.functions as func
import requests
from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient

app = func.FunctionApp()
UTC = timezone.utc
GRAPH = "https://graph.microsoft.com/v1.0"
SITE = os.getenv("SHAREPOINT_SITE_ID", "stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f")
LISTS = {"vehicles": os.getenv("VEHICLES_LIST_ID", "8d96865a-b7dc-40c0-9947-468542800bd4"), "trailers": os.getenv("TRAILERS_LIST_ID", "a2fb1103-b9fe-48a8-90fd-9f6fcdd2d74c"), "drivers": os.getenv("DRIVERS_LIST_ID", "2b89c3fc-d83e-424b-81b9-8e2546fabb78"), "log": os.getenv("SYNC_LOG_LIST_ID", "ff965828-ca04-4374-aec9-b8b500e234b1")}
cred = DefaultAzureCredential()

def norm(v): return re.sub(r"[^A-Z0-9]", "", str(v or "").upper())
def clean(v): return None if v is None or str(v).strip() == "" else str(v).strip()
def now(): return datetime.now(UTC).isoformat()

class Http:
    def __init__(self, headers=None): self.s=requests.Session(); self.s.headers.update(headers or {})
    def request(self, method, url, **kw):
        for attempt in range(6):
            r=self.s.request(method,url,timeout=45,**kw)
            if r.status_code not in (429,500,502,503,504): r.raise_for_status(); return r
            time.sleep(min(int(r.headers.get("Retry-After","0") or 0) or 2**attempt,30))
        r.raise_for_status()

class Graph:
    def __init__(self): self.h=Http()
    def req(self, method, path, **kw):
        token=cred.get_token("https://graph.microsoft.com/.default").token
        headers=kw.pop("headers",{}); headers["Authorization"]="Bearer "+token
        return self.h.request(method, GRAPH+path, headers=headers, **kw)
    def pages(self,path):
        out=[]; url=GRAPH+path
        while url:
            token=cred.get_token("https://graph.microsoft.com/.default").token
            data=self.h.request("GET",url,headers={"Authorization":"Bearer "+token}).json()
            out += data.get("value",[]); url=data.get("@odata.nextLink")
        return out
    def columns(self,lid): return self.pages(f"/sites/{SITE}/lists/{lid}/columns?$select=id,name,displayName")
    def items(self,lid): return self.pages(f"/sites/{SITE}/lists/{lid}/items?$expand=fields&$top=999")
    def add_column(self,lid,name,kind="text"):
        body={"name":name,"displayName":name,kind:{}}
        return self.req("POST",f"/sites/{SITE}/lists/{lid}/columns",json=body).json()
    def create(self,lid,fields): return self.req("POST",f"/sites/{SITE}/lists/{lid}/items",json={"fields":fields}).json()
    def update(self,lid,iid,fields): return self.req("PATCH",f"/sites/{SITE}/lists/{lid}/items/{iid}/fields",json=fields).json()

def secret(name):
    vault=os.getenv("KEY_VAULT_URL","https://slhtmskvprod32190.vault.azure.net/")
    return SecretClient(vault_url=vault,credential=cred).get_secret(name).value

def fleetio_assets():
    h=Http({"Authorization":"Token "+secret("fleetio-api-key"),"Account-Token":secret("fleetio-account-token"),"X-Api-Version":"2024-01-01","Accept":"application/json"})
    url="https://secure.fleetio.com/api/v1/vehicles?per_page=100"; out=[]
    while url:
        r=h.request("GET",url); data=r.json(); out += data if isinstance(data,list) else data.get("records",data.get("data",[]))
        url=r.links.get("next",{}).get("url")
        if not url and isinstance(data,dict): url=data.get("next_page_url")
    return out

def sage_people():
    h=Http({"X-Auth-Token":secret("sage-api-key"),"User-Agent":"SLH-SharePoint-Sync/1.0","Accept":"application/json"}); out=[]; page=1
    while True:
        data=h.request("GET",f"https://stuartlyonshaulage.sage.hr/api/employees?page={page}").json()
        rows=data.get("data",data.get("employees",[])) if isinstance(data,dict) else data
        out+=rows
        if not rows or (isinstance(data,dict) and page>=int(data.get("meta",{}).get("last_page",page))): break
        page+=1
    return out

def fleet_kind(a):
    t=" ".join(str(a.get(k) or "") for k in ("vehicle_type_name","type_name","name","group_name")).upper()
    if "TRAILER" in t: return "trailers"
    if any(x in t for x in ("UNIT","TRUCK","TRACTOR","HGV","RIGID","VAN")): return "vehicles"
    return None

def sage_driver(p):
    s=" ".join(str(p.get(k) or "") for k in ("team","team_name","position","position_name","job_title")).lower()
    return any(x in s for x in ("driver","hgv","class 1","class 2"))

def trailer_id(a):
    value=norm(a.get("name") or a.get("license_plate"))
    value=re.sub(r"^SLH", "", value)
    value=re.sub(r"NEW$", "", value)
    return value.lstrip("0") or "0"

def trailer_norm(v):
    value=norm(v)
    value=re.sub(r"^SLH", "", value)
    value=re.sub(r"NEW$", "", value)
    return value.lstrip("0") or "0"

def fleet_status(a): return clean(a.get("vehicle_status_name") or a.get("status_name") or a.get("status"))
def fleet_active(status):
    if not status: return None
    return "No" if any(x in status.lower() for x in ("inactive","sold","disposed","retired","decommission")) else "Yes"

COLS={
 "vehicles":["FleetioId","FleetioName","FleetioVIN","FleetioMake","FleetioModel","FleetioType","FleetioLastSync"],
 "trailers":["FleetioId","FleetioName","FleetioVIN","FleetioMake","FleetioModel","FleetioType","FleetioLastSync"],
 "drivers":["SageHrId","SageEmployeeNumber","SageEmploymentStatus","SageEmail","SageMobile","SageLastSync"],
 "log":["Source","Status","StartedAt","FinishedAt","Matched","CreatedCount","UpdatedCount","Ambiguous","ErrorCount","Details"]}

def ensure_columns(g,key):
    existing={c["displayName"]:c["name"] for c in g.columns(LISTS[key])}
    for name in COLS[key]:
        if name not in existing:
            c=g.add_column(LISTS[key],name); existing[name]=c["name"]
    return existing

def field_names(g,key,dry):
    cols=g.columns(LISTS[key]); names={c["displayName"]:c["name"] for c in cols}
    if not dry: names=ensure_columns(g,key)
    for n in COLS[key]: names.setdefault(n,n)
    return names

def choose(items, source_field, source_id, fallback_field, fallback):
    exact=[x for x in items if norm(x["fields"].get(source_field))==norm(source_id)] if source_id else []
    if len(exact)==1:return exact[0],"source"
    if len(exact)>1:return None,"ambiguous"
    fb=[x for x in items if norm(x["fields"].get(fallback_field))==norm(fallback)] if fallback else []
    if len(fb)==1:return fb[0],"fallback"
    return (None,"ambiguous") if len(fb)>1 else (None,"new")

def delta(old, desired):
    return {k:v for k,v in desired.items() if v is not None and str(old.get(k) or "")!=str(v)}

def run_fleet(g,dry):
    assets=fleetio_assets(); reports=[]
    for key in ("vehicles","trailers"):
        names=field_names(g,key,dry); items=g.items(LISTS[key]); stats={"source":key,"retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
        sid=names["FleetioId"]; fallback="field_1"
        for a in assets:
            if fleet_kind(a)!=key: continue
            stats["retrieved"]+=1; aid=str(a.get("id") or ""); ident=trailer_id(a) if key=="trailers" else clean(a.get("license_plate") or a.get("name"))
            if key=="trailers":
                exact=[x for x in items if norm(x["fields"].get(sid))==norm(aid)]
                fb=[x for x in items if trailer_norm(x["fields"].get(fallback))==trailer_norm(ident)]
                item,why=(exact[0],"source") if len(exact)==1 else ((None,"ambiguous") if len(exact)>1 or len(fb)>1 else ((fb[0],"fallback") if len(fb)==1 else (None,"new")))
            else: item,why=choose(items,sid,aid,fallback,ident)
            if why=="ambiguous": stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":aid,"identifier":ident,"name":clean(a.get("name"))}); continue
            status=fleet_status(a)
            desired={sid:aid,names["FleetioName"]:clean(a.get("name")),names["FleetioVIN"]:clean(a.get("vin")),names["FleetioMake"]:clean(a.get("make_name") or a.get("make")),names["FleetioModel"]:clean(a.get("model_name") or a.get("model")),names["FleetioType"]:clean(a.get("vehicle_type_name") or a.get("type_name")),fallback:ident}
            if key=="vehicles": desired.update({"field_10":status,"field_6":fleet_active(status)})
            else: desired.update({"field_7":status,"field_5":fleet_active(status)})
            if item:
                stats["matched"]+=1; changes=delta(item["fields"],desired)
                if changes:
                    changes[names["FleetioLastSync"]]=now(); stats["updated"]+=1
                    if not dry:g.update(LISTS[key],item["id"],changes)
            else:
                desired["Title"]=clean(a.get("name")) or ident or aid; desired[names["FleetioLastSync"]]=now(); stats["created"]+=1
                stats["new_candidates"].append({"id":aid,"identifier":ident,"name":clean(a.get("name"))})
                if not dry:g.create(LISTS[key],desired)
        reports.append(stats)
    return reports

def display_name(p):
    first=clean(p.get("first_name")) or ""; last=clean(p.get("last_name")) or ""
    return (f"{last}, {first}" if last else first).strip(", ")

def run_sage(g,dry):
    names=field_names(g,"drivers",dry); items=g.items(LISTS["drivers"]); stats={"source":"sage","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
    sid=names["SageHrId"]
    for p in sage_people():
        if not sage_driver(p):continue
        stats["retrieved"]+=1; pid=str(p.get("id") or ""); emp=clean(p.get("employee_number")); title=emp or pid; name=display_name(p)
        item,why=choose(items,sid,pid,"Title",title)
        if why=="new" and name:
            matches=[x for x in items if norm(x["fields"].get("field_1"))==norm(name)]
            if len(matches)==1:item,why=matches[0],"name"
            elif len(matches)>1:why="ambiguous"
        if why=="ambiguous":stats["ambiguous"]+=1;stats["ambiguous_candidates"].append({"id":pid,"employee_number":emp,"name":name});continue
        desired={sid:pid,names["SageEmployeeNumber"]:emp,names["SageEmploymentStatus"]:clean(p.get("employment_status")),names["SageEmail"]:clean(p.get("email")),names["SageMobile"]:clean(p.get("mobile")),"field_1":name}
        if item:
            stats["matched"]+=1; changes=delta(item["fields"],desired)
            if changes:
                changes[names["SageLastSync"]]=now();stats["updated"]+=1
                if not dry:g.update(LISTS["drivers"],item["id"],changes)
        else:
            desired["Title"]=title; desired[names["SageLastSync"]]=now();stats["created"]+=1
            stats["new_candidates"].append({"id":pid,"employee_number":emp,"name":name})
            if not dry:g.create(LISTS["drivers"],desired)
    return [stats]

def log_result(g,source,status,started,reports,error=None):
    try:
        names=ensure_columns(g,"log"); totals=lambda k:sum(x.get(k,0) for x in reports)
        fields={"Title":f"{source} {started}",names["Source"]:source,names["Status"]:status,names["StartedAt"]:started,names["FinishedAt"]:now(),names["Matched"]:totals("matched"),names["CreatedCount"]:totals("created"),names["UpdatedCount"]:totals("updated"),names["Ambiguous"]:totals("ambiguous"),names["ErrorCount"]:1 if error else totals("errors"),names["Details"]:json.dumps(reports if not error else {"reports":reports,"error":str(error)[:1500]})[:8000]}
        g.create(LISTS["log"],fields)
    except Exception: logging.exception("Sync_Log write failed")

def execute(source="all",dry=None):
    dry=(os.getenv("SLH_SYNC_MODE","validate").lower()!="live") if dry is None else dry
    g=Graph(); started=now(); reports=[]
    try:
        if source in ("all","fleetio"): reports+=run_fleet(g,dry)
        if source in ("all","sage"): reports+=run_sage(g,dry)
        status="dry-run" if dry else "success"
        if not dry: log_result(g,source,status,started,reports)
        return {"status":status,"started":started,"finished":now(),"reports":reports}
    except Exception as e:
        logging.exception("sync failed")
        if not dry:log_result(g,source,"failed",started,reports,e)
        raise

@app.function_name(name="ScheduledMasterDataSync")
@app.schedule(schedule="0 15 3 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled(timer:func.TimerRequest): execute()

@app.function_name(name="ManualMasterDataSync")
@app.route(route="sync",methods=["POST"],auth_level=func.AuthLevel.FUNCTION)
def manual(req:func.HttpRequest):
    source=req.params.get("source","all").lower(); dry=req.params.get("dry_run")
    if source not in ("all","fleetio","sage"):return func.HttpResponse("invalid source",status_code=400)
    try:return func.HttpResponse(json.dumps(execute(source,None if dry is None else dry.lower()=="true")),mimetype="application/json")
    except Exception as e:return func.HttpResponse(json.dumps({"status":"failed","error":str(e)}),status_code=500,mimetype="application/json")

@app.function_name(name="Health")
@app.route(route="health",methods=["GET"],auth_level=func.AuthLevel.ANONYMOUS)
def health(req:func.HttpRequest): return func.HttpResponse(json.dumps({"status":"ok","mode":os.getenv("SLH_SYNC_MODE","validate"),"utc":now()}),mimetype="application/json")
