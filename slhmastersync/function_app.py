import hashlib, json, logging, os, re, time, random, threading
from contextlib import contextmanager
from urllib.parse import urlparse, urlencode
from datetime import datetime, timezone
from zoneinfo import ZoneInfo
import azure.functions as func
import requests
from azure.identity import ManagedIdentityCredential
from azure.keyvault.secrets import SecretClient

app = func.FunctionApp()
UTC = timezone.utc
GRAPH = "https://graph.microsoft.com/v1.0"
SITE = os.getenv("SHAREPOINT_SITE_ID", "stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f")
LISTS = {"vehicles": os.getenv("VEHICLES_LIST_ID", "8d96865a-b7dc-40c0-9947-468542800bd4"), "trailers": os.getenv("TRAILERS_LIST_ID", "a2fb1103-b9fe-48a8-90fd-9f6fcdd2d74c"), "drivers": os.getenv("DRIVERS_LIST_ID", "2b89c3fc-d83e-424b-81b9-8e2546fabb78"), "geofences": os.getenv("GEOFENCES_LIST_ID", "0d8cd682-0603-4224-9acb-db49ba289475"), "log": os.getenv("SYNC_LOG_LIST_ID", "ff965828-ca04-4374-aec9-b8b500e234b1")}
cred = ManagedIdentityCredential()
logging.getLogger('azure').setLevel(logging.WARNING)
logging.getLogger('urllib3').setLevel(logging.WARNING)

def norm(v): return re.sub(r"[^A-Z0-9]", "", str(v or "").upper())
def clean(v): return None if v is None or str(v).strip() == "" else str(v).strip()
def now(): return datetime.now(UTC).isoformat()

def uk_master_sync_due(at=None):
    local=(at or datetime.now(UTC)).astimezone(ZoneInfo('Europe/London'))
    return local.hour in (6,16)

class Http:
    def __init__(self, headers=None): self.s=requests.Session(); self.s.headers.update(headers or {})
    def request(self, method, url, **kw):
        for attempt in range(6):
            try:
                r=self.s.request(method,url,timeout=45,**kw)
            except requests.RequestException:
                if attempt == 5: raise RuntimeError('API transport failure') from None
                time.sleep(min(2**attempt,30)+random.random()); continue
            if r.status_code not in (429,500,502,503,504):
                if not r.ok: raise RuntimeError(f'HTTP {r.status_code} from {urlparse(url).hostname}')
                return r
            if attempt == 5: raise RuntimeError(f'HTTP {r.status_code}: retries exhausted')
            retry=r.headers.get('Retry-After','0')
            time.sleep(min(int(retry) if retry.isdigit() and int(retry)>0 else 2**attempt,60)+random.random())

@contextmanager
def sync_lock(source):
    from azure.storage.blob import BlobServiceClient
    from azure.core.exceptions import ResourceExistsError
    connection=os.environ.get('AzureWebJobsStorage')
    if not connection: raise RuntimeError('Existing host storage connection unavailable for sync lock')
    service=BlobServiceClient.from_connection_string(connection)
    container=service.get_container_client('slh-sync-locks')
    try: container.create_container()
    except ResourceExistsError: pass
    blob=container.get_blob_client(source+'.lock')
    try: blob.upload_blob(b'',overwrite=False)
    except ResourceExistsError: pass
    try: lease=blob.acquire_lease(lease_duration=60)
    except Exception: raise RuntimeError('Another source synchronisation is running') from None
    stop=threading.Event(); lost=threading.Event()
    def renew():
        while not stop.wait(20):
            try: lease.renew()
            except Exception: lost.set(); break
    thread=threading.Thread(target=renew,daemon=True);thread.start()
    try: yield lost
    finally:
        stop.set();thread.join(timeout=2)
        try: lease.release()
        except Exception: pass

