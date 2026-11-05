import hashlib, json, logging, os, re, time
from datetime import datetime, timezone
import azure.functions as func
import requests
from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient

app = func.FunctionApp()
UTC = timezone.utc
GRAPH = "https://graph.microsoft.com/v1.0"
SITE = os.getenv("SHAREPOINT_SITE_ID", "stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f")
LISTS = {"vehicles": os.getenv("VEHICLES_LIST_ID", "8d96865a-b7dc-40c0-9947-468542800bd4"), "trailers": os.getenv("TRAILERS_LIST_ID", "a2fb1103-b9fe-48a8-90fd-9f6fcdd2d74c"), "drivers": os.getenv("DRIVERS_LIST_ID", "c8d1d700-a1ce-4920-b349-f2a3a6e09982"), "geofences": os.getenv("GEOFENCES_LIST_ID", "0d8cd682-0603-4224-9acb-db49ba289475"), "log": os.getenv("SYNC_LOG_LIST_ID", "b681f046-d299-4a1b-8a33-653f600752b8")}
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

def roadtech_telemetry():
    """Retrieve Falcon current telemetry using the verified RoadTech API contract."""
    base=os.getenv("ROADTECH_BASE_URL","").rstrip("/")
    company=os.getenv("ROADTECH_COMPANY_CODE","")
    if not base or not company: raise RuntimeError("RoadTech base URL/company code not configured")
    api_key=secret("tacho-api-key"); username=secret("tacho-username"); password=secret("tacho-password")
    h=Http({"APIKEY":api_key,"Accept":"application/json","Content-Type":"application/json"})
    login={"User":username,"Pass":password,"OsVersion":"1.0","OsName":"Azure Function","PcName":"slh-sharepoint-sync-prod","AuthType":"password"}
    r=h.request("POST",base+"/auth/login",json=login)
    data=r.json(); sid=(data.get("SID") or data.get("sid")) if isinstance(data,dict) else None
    if not sid:
        login["Pass"]=hashlib.sha1(password.encode()).hexdigest()
        data=h.request("POST",base+"/auth/login",json=login).json()
        sid=(data.get("SID") or data.get("sid")) if isinstance(data,dict) else None
    if not sid: raise RuntimeError("RoadTech authentication returned no session ID")
    h.s.headers.update({"SID":str(sid)})
    rows=[]; offset=0
    while True:
        body={"CompCode":company,"T":datetime.now(UTC).strftime("%Y-%m-%d"),"DataMask":5,"Offset":offset,"OnlyLive":1}
        page=h.request("POST",base+"/Falcon/GetCurrentTelemetry",json=body).json()
        batch=(page.get("Data") or page.get("data") or []) if isinstance(page,dict) else []
        rows.extend(batch)
        if not (page.get("MoreData") or page.get("moreData")) or not batch: break
        offset+=len(batch)
    return rows

def samsara_pages(path):
    h=Http({"Authorization":"Bearer "+secret("samsara-api-token"),"Accept":"application/json"})
    url="https://api.eu.samsara.com/"+path.lstrip("/"); out=[]
    while url:
        data=h.request("GET",url).json(); out.extend(data.get("data",[]))
        page=data.get("pagination") or {}; cursor=page.get("endCursor")
        if not page.get("hasNextPage") or not cursor: break
        url="https://api.eu.samsara.com/"+path.lstrip("/")+(("&" if "?" in path else "?")+"after="+str(cursor))
    return out

def samsara_vehicles(): return samsara_pages("fleet/vehicles?limit=512")
def samsara_addresses(): return samsara_pages("addresses?limit=512")

def fleet_kind(a):
    t=" ".join(str(a.get(k) or "") for k in ("vehicle_type_name","type_name","name","group_name")).upper()
    if "TRAILER" in t: return "trailers"
    if any(x in t for x in ("UNIT","TRUCK","TRACTOR","HGV","RIGID","VAN")): return "vehicles"
    return None

