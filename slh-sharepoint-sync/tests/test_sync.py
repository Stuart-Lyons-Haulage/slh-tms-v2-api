import importlib.util, pathlib, sys, types
af=types.ModuleType("azure.functions"); af.FunctionApp=lambda:None
class Dummy: pass
af.TimerRequest=af.HttpRequest=Dummy; af.AuthLevel=types.SimpleNamespace(FUNCTION=1,ANONYMOUS=2)
az=types.ModuleType("azure"); az.functions=af
sys.modules.setdefault("azure",az); sys.modules.setdefault("azure.functions",af)
ai=types.ModuleType("azure.identity"); ai.DefaultAzureCredential=lambda:Dummy()
ak=types.ModuleType("azure.keyvault.secrets"); ak.SecretClient=Dummy
sys.modules.setdefault("azure.identity",ai); sys.modules.setdefault("azure.keyvault.secrets",ak)
sys.modules.setdefault("requests",types.ModuleType("requests"))
spec=importlib.util.spec_from_file_location("sync",pathlib.Path(__file__).parents[1]/"function_app.py")
# Import helpers without evaluating decorators.
src=(pathlib.Path(__file__).parents[1]/"function_app.py").read_text(); src=src.split("@app.function_name")[0]
m=types.ModuleType("sync"); exec(compile(src,"function_app.py","exec"),m.__dict__)
def test_normalisation(): assert m.norm(" fn-69 abf ")=="FN69ABF"
def test_fleet_classification():
 assert m.fleet_kind({"vehicle_type_name":"Unit"})=="vehicles"
 assert m.fleet_kind({"vehicle_type_name":"Trailer"})=="trailers"
 assert m.fleet_kind({"name":"Tacho Card Reader","vehicle_type_name":"Car"}) is None
def test_source_id_wins():
 rows=[{"fields":{"FleetioId":"7","field_1":"OLD"}}]
 assert m.choose(rows,"FleetioId","7","field_1","NEW")[1]=="source"
def test_ambiguity_refused():
 rows=[{"fields":{"field_1":"AA-1"}},{"fields":{"field_1":"AA1"}}]
 assert m.choose(rows,"FleetioId","9","field_1","AA1")[1]=="ambiguous"
def test_blanks_do_not_overwrite(): assert m.delta({"x":"valid"},{"x":None})=={}
def test_sage_role_filter():
 assert m.sage_driver({"position":"HGV Driver"})
 assert not m.sage_driver({"position":"Administrator"})
def test_trailer_identifier():
 assert m.trailer_id({"name":"SLH03"})=="3"
 assert m.trailer_id({"name":"SLH46DD NEW"})=="46DD"
 assert m.trailer_norm("03")=="3"
def test_fleet_status_ownership():
 assert m.fleet_active("Active")=="Yes"
 assert m.fleet_active("VOR")=="Yes"
 assert m.fleet_active("Sold")=="No"
def test_roadtech_time():
 assert m.roadtech_time({"dataGps":{"Time":"2026-10-28T12:00:00Z"}})=="2026-10-28T12:00:00Z"
 assert m.roadtech_time({}) is None