class Graph:
    def __init__(self, limit=None):
        self.h=Http(); self.lock_lost=None; self.limit=limit; self.writes=0; self.evidence=[]
    def budget(self):
        if self.lock_lost and self.lock_lost.is_set(): raise RuntimeError('Sync lease lost; further writes stopped')
        return self.limit is None or self.writes < self.limit
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
    def columns(self,lid): return self.pages(f"/sites/{SITE}/lists/{lid}/columns")
    def items(self,lid): return self.pages(f"/sites/{SITE}/lists/{lid}/items?$expand=fields&$top=999")
    def add_column(self,lid,name,kind="text"):
        raise RuntimeError('Automatic schema changes disabled; review existing columns first')
    def create(self,lid,fields):
        if os.getenv('SLH_WRITES_VERIFIED','false')!='true': raise RuntimeError('Production writes locked pending controlled verification')
        if not self.budget(): return None
        # A Graph POST is deliberately not retried: an unknown outcome must be
        # reconciled using the stable external ID on the next run.
        token=cred.get_token("https://graph.microsoft.com/.default").token
        try:
            r=self.h.s.request("POST",GRAPH+f"/sites/{SITE}/lists/{lid}/items",headers={"Authorization":"Bearer "+token},json={"fields":fields},timeout=45)
        except requests.RequestException: raise RuntimeError('Create outcome unknown; reconcile next run') from None
        if not r.ok: raise RuntimeError(f'Graph create HTTP {r.status_code}')
        row=r.json(); self.writes+=1; self.evidence.append({'list':lid,'item':row['id'],'operation':'insert','fields':sorted(fields)})
        return row
    def update(self,lid,iid,fields):
        if os.getenv('SLH_WRITES_VERIFIED','false')!='true': raise RuntimeError('Production writes locked pending controlled verification')
        if not self.budget(): return None
        before=self.req('GET',f'/sites/{SITE}/lists/{lid}/items/{iid}?$expand=fields').json()
        result=self.req("PATCH",f"/sites/{SITE}/lists/{lid}/items/{iid}/fields",headers={'If-Match':before['eTag']},json=fields).json()
        check=self.req("GET",f"/sites/{SITE}/lists/{lid}/items/{iid}?$expand=fields").json()['fields']
        if delta(check,fields): raise RuntimeError('SharePoint read-back mismatch')
        manual={k:v for k,v in before['fields'].items() if (k=='Title' or k.startswith('field_')) and k not in fields}
        preserved=all(check.get(k)==v for k,v in manual.items())
        if not preserved: raise RuntimeError('Unrelated operational fields changed during write')
        self.writes+=1; self.evidence.append({'list':lid,'item':iid,'operation':'update','fields':sorted(fields),'verified':True,'manual_fields_preserved':True})
        return result

def secret(name):
    vault=os.getenv("KEY_VAULT_URL","https://slh-lists-kv-261028.vault.azure.net/")
    return SecretClient(vault_url=vault,credential=cred).get_secret(name).value

def fleetio_assets():
    h=Http({"Authorization":"Token "+secret("fleetio-api-key"),"Account-Token":secret("fleetio-account-token"),"X-Api-Version":"2024-01-01","Accept":"application/json"})
    base="https://secure.fleetio.com/api/v1/vehicles"; url=base+"?per_page=100"; out=[]; seen=set()
    while url:
        if url in seen: raise RuntimeError('Fleetio repeated pagination cursor')
        if urlparse(url).hostname!='secure.fleetio.com': raise RuntimeError('Fleetio pagination host rejected')
        seen.add(url)
        r=h.request("GET",url); data=r.json(); out += data if isinstance(data,list) else data.get("records",data.get("data",[]))
        url=r.links.get("next",{}).get("url")
        if not url and isinstance(data,dict):
            cursor=data.get('next_cursor'); url=base+'?'+urlencode({'per_page':100,'start_cursor':cursor}) if cursor else data.get("next_page_url")
        if not url and isinstance(data,list):
            page=int(r.headers.get('X-Pagination-Current-Page','1')); total=int(r.headers.get('X-Pagination-Total-Pages','1'))
            if page<total: url=base+'?'+urlencode({'per':100,'page':page+1})
    return out

def sage_people():
    h=Http({"X-Auth-Token":secret("sage-api-key"),"User-Agent":"SLH-SharePoint-Sync/1.0","Accept":"application/json"}); out=[]; page=1
    while True:
        base=os.getenv('SAGE_BASE_URL','https://stuartlyonshaulagelimited.sage.hr/api').rstrip('/')
        data=h.request("GET",f"{base}/employees?page={page}").json()
        rows=data.get("data",data.get("employees",[])) if isinstance(data,dict) else data
        out+=rows
        if not rows or (isinstance(data,dict) and page>=int(data.get("meta",{}).get("total_pages",data.get("meta",{}).get("last_page",page)))): break
        page+=1
    return out