def sage_driver(p):
    s=" ".join(str(p.get(k) or "") for k in ("team","team_name","position","position_name","job_title")).lower()
    agency=" ".join(str(p.get(k) or "") for k in ("agency","agency_name","employment_type","worker_type")).lower()
    if "agency" in agency or "agency" in s: return False
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
 "vehicles":["FleetioId","FleetioName","FleetioVIN","FleetioMake","FleetioModel","FleetioType","FleetioLastSync","RoadTechVehicleId","RoadTechVehicleCode","RoadTechLastTelemetryUtc","RoadTechIgnition","RoadTechMoving","RoadTechOdometerKm","RoadTechLastSync"],
 "trailers":["FleetioId","FleetioName","FleetioVIN","FleetioMake","FleetioModel","FleetioType","FleetioLastSync"],
 "drivers":["SageHrId","SageEmployeeNumber","SageEmploymentStatus","SageEmail","SageMobile","SageLastSync"],
 "geofences":["SamsaraId","SamsaraName","SamsaraFormattedAddress","SamsaraLatitude","SamsaraLongitude","SamsaraAddressTypes","SamsaraLastSync"],
 "log":["Source","Status","StartedAt","FinishedAt","Matched","CreatedCount","UpdatedCount","Ambiguous","ErrorCount","Details"],
 "leave":["SageEmployeeId","EmployeeNumber","DriverName","LeaveDate","LeaveEndDate","LeaveType","LeaveDetails","IsPartDay","Hours","SageLastSync"]}

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


def tacho_login():
    base=os.getenv("TACHO_BASE_URL",os.getenv("ROADTECH_BASE_URL","")).rstrip("/")
    if not base: raise RuntimeError("TachoMaster/RoadTech base URL not configured")
    h=Http({"APIKEY":secret("tacho-api-key"),"Accept":"application/json","Content-Type":"application/json"})
    username=secret("tacho-username"); password=secret("tacho-password")
    body={"User":username,"Pass":password,"OsVersion":"1.0","OsName":"Azure Function","PcName":"slh-master-drivers-sync","AuthType":"password"}
    data=h.request("POST",base+"/auth/login",json=body).json()
    sid=(data.get("SID") or data.get("sid") or data.get("token") or data.get("Token")) if isinstance(data,dict) else None
    if not sid:
        body["Pass"]=hashlib.sha1(password.encode()).hexdigest()
        data=h.request("POST",base+"/auth/login",json=body).json()
        sid=(data.get("SID") or data.get("sid") or data.get("token") or data.get("Token")) if isinstance(data,dict) else None
    if not sid: raise RuntimeError("TachoMaster login returned no SID")
    h.s.headers.update({"SID":str(sid)})
    return h,base

def tacho_members():
    h,base=tacho_login(); out=[]; offset=0
    for _ in range(100):
        data=h.request("POST",base+"/Member/GetMembersLong",json={"Offset":offset,"OnlyLiveMembers":False}).json()
        page=data.get("Data") or data.get("data") or data.get("Members") or []
        if isinstance(page,dict): page=page.get("Data",[])
        out.extend(page)
        more=bool(data.get("MoreData") or data.get("moreData"))
        count=int(data.get("RecordCount") or data.get("recordCount") or len(page))
        if not more or not page: break
        offset+=count
    return out

def tacho_name(p):
    given=clean(p.get("GivenNames") or p.get("givenNames") or p.get("CName") or p.get("cName"))
    surname=clean(p.get("Surname") or p.get("surname") or p.get("SName") or p.get("sName"))
    return " ".join(x for x in (given,surname) if x).strip()

def run_tacho(g,dry):
    items=g.items(LISTS["drivers"])
    cols={c["displayName"]:c["name"] for c in g.columns(LISTS["drivers"])}
    def col(name): return cols.get(name,name)
    stats={"source":"tachomaster","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
    for p in tacho_members():
        member=clean(p.get("MemCode") or p.get("memCode") or p.get("MemberCode") or p.get("memberCode"))
        name=tacho_name(p)
        if not member or not name: continue
        stats["retrieved"]+=1
        exact=[x for x in items if norm(x["fields"].get(col("MemberCode")))==norm(member)]
        fallback=[x for x in items if norm(x["fields"].get(col("Name")))==norm(name)]
        if len(exact)>1 or (not exact and len(fallback)>1):
            stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"memberCode":member,"name":name}); continue
        item=exact[0] if len(exact)==1 else (fallback[0] if len(fallback)==1 else None)
        desired={col("MemberCode"):member,col("Name"):name,col("CardNumber"):clean(p.get("CardNoShort") or p.get("cardNoShort") or p.get("CardNumber")),col("TachoLastSync"):now()}
        if item:
            stats["matched"]+=1; changes=delta(item["fields"],desired)
            if changes:
                stats["updated"]+=1
                if not dry:g.update(LISTS["drivers"],item["id"],changes)
        else:
            desired["Title"]=name; stats["created"]+=1; stats["new_candidates"].append({"memberCode":member,"name":name})
            if not dry:g.create(LISTS["drivers"],desired)
    return [stats]

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

