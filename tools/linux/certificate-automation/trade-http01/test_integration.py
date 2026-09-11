from pathlib import Path
from types import SimpleNamespace
import json,os,subprocess,unittest
import test_transaction as fixtures
from transaction import DOMAIN,FILES,Refused
from native_api import request_import
from native_deploy import dispatch,main

class IntegrationTests(fixtures.TransactionTests):
    def setUp(self):
        super().setUp()
        self.api_calls=[];self.reply='success'
        self.tx.importer=self.native_import
        for f,n in zip(FILES,[DOMAIN+'.cer','ca.cer','fullchain.cer',DOMAIN+'.key']):
            (self.source/f).rename(self.source/n)
    def native_import(self,path,cid,description):
        # Mock namespace translation only; production adapter's path guard stays intact.
        logical=Path('/volume1/workplan-certificate-automation/trade-native-staging')/path.relative_to(self.stage)
        def run(args,**kwargs):
            self.api_calls.append(args)
            values={k:json.loads(v) for k,v in [a.split('=',1) for a in args if a.startswith(('key_tmp=','cert_tmp=','inter_cert_tmp=','id=','desc='))]}
            self.assertEqual(values['id'],'arrkjO');self.assertEqual(values['desc'],DOMAIN)
            for key,name in [('key_tmp','privkey.pem'),('cert_tmp','cert.pem'),('inter_cert_tmp','chain.pem')]:
                self.assertEqual(values[key],str(logical/name))
                self.assertTrue((path/name).is_file())
            if self.reply!='ack-no-change':
                for d in self.dirs:self.copy(path,d)
                self.served=self.validator(path)['leaf']
            if self.reply=='timeout':raise subprocess.TimeoutExpired(args,90,stderr='PRIVATE-CANARY')
            return SimpleNamespace(returncode=0,stdout='' if self.reply=='empty' else '{"success":true}',stderr='PRIVATE-CANARY')
        return request_import(logical,cid,description,run)
    def test_acme_names_deploy_and_explicit_rollback(self):
        self.assertEqual(dispatch(self.tx,'preflight')['state'],'ready')
        result=dispatch(self.tx,'apply',self.source)
        self.assertEqual(result['state'],'deployed');self.assertEqual(len(self.api_calls),1)
        observation=dispatch(self.tx,'observe')['observation']
        with self.assertRaises(Refused):dispatch(self.tx,'rollback',run=result['run'],observed=observation)
        self.assertEqual(len(self.api_calls),1)
        result=dispatch(self.tx,'rollback',run=result['run'],observed=observation,ended=True)
        self.assertEqual(result['state'],'rolled-back');self.assertEqual(len(self.api_calls),2)
        self.assertEqual(dispatch(self.tx,'preflight')['state'],'ready')
    def uncertain(self,reply):
        self.reply=reply
        result=dispatch(self.tx,'apply',self.source)
        self.assertEqual(result['state'],'recovery-required')
        self.assertNotIn('PRIVATE-CANARY',json.dumps(result))
        self.assertEqual(len(self.api_calls),1)
        with self.assertRaisesRegex(Refused,'unresolved_transaction'):dispatch(self.tx,'preflight')
        with self.assertRaisesRegex(Refused,'unresolved_transaction'):dispatch(self.tx,'apply',self.source)
        self.assertEqual(len(self.api_calls),1)
    def test_empty_after_change_blocks_preflight_and_reimport(self):self.uncertain('empty')
    def test_timeout_after_change_blocks_preflight_and_reimport(self):self.uncertain('timeout')
    def test_ack_without_installation_is_not_success(self):self.uncertain('ack-no-change')
    def test_wrong_source_format_no_api(self):
        with self.assertRaisesRegex(Refused,'unsupported_source_format'):self.tx.apply(self.source,'../../key')
        self.assertEqual(self.api_calls,[])
    def test_missing_acme_key_no_api(self):
        (self.source/(DOMAIN+'.key')).unlink()
        with self.assertRaises(FileNotFoundError):dispatch(self.tx,'apply',self.source)
        self.assertEqual(self.api_calls,[])
    def test_acme_key_symlink_no_api(self):
        p=self.source/(DOMAIN+'.key');p.unlink();p.symlink_to(self.c/'new/privkey.pem')
        with self.assertRaises(OSError):dispatch(self.tx,'apply',self.source)
        self.assertEqual(self.api_calls,[])
    def test_unknown_mode_no_api(self):
        with self.assertRaises(ValueError):dispatch(self.tx,'unknown',self.source)
        self.assertEqual(self.api_calls,[])

def load_tests(loader,tests,pattern):
    # Inherited transaction cases run separately against their original fixture.
    return unittest.TestSuite(IntegrationTests(n) for n in IntegrationTests.__dict__ if n.startswith('test_'))

if __name__=='__main__':unittest.main(verbosity=2)