def roadtech_telemetry():
    """RoadTech Falcon API document v1.2, sections 2 and 3.3."""
    base=os.getenv("ROADTECH_BASE_URL","").rstrip("/")
    company=secret("roadtech-company-code")
    if not base or not company: raise RuntimeError("RoadTech base URL/company code not configured")
    api_key=secret("roadtech-api-key"); username=secret("roadtech-username"); password=secret("roadtech-password")
    h=Http({"APIKEY":api_key,"Accept":"application/json","Content-Type":"application/json"})
    login={"User":username,"Pass":password,"OsVersion":"1.0","OsName":"Azure Function","PcName":"slhmastersync","AuthType":"password"}
    r=h.request("POST",base+"/api/auth/login",json=login)
    data=r.json(); sid=(data.get('Token') or data.get('token') or data.get("SID") or data.get("sid")) if isinstance(data,dict) else data
    if not sid: raise RuntimeError("RoadTech authentication returned no session ID")
    h.s.headers.update({"SID":str(sid)})
    rows=[]; offset=0
    while True:
        body={"CompCode":company,"T":datetime.now(UTC).strftime("%Y-%m-%d"),"DataMask":7,"Offset":offset,"OnlyLive":1}
        page=h.request("POST",base+"/api/Falcon/GetCurrentTelemetry",json=body).json()
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
 "drivers":["SageHrId","SageEmployeeNumber","SageEmploymentStatus","SageEmail","SageMobile","SageLastSync","SageStartDate"],
 "geofences":["SamsaraId","SamsaraName","SamsaraFormattedAddress","SamsaraLatitude","SamsaraLongitude","SamsaraAddressTypes","SamsaraLastSync","SamsaraSiteRef","SamsaraRadiusMeters"],
 "roadtech":["RoadTechVehicleCode","RoadTechLastTelemetryUtc","RoadTechIgnition","RoadTechMoving","RoadTechOdometerKm","RoadTechLatitude","RoadTechLongitude","RoadTechLocation","RoadTechSpeedKmh","RoadTechLastSync"],
 "log":["Source","Status","StartedAt","FinishedAt","Matched","CreatedCount","UpdatedCount","Ambiguous","ErrorCount","Details"]}

def ensure_columns(g,key):
    existing={c["displayName"]:c["name"] for c in g.columns(LISTS[key])}
    for name in COLS[key]:
        if name not in existing:
            raise RuntimeError('Schema field requires reviewed mapping: '+key+'.'+name)
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
    if len(fb)==1:
        assigned=fb[0]['fields'].get(source_field)
        if assigned and norm(assigned)!=norm(source_id): return None,'ambiguous'
        return fb[0],"fallback"
    return (None,"ambiguous") if len(fb)>1 else (None,"new")

def delta(old, desired):
    return {k:v for k,v in desired.items() if v is not None and str(old.get(k) if old.get(k) is not None else "")!=str(v)}

