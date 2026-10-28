import os
import pathlib
import sys
import types
from unittest.mock import patch

import unittest

af = types.ModuleType('azure.functions')
af.FunctionApp = lambda: None
az = types.ModuleType('azure')
ai = types.ModuleType('azure.identity')
ai.ManagedIdentityCredential = lambda: object()
kv = types.ModuleType('azure.keyvault.secrets')
kv.SecretClient = object
modules = {'azure': az, 'azure.functions': af, 'azure.identity': ai, 'azure.keyvault.secrets': kv}
rq = types.ModuleType('requests')
rq.Session = lambda: types.SimpleNamespace(headers={})
rq.RequestException = OSError
modules['requests'] = rq
src = (pathlib.Path(__file__).parents[1] / 'function_app.py').read_text().split('@app.function_name')[0]
m = types.ModuleType('integration')
with patch.dict(sys.modules, modules):
    exec(compile(src, 'function_app.py', 'exec'), m.__dict__)

class SafetyTests(unittest.TestCase):
    def test_unverified_writes_are_locked(self):
        with patch.dict(os.environ, {'SLH_WRITES_VERIFIED':'false'}):
            g = m.Graph()
            with self.assertRaisesRegex(RuntimeError, 'locked'):
                g.update('vehicles','1',{'FleetioName':'test'})
            with self.assertRaisesRegex(RuntimeError, 'locked'):
                g.create('vehicles',{'Title':'test'})
    
    def test_schema_is_never_automatically_changed(self):
        with self.assertRaisesRegex(RuntimeError, 'schema'):
            m.Graph().add_column('vehicles','DuplicateField')
    
    def test_unchanged_and_blank_fields_are_not_written(self):
        assert m.delta({'x':'keep','y':'same'},{'x':None,'y':'same'}) == {}
    
    def test_duplicate_matching_is_refused(self):
        rows=[{'fields':{'FleetioId':'123'}},{'fields':{'FleetioId':'123'}}]
        assert m.choose(rows,'FleetioId','123','field_1','AB12CDE') == (None,'ambiguous')
    
    def test_external_id_survives_registration_change(self):
        row={'fields':{'FleetioId':'123','field_1':'OLD'}}
        assert m.choose([row],'FleetioId','123','field_1','NEW') == (row,'source')
    
    def test_cursor_pagination_reads_all_records(self):
        class Response:
            links={};headers={}
            def __init__(self,data): self.data=data
            def json(self): return self.data
        calls=[]
        pages=[{'records':[{'id':1}],'next_cursor':'next'}, {'records':[{'id':2}],'next_cursor':None}]
        def request(self,method,url,**kw):
            calls.append(url);return Response(pages.pop(0))
        with patch.object(m,'secret',return_value='fake'), patch.object(m.Http,'request',request):
            assert m.fleetio_assets() == [{'id':1},{'id':2}]
        assert 'start_cursor=next' in calls[1]
    
    def test_error_output_does_not_include_response_or_credentials(self):
        class Response:
            status_code=401;ok=False
        h=m.Http();h.s.request=lambda *a,**k: Response()
        with self.assertRaises(RuntimeError) as error:
            h.request('GET','https://secure.fleetio.com/api/v1/vehicles?secret=fake')
        assert str(error.exception) == 'HTTP 401 from secure.fleetio.com'