def sage_leave(g,dry):
    names=field_names(g,"leave",dry); items=g.items(LISTS["leave"]); stats={"source":"sage-leave","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
    people=sage_people(); drivers={str(p.get("id")):p for p in people if sage_driver(p)}
    base=os.getenv("SAGE_BASE_URL","https://stuartlyonshaulage.sage.hr/api").rstrip("/")
    h=Http({"X-Auth-Token":secret("sage-api-key"),"User-Agent":"SLH-SharePoint-Sync/1.0","Accept":"application/json"})
    for offset in range(14):
        date=(datetime.now(UTC).date()).fromordinal(datetime.now(UTC).date().toordinal()+offset).isoformat()
        data=h.request("GET",f"{base}/leave-management/out-of-office-today?date={date}").json()
        rows=data.get("data",[]) if isinstance(data,dict) else (data or [])
        for leave in rows:
            employee=drivers.get(str(leave.get("employee_id")))
            if not employee: continue
            stats["retrieved"]+=1
            emp=clean(employee.get("employee_number")); name=display_name(employee); start=clean(leave.get("start_date")) or date; end=clean(leave.get("end_date")) or start
            policy=clean((leave.get("policy") or {}).get("name")) if isinstance(leave.get("policy"),dict) else clean(leave.get("policy"))
            key=f"{emp}|{start}|{policy or ''}"
            exact=[x for x in items if norm(x["fields"].get(names["EmployeeNumber"]))==norm(emp) and str(x["fields"].get(names["LeaveDate"]) or "")[:10]==str(start)[:10] and norm(x["fields"].get(names["LeaveType"]))==norm(policy)]
            desired={names["SageEmployeeId"]:str(leave.get("employee_id")),names["EmployeeNumber"]:emp,names["DriverName"]:name,names["LeaveDate"]:start,names["LeaveEndDate"]:end,names["LeaveType"]:policy,names["LeaveDetails"]:clean(leave.get("details")),names["IsPartDay"]:str(bool(leave.get("is_part_of_day"))),names["Hours"]:clean(leave.get("hours")),names["SageLastSync"]:now()}
            if len(exact)>1: stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"key":key}); continue
            if exact:
                stats["matched"]+=1; changes=delta(exact[0]["fields"],desired)
                if changes:
                    stats["updated"]+=1
                    if not dry:g.update(LISTS["leave"],exact[0]["id"],changes)
            else:
                desired["Title"]=key; stats["created"]+=1; stats["new_candidates"].append({"key":key})
                if not dry:g.create(LISTS["leave"],desired)
    return [stats]

def roadtech_time(row):
    gps=row.get("dataGps") or {}
    return clean(gps.get("Time") or gps.get("time"))

