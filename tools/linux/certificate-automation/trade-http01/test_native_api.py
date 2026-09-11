import json
import subprocess
import unittest
from types import SimpleNamespace
from native_api import import_arguments, classify_response, request_import

STAGE='/volume1/workplan-certificate-automation/trade-native-staging/run-1/candidate'

class NativeAdapterTests(unittest.TestCase):
    def test_response_matrix(self):
        cases=[
            (0,'',False,'empty_response'),
            (0,'   ',False,'empty_response'),
            (0,'{}',False,'explicit_success_missing'),
            (0,'{"success":false}',False,'explicit_success_missing'),
            (0,'{"success":"true"}',False,'explicit_success_missing'),
            (0,'{"success":1}',False,'explicit_success_missing'),
            (0,'{"success":true}',True,'api_success'),
            (0,'{"success":true,"data":{}}',True,'api_success'),
            (0,'{"success":true,"error":{}}',False,'api_error'),
            (0,'{"success":false,"success":true}',False,'invalid_json_response'),
            (0,'{"success":true} trailing',False,'invalid_json_response'),
            (0,'banner\n{"success":true}',False,'invalid_json_response'),
            (0,'[true]',False,'invalid_response_shape'),
            (255,'{"success":true}',False,'command_nonzero'),
            (0,'X'*(1024*1024+1),False,'oversized_response'),
        ]
        for status,body,expected,code in cases:
            with self.subTest(status=status,body=body[:40]):
                result=classify_response(status,body)
                self.assertEqual(result.api_success,expected)
                self.assertEqual(result.code,code)
                self.assertTrue(result.reconciliation_required)

    def test_fixed_work_target_and_json_arguments(self):
        args=import_arguments(STAGE,'arrkjO','trade.2884.kr')
        self.assertEqual(args[:5],['/usr/syno/bin/synowebapi','--exec-fastwebapi','api=SYNO.Core.Certificate','method=import','version=1'])
        values={k:json.loads(v) for k,v in (a.split('=',1) for a in args[5:])}
        self.assertEqual(values,{'key_tmp':STAGE+'/privkey.pem','cert_tmp':STAGE+'/cert.pem','inter_cert_tmp':STAGE+'/chain.pem','id':'arrkjO','desc':'trade.2884.kr'})
        self.assertNotIn('as_default',values)

    def test_invalid_scope(self):
        for path in ['/tmp/input','relative','/volume1/workplan-certificate-automation/trade-native-staging/../other','/volume1/workplan-certificate-automation/trade-native-staging/run/','/volume1/workplan-certificate-automation/trade-native-staging//run','/volume1/workplan-certificate-automation/trade-native-staging/run\nother']:
            with self.subTest(path=path),self.assertRaises(ValueError):
                import_arguments(path,'arrkjO','trade.2884.kr')
        for cid in ['../x','', 'abc;command', 'a'*65]:
            with self.subTest(cid=cid),self.assertRaises(ValueError):
                import_arguments(STAGE,cid,'trade.2884.kr')
        with self.assertRaises(ValueError):import_arguments(STAGE,'arrkjO','work.2884.kr')

    def test_no_retry_and_no_secret_output_on_uncertain_result(self):
        for failure in [subprocess.TimeoutExpired('synowebapi',90,output='SECRET-CANARY'),OSError('SECRET-CANARY')]:
            calls=[]
            def run(args,**kwargs):calls.append((args,kwargs));raise failure
            result=request_import(STAGE,'arrkjO','trade.2884.kr',run)
            self.assertEqual(len(calls),1)
            self.assertEqual(result.state,'uncertain')
            self.assertNotIn('SECRET-CANARY',repr(result))
            self.assertNotIn('shell',calls[0][1])
            self.assertEqual(set(calls[0][1]['env']),{'PATH','LC_ALL'})

    def test_api_acknowledgement_is_not_deployment_success(self):
        calls=[]
        def run(args,**kwargs):
            calls.append(args)
            return SimpleNamespace(returncode=0,stdout='{"success":true}',stderr='SECRET-CANARY')
        result=request_import(STAGE,'arrkjO','trade.2884.kr',run)
        self.assertEqual(len(calls),1)
        self.assertTrue(result.api_success)
        self.assertTrue(result.reconciliation_required)
        self.assertNotIn('SECRET-CANARY',repr(result))

if __name__=='__main__':unittest.main(verbosity=2)
