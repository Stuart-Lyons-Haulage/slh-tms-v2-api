"""Isolated SLH SharePoint API sync preflight. No source API requests or writes."""
import os, json, logging
from datetime import datetime,timezone
import azure.functions as func
app=func.FunctionApp()
SITE="stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f"
def preflight():
    e=os.environ
    return {"checked_at_utc":datetime.now(timezone.utc).isoformat(),
            "site_verified":e.get("SHAREPOINT_SITE_ID")==SITE,
            "mode":e.get("SLH_SYNC_MODE","validate"),
            "writes_enabled":False,
            "source_calls_performed":0,
            "writes_performed":0,
            "fleetio_reference_configured":bool(e.get("FLEETIO_API_KEY")),
            "sage_reference_configured":bool(e.get("SAGEHR_API_KEY"))}
@app.timer_trigger(schedule="0 0 6 * * *",arg_name="timer",run_on_startup=False,use_monitor=True)
def scheduled_preflight(timer:func.TimerRequest):
    logging.info("SLH sync preflight %s",json.dumps(preflight()))
@app.route(route="health",methods=["GET"],auth_level=func.AuthLevel.FUNCTION)
def health(req:func.HttpRequest)->func.HttpResponse:
    d=preflight()
    return func.HttpResponse(json.dumps(d),mimetype="application/json",status_code=200 if d["site_verified"] else 503)