def run_roadtech(g,dry):
    names=field_names(g,"vehicles",dry); items=g.items(LISTS["vehicles"])
    stats={"source":"roadtech","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"unmatched":0,"unmatched_candidates":[],"ambiguous_candidates":[]}
    sid=names["RoadTechVehicleId"]
    for row in roadtech_telemetry():
        stats["retrieved"]+=1
        rid=clean(row.get("vehRtid")); code=clean(row.get("vehCode")); ident=norm(code)
        exact=[x for x in items if rid and norm(x["fields"].get(sid))==norm(rid)]
        fallback=[x for x in items if ident and norm(x["fields"].get("field_1"))==ident]
        if len(exact)>1 or (not exact and len(fallback)>1):
            stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":rid,"identifier":code}); continue
        item=exact[0] if len(exact)==1 else (fallback[0] if len(fallback)==1 else None)
        if not item:
            stats["unmatched"]+=1; stats["unmatched_candidates"].append({"id":rid,"identifier":code}); continue
        stats["matched"]+=1; can=row.get("dataCan") or {}
        desired={sid:rid,names["RoadTechVehicleCode"]:code,names["RoadTechLastTelemetryUtc"]:roadtech_time(row),names["RoadTechIgnition"]:clean(row.get("Ign")),names["RoadTechMoving"]:clean(can.get("Moving")),names["RoadTechOdometerKm"]:clean(can.get("OdoKm"))}
        changes=delta(item["fields"],desired)
        if changes:
            changes[names["RoadTechLastSync"]]=now(); stats["updated"]+=1
            if not dry:g.update(LISTS["vehicles"],item["id"],changes)
    return [stats]

def run_samsara(g,dry):
    reports=[]
    names=field_names(g,"vehicles",dry); items=g.items(LISTS["vehicles"])
    stats={"source":"samsara-vehicles","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"unmatched":0,"unmatched_candidates":[],"ambiguous_candidates":[]}
    sid=names.get("SamsaraId")
    if not sid:
        if not dry: sid=g.add_column(LISTS["vehicles"],"SamsaraId")["name"]
        else: sid="SamsaraId"
    for row in samsara_vehicles():
        stats["retrieved"]+=1; rid=clean(row.get("id")); plate=clean(row.get("licensePlate"))
        exact=[x for x in items if rid and norm(x["fields"].get(sid))==norm(rid)]
        fallback=[x for x in items if plate and norm(x["fields"].get("field_1"))==norm(plate)]
        if len(exact)>1 or (not exact and len(fallback)>1):
            stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":rid,"identifier":plate}); continue
        item=exact[0] if len(exact)==1 else (fallback[0] if len(fallback)==1 else None)
        if not item:
            stats["unmatched"]+=1; stats["unmatched_candidates"].append({"id":rid,"identifier":plate}); continue
        stats["matched"]+=1; changes=delta(item["fields"],{sid:rid})
        if changes:
            stats["updated"]+=1
            if not dry:g.update(LISTS["vehicles"],item["id"],changes)
    reports.append(stats)

    names=field_names(g,"geofences",dry); items=g.items(LISTS["geofences"])
    stats={"source":"samsara-geofences","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
    sid=names["SamsaraId"]
    for row in samsara_addresses():
        stats["retrieved"]+=1; rid=clean(row.get("id")); label=clean(row.get("name")) or clean(row.get("formattedAddress")) or rid
        exact=[x for x in items if rid and norm(x["fields"].get(sid))==norm(rid)]
        fallback=[x for x in items if label and norm(x["fields"].get("Title"))==norm(label)]
        if len(exact)>1 or (not exact and len(fallback)>1):
            stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":rid,"name":label}); continue
        item=exact[0] if len(exact)==1 else (fallback[0] if len(fallback)==1 else None)
        desired={sid:rid,names["SamsaraName"]:clean(row.get("name")),names["SamsaraFormattedAddress"]:clean(row.get("formattedAddress")),names["SamsaraLatitude"]:clean(row.get("latitude")),names["SamsaraLongitude"]:clean(row.get("longitude")),names["SamsaraAddressTypes"]:clean(",".join(row.get("addressTypes") or []))}
        if item:
            stats["matched"]+=1; changes=delta(item["fields"],desired)
            if changes:
                changes[names["SamsaraLastSync"]]=now(); stats["updated"]+=1
                if not dry:g.update(LISTS["geofences"],item["id"],changes)
        else:
            desired["Title"]=label; desired[names["SamsaraLastSync"]]=now(); stats["created"]+=1
            stats["new_candidates"].append({"id":rid,"name":label})
            if not dry:g.create(LISTS["geofences"],desired)
    reports.append(stats); return reports

def log_result(g,source,status,started,reports,error=None):
    try:
        names=ensure_columns(g,"log"); totals=lambda k:sum(x.get(k,0) for x in reports)
        summary=[{k:r.get(k,0) for k in ("source","retrieved","matched","created","updated","ambiguous","errors")} for r in reports]
        detail=json.dumps(summary if not error else {"summary":summary,"error":str(error)[:100]},separators=(",",":"))[:240]
        fields={"Title":f"{source} {started}",names["Source"]:source,names["Status"]:status,names["StartedAt"]:started,names["FinishedAt"]:now(),names["Matched"]:str(totals("matched")),names["CreatedCount"]:str(totals("created")),names["UpdatedCount"]:str(totals("updated")),names["Ambiguous"]:str(totals("ambiguous")),names["ErrorCount"]:str(1 if error else totals("errors")),names["Details"]:detail}
        g.create(LISTS["log"],fields)
    except Exception: logging.exception("Sync_Log write failed")

def execute(source="all",dry=None):
    dry=(os.getenv("SLH_SYNC_MODE","validate").lower()!="live") if dry is None else dry
    g=Graph(); started=now(); reports=[]
    try:
        if source in ("all","fleetio"): reports+=run_fleet(g,dry)
        if source in ("all","tacho"): reports+=run_tacho(g,dry)
        if source in ("all","sage"): reports+=run_sage(g,dry)
        if source in ("all","sage_leave","sage"): reports+=sage_leave(g,dry)
        if source in ("all","roadtech"): reports+=run_roadtech(g,dry)
        if source in ("all","samsara"): reports+=run_samsara(g,dry)
        status="dry-run" if dry else "success"
        if not dry: log_result(g,source,status,started,reports)
        return {"status":status,"started":started,"finished":now(),"reports":reports}
    except Exception as e:
        logging.exception("sync failed")
        if not dry:log_result(g,source,"failed",started,reports,e)
        raise

@app.function_name(name="ScheduledFleetioSync")
@app.schedule(schedule="0 15 3 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_fleetio(timer:func.TimerRequest): execute("fleetio")

@app.function_name(name="ScheduledTachoSync")
@app.schedule(schedule="0 30 3 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_tacho(timer:func.TimerRequest):
    if os.getenv("TACHO_SYNC_ENABLED","false").lower()=="true": execute("tacho")
    else: logging.warning("TachoMaster schedule disabled")

@app.function_name(name="ScheduledSageSync")
@app.schedule(schedule="0 45 3 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_sage(timer:func.TimerRequest):
    if os.getenv("SAGE_SYNC_ENABLED","false").lower()=="true": execute("sage")
    else: logging.warning("Sage HR schedule disabled: credential validation required")

@app.function_name(name="ScheduledRoadTechSync")
@app.schedule(schedule="0 */15 * * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_roadtech(timer:func.TimerRequest):
    if os.getenv("ROADTECH_SYNC_ENABLED","false").lower()=="true": execute("roadtech")
    else: logging.warning("RoadTech schedule disabled")

@app.function_name(name="ScheduledSamsaraSync")
@app.schedule(schedule="0 15 4 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_samsara(timer:func.TimerRequest):
    if os.getenv("SAMSARA_SYNC_ENABLED","false").lower()=="true": execute("samsara")
    else: logging.warning("Samsara schedule disabled")

@app.function_name(name="ManualMasterDataSync")
@app.route(route="sync",methods=["POST"],auth_level=func.AuthLevel.FUNCTION)
def manual(req:func.HttpRequest):
    source=req.params.get("source","all").lower(); dry=req.params.get("dry_run")
    if source not in ("all","fleetio","sage","sage_leave","tacho","roadtech","samsara"):return func.HttpResponse("invalid source",status_code=400)
    try:return func.HttpResponse(json.dumps(execute(source,None if dry is None else dry.lower()=="true")),mimetype="application/json")
    except Exception as e:return func.HttpResponse(json.dumps({"status":"failed","error":str(e)}),status_code=500,mimetype="application/json")

@app.function_name(name="Health")
@app.route(route="health",methods=["GET"],auth_level=func.AuthLevel.ANONYMOUS)
def health(req:func.HttpRequest): return func.HttpResponse(json.dumps({"status":"ok","mode":os.getenv("SLH_SYNC_MODE","validate"),"utc":now()}),mimetype="application/json")