def run_fleet(g,dry):
    assets=fleetio_assets(); reports=[]
    for key in ("vehicles","trailers"):
        names=field_names(g,key,dry); items=g.items(LISTS[key]); stats={"source":key,"retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
        sid=names["FleetioId"]; fallback="field_1"
        for a in assets:
            if not dry and not g.budget(): break
            if fleet_kind(a)!=key: continue
            stats["retrieved"]+=1; aid=str(a.get("id") or ""); ident=trailer_id(a) if key=="trailers" else clean(a.get("license_plate") or a.get("name"))
            if key=="trailers":
                exact=[x for x in items if norm(x["fields"].get(sid))==norm(aid)]
                fb=[x for x in items if trailer_norm(x["fields"].get(fallback))==trailer_norm(ident)]
                item,why=(exact[0],"source") if len(exact)==1 else ((None,"ambiguous") if len(exact)>1 or len(fb)>1 else ((fb[0],"fallback") if len(fb)==1 else (None,"new")))
            else: item,why=choose(items,sid,aid,fallback,ident)
            if not aid: raise RuntimeError("Fleetio asset has no stable ID")
            if item and item["fields"].get(sid) and norm(item["fields"][sid])!=norm(aid): item,why=None,"ambiguous"
            if why=="ambiguous": stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":aid,"identifier":ident,"name":clean(a.get("name"))}); continue
            status=fleet_status(a)
            desired={sid:aid,names["FleetioName"]:clean(a.get("name")),names["FleetioVIN"]:clean(a.get("vin")),names["FleetioMake"]:clean(a.get("make_name") or a.get("make")),names["FleetioModel"]:clean(a.get("model_name") or a.get("model")),names["FleetioType"]:clean(a.get("vehicle_type_name") or a.get("type_name")),fallback:ident}
            if key=="vehicles": desired.update({"field_10":status})
            else: desired.update({"field_7":status})
            if item:
                desired.pop(fallback,None)
                stats["matched"]+=1; changes=delta(item["fields"],desired)
                if changes:
                    changes[names["FleetioLastSync"]]=now(); stats["updated"]+=1
                    if not dry:g.update(LISTS[key],item["id"],changes)
            else:
                if not dry and os.getenv('SLH_CREATES_VERIFIED','false')!='true':
                    stats['errors']+=1; continue
                desired["Title"]="FLEETIO-"+aid; desired[names["FleetioLastSync"]]=now(); stats["created"]+=1
                stats["new_candidates"].append({"id":aid,"identifier":ident,"name":clean(a.get("name"))})
                if not dry:
                    new=g.create(LISTS[key],desired)
                    if new: items.append(new)
                else: items.append({'id':'preview-'+aid,'fields':desired})
        reports.append(stats)
    return reports

def display_name(p):
    first=clean(p.get("first_name")) or ""; last=clean(p.get("last_name")) or ""
    return (f"{last}, {first}" if last else first).strip(", ")

def run_sage(g,dry):
    names=field_names(g,"drivers",dry); items=g.items(LISTS["drivers"]); stats={"source":"sage","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"new_candidates":[],"ambiguous_candidates":[]}
    sid=names["SageHrId"]
    for p in sage_people():
        if not dry and not g.budget(): break
        if not sage_driver(p):continue
        stats["retrieved"]+=1; pid=str(p.get("id") or ""); emp=clean(p.get("employee_number")); title="SAGE-"+pid; name=display_name(p)
        item,why=choose(items,sid,pid,"SageEmployeeNumber",emp)
        if why=="new" and name:
            matches=[x for x in items if norm(x["fields"].get("field_1"))==norm(name)]
            if len(matches)==1:item,why=matches[0],"name"
            elif len(matches)>1:why="ambiguous"
            if item and item["fields"].get(sid) and norm(item["fields"][sid])!=norm(pid): item,why=None,"ambiguous"
        if why=="ambiguous":stats["ambiguous"]+=1;stats["ambiguous_candidates"].append({"id":pid,"employee_number":emp,"name":name});continue
        desired={sid:pid,names["SageEmployeeNumber"]:emp,names["SageEmploymentStatus"]:clean(p.get("employment_status")),names["SageEmail"]:clean(p.get("email")),names["SageMobile"]:clean(p.get("mobile_phone")),names["SageStartDate"]:clean(p.get("employment_start_date")),"field_1":name}
        if item:
            desired.pop("field_1",None)
            stats["matched"]+=1; changes=delta(item["fields"],desired)
            if changes:
                changes[names["SageLastSync"]]=now();stats["updated"]+=1
                if not dry:g.update(LISTS["drivers"],item["id"],changes)
        else:
            if not dry and os.getenv('SLH_CREATES_VERIFIED','false')!='true':
                stats['errors']+=1; continue
            desired["Title"]=title; desired[names["SageLastSync"]]=now();stats["created"]+=1
            stats["new_candidates"].append({"id":pid,"employee_number":emp,"name":name})
            if not dry:
                new=g.create(LISTS['drivers'],desired)
                if new: items.append(new)
            else: items.append({'id':'preview-'+pid,'fields':desired})
    return [stats]

def roadtech_time(row):
    gps=row.get("dataGps") or {}
    return clean(gps.get("Time") or gps.get("time"))

def run_roadtech(g,dry):
    names={c['displayName']:c['name'] for c in g.columns(LISTS['vehicles'])}
    for name in COLS['roadtech']:
        if name not in names: raise RuntimeError('Reviewed RoadTech schema missing: '+name)
    items=g.items(LISTS["vehicles"])
    stats={"source":"roadtech","retrieved":0,"matched":0,"created":0,"updated":0,"ambiguous":0,"errors":0,"unmatched":0,"unmatched_candidates":[],"ambiguous_candidates":[]}
    sid=names["DotTrackingVehicleId"]
    for row in roadtech_telemetry():
        if not dry and not g.budget(): break
        stats["retrieved"]+=1
        rid=clean(row.get("vehRtid")); code=clean(row.get("vehCode")); ident=norm(code)
        exact=[x for x in items if rid and norm(x["fields"].get(sid))==norm(rid)]
        fallback=[x for x in items if ident and norm(x["fields"].get("field_1"))==ident]
        if len(exact)>1 or (not exact and len(fallback)>1):
            stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":rid,"identifier":code}); continue
        item=exact[0] if len(exact)==1 else (fallback[0] if len(fallback)==1 else None)
        if not item:
            stats["unmatched"]+=1; stats["unmatched_candidates"].append({"id":rid,"identifier":code}); continue
        stats["matched"]+=1; can=row.get("dataCan") or {}; gps=row.get("dataGps") or {}; gaz=row.get("dataGaz") or {}
        desired={sid:rid,names["RoadTechVehicleCode"]:code,names["RoadTechLastTelemetryUtc"]:roadtech_time(row),names["RoadTechIgnition"]:clean(row.get("Ign")),names["RoadTechMoving"]:clean(can.get("Moving")),names["RoadTechOdometerKm"]:clean(can.get("OdoKm")),names["RoadTechLatitude"]:clean(gps.get("Lat")),names["RoadTechLongitude"]:clean(gps.get("Long")),names["RoadTechSpeedKmh"]:clean(gps.get("KmH")),names["RoadTechLocation"]:clean(", ".join(str(gaz.get(k) or "") for k in ("Town","County","Country","PCode")).strip(", "))}
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
        if not dry and not g.budget(): break
        stats["retrieved"]+=1; rid=clean(row.get("id")); plate=clean(row.get("licensePlate") or row.get("name"))
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
        if not dry and not g.budget(): break
        stats["retrieved"]+=1; rid=clean(row.get("id")); label=clean(row.get("name")) or clean(row.get("formattedAddress")) or rid
        exact=[x for x in items if rid and norm(x["fields"].get(sid))==norm(rid)]
        fallback=[x for x in items if label and norm(x["fields"].get("Title"))==norm(label)]
        if len(exact)>1 or (not exact and len(fallback)>1):
            stats["ambiguous"]+=1; stats["ambiguous_candidates"].append({"id":rid,"name":label}); continue
        item=exact[0] if len(exact)==1 else (fallback[0] if len(fallback)==1 else None)
        desired={sid:rid,names["SamsaraName"]:clean(row.get("name")),names["SamsaraFormattedAddress"]:clean(row.get("formattedAddress")),names["SamsaraLatitude"]:clean(row.get("latitude")),names["SamsaraLongitude"]:clean(row.get("longitude")),names["SamsaraAddressTypes"]:clean(",".join(row.get("addressTypes") or [])),names["SamsaraSiteRef"]:clean((row.get("externalIds") or {}).get("slhTmsSite")),names["SamsaraRadiusMeters"]:clean(((row.get("geofence") or {}).get("circle") or {}).get("radiusMeters"))}
        if item:
            stats["matched"]+=1; changes=delta(item["fields"],desired)
            if changes:
                changes[names["SamsaraLastSync"]]=now(); stats["updated"]+=1
                if not dry:g.update(LISTS["geofences"],item["id"],changes)
        else:
            if not dry and os.getenv('SLH_CREATES_VERIFIED','false')!='true':
                stats['errors']+=1; continue
            desired["Title"]=label; desired[names["SamsaraLastSync"]]=now(); stats["created"]+=1
            stats["new_candidates"].append({"id":rid,"name":label})
            if not dry:
                new=g.create(LISTS['geofences'],desired)
                if new: items.append(new)
            else: items.append({'id':'preview-'+rid,'fields':desired})
    reports.append(stats); return reports

def log_result(g,source,status,started,reports,error=None):
    try:
        names=ensure_columns(g,"log"); totals=lambda k:sum(x.get(k,0) for x in reports)
        summary=[{k:r.get(k,0) for k in ("source","retrieved","matched","created","updated","ambiguous","errors")} for r in reports]
        detail=json.dumps(summary if not error else {"summary":summary,"error":str(error)[:100]},separators=(",",":"))[:240]
        fields={"Title":f"{source} {started}",names["Source"]:source,names["Status"]:status,names["StartedAt"]:started,names["FinishedAt"]:now(),names["Matched"]:str(totals("matched")),names["CreatedCount"]:str(totals("created")),names["UpdatedCount"]:str(totals("updated")),names["Ambiguous"]:str(totals("ambiguous")),names["ErrorCount"]:str(1 if error else totals("errors")),names["Details"]:detail}
        g.create(LISTS["log"],fields)
    except Exception: logging.exception("Sync_Log write failed")

def execute(source="all",dry=None,limit=None):
    dry=(os.getenv("SLH_SYNC_MODE","validate").lower()!="live") if dry is None else dry
    if dry: return execute_inner(source,True,limit)
    if source=='all': raise RuntimeError('Run one provider at a time for controlled source locking')
    with sync_lock(source) as lost: return execute_inner(source,False,limit,lost)

def execute_inner(source="all",dry=None,limit=None,lost=None):
    dry=(os.getenv("SLH_SYNC_MODE","validate").lower()!="live") if dry is None else dry
    g=Graph(limit); g.lock_lost=lost; started=now(); reports=[]
    try:
        if source in ("all","fleetio"): reports+=run_fleet(g,dry)
        if source in ("all","sage"): reports+=run_sage(g,dry)
        if source in ("all","roadtech"): reports+=run_roadtech(g,dry)
        if source in ("all","samsara"): reports+=run_samsara(g,dry)
        status="dry-run" if dry else "success"
        if not dry:
            limit_saved=g.limit; g.limit=None
            log_result(g,source,status,started,reports)
            g.limit=limit_saved
        logging.info('SLH_SYNC_RESULT %s',json.dumps({'source':source,'status':status,'reports':[{k:v for k,v in r.items() if not isinstance(v,list)} for r in reports]}))
        return {"status":status,"started":started,"finished":now(),"reports":reports,"writes":g.writes,"evidence":g.evidence}
    except Exception as e:
        logging.error("SLH_SYNC_FAILURE source=%s error_type=%s",source,type(e).__name__)
        if not dry:log_result(g,source,"failed",started,reports,e)
        raise

@app.function_name(name="ScheduledFleetioSync")
@app.schedule(schedule="0 0 5,6,15,16 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_fleetio(timer:func.TimerRequest):
    if os.getenv('FLEETIO_SYNC_ENABLED','false')=='true' and uk_master_sync_due(): execute('fleetio')

@app.function_name(name="ScheduledSageSync")
@app.schedule(schedule="0 0 5,6,15,16 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_sage(timer:func.TimerRequest):
    if os.getenv("SAGE_SYNC_ENABLED","false").lower()=="true" and uk_master_sync_due(): execute("sage")
    else: logging.warning("Sage HR schedule disabled: credential validation required")

@app.function_name(name="ScheduledRoadTechSync")
@app.schedule(schedule="0 */30 * * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_roadtech(timer:func.TimerRequest):
    if os.getenv("ROADTECH_SYNC_ENABLED","false").lower()=="true": execute("roadtech")
    else: logging.warning("RoadTech schedule disabled")

@app.function_name(name="ScheduledSamsaraSync")
@app.schedule(schedule="0 7,22,37,52 * * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_samsara(timer:func.TimerRequest):
    if os.getenv("SAMSARA_SYNC_ENABLED","false").lower()=="true": execute("samsara")
    else: logging.warning("Samsara schedule disabled")

@app.function_name(name="ManualMasterDataSync")
@app.route(route="sync",methods=["POST"],auth_level=func.AuthLevel.FUNCTION)
def manual(req:func.HttpRequest):
    source=req.params.get("source","all").lower(); dry=req.params.get("dry_run")
    if source not in ("all","fleetio","sage","roadtech","samsara"):return func.HttpResponse("invalid source",status_code=400)
    try:return func.HttpResponse(json.dumps(execute(source,None if dry is None else dry.lower()=="true",int(req.params["max_writes"]) if req.params.get("max_writes") else None)),mimetype="application/json")
    except Exception as e:return func.HttpResponse(json.dumps({"status":"failed","error_type":type(e).__name__}),status_code=500,mimetype="application/json")

@app.function_name(name="Health")
@app.route(route="health",methods=["GET"],auth_level=func.AuthLevel.ANONYMOUS)
def health(req:func.HttpRequest): return func.HttpResponse(json.dumps({"status":"ok","mode":os.getenv("SLH_SYNC_MODE","validate"),"utc":now()}),mimetype="application/json")

@app.function_name(name="IntegrationPreflight")
@app.route(route="preflight",methods=["POST"],auth_level=func.AuthLevel.FUNCTION)
def preflight(req:func.HttpRequest):
    """Read-only diagnostics. Return schemas/counts, never credentials or HR values."""
    source=req.params.get('source','graph').lower(); report={'source':source,'utc':now(),'writes':0}
    try:
        if source=='graph':
            g=Graph(); report['lists']={}
            for key,lid in LISTS.items():
                try:
                    cols=g.columns(lid)
                    report['lists'][key]={'status':'ok','columns':[{'name':c['name'],'displayName':c['displayName'],'type':next((k for k in ('text','number','dateTime','choice','lookup','boolean') if k in c),'other')} for c in cols]}
                except Exception as e: report['lists'][key]={'status':'failed','error_type':type(e).__name__,'error':str(e) if isinstance(e,RuntimeError) else 'Graph authentication failed'}
        elif source=='fleetio':
            h=Http({'Authorization':'Token '+secret('fleetio-api-key'),'Account-Token':secret('fleetio-account-token'),'X-Api-Version':'2024-01-01'})
            d=h.request('GET','https://secure.fleetio.com/api/v1/vehicles?per_page=5').json(); rows=d if isinstance(d,list) else d.get('records',[])
            report.update(records=len(rows),fields=sorted(rows[0]) if rows else [])
        elif source=='sage':
            h=Http({'X-Auth-Token':secret('sage-api-key')});base=os.getenv('SAGE_BASE_URL','https://stuartlyonshaulagelimited.sage.hr/api')
            d=h.request('GET',base+'/employees?page=1').json(); rows=d.get('data',[])
            report.update(records=len(rows),fields=sorted(rows[0]) if rows else [])
        elif source=='samsara':
            h=Http({'Authorization':'Bearer '+secret('samsara-api-token')});d=h.request('GET','https://api.eu.samsara.com/fleet/vehicles?limit=5').json();rows=d.get('data',[])
            report.update(records=len(rows),fields=sorted(rows[0]) if rows else [])
        elif source=='roadtech':
            rows=roadtech_telemetry();report.update(records=len(rows),fields=sorted(rows[0]) if rows else [],nested_fields={k:sorted(v) for k,v in (rows[0] if rows else {}).items() if isinstance(v,dict)})
        elif source=='keyvault':
            report['secrets']={}
            for name in ('fleetio-api-key','fleetio-account-token','roadtech-api-key','roadtech-company-code','roadtech-username','roadtech-password','sage-api-key','samsara-api-token'):
                try: report['secrets'][name]='accessible' if secret(name) else 'empty'
                except Exception: report['secrets'][name]='unavailable'
        else: return func.HttpResponse('invalid source',status_code=400)
        report['status']='checked'
        logging.info('SLH_PREFLIGHT %s',json.dumps({'source':source,'utc':report['utc'],'status':'checked','writes':0}))
        return func.HttpResponse(json.dumps(report),mimetype='application/json')
    except Exception as e:
        report.update(status='failed',error_type=type(e).__name__,error=str(e) if isinstance(e,RuntimeError) else 'Authentication or provider check failed')
        logging.warning('SLH_PREFLIGHT %s',json.dumps({'source':source,'status':'failed','error_type':type(e).__name__,'writes':0}))
        return func.HttpResponse(json.dumps(report),mimetype='application/json',status_code=502)


@app.function_name(name="ProvisionReviewedSchema")
@app.route(route="provision-schema",methods=["POST"],auth_level=func.AuthLevel.FUNCTION)
def provision_schema(req:func.HttpRequest):
    source=req.params.get('source')
    if source not in ('roadtech','samsara','fleetio','sage'): return func.HttpResponse('invalid source',status_code=400)
    g=Graph(); targets={'fleetio':{'vehicles':['FleetioIssuesCount'],'trailers':['FleetioIssuesCount']},'sage':{'drivers':['SageStartDate']},'roadtech':{'vehicles':COLS['roadtech']},'samsara':{'vehicles':['SamsaraId'],'geofences':COLS['geofences']}}[source]; report=[]
    for key,fields in targets.items():
        existing=g.columns(LISTS[key]); names={norm(c['name']) for c in existing}|{norm(c['displayName']) for c in existing}
        for name in fields:
            if norm(name) in names: continue
            g.req('POST',f'/sites/{SITE}/lists/{LISTS[key]}/columns',json={'name':name,'text':{'maxLength':255}})
            names.add(norm(name));report.append({'list':key,'field':name})
    return func.HttpResponse(json.dumps({'created':report}),mimetype='application/json')
