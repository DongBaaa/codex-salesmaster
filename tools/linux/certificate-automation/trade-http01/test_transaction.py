from pathlib import Path
from types import SimpleNamespace
import fcntl,hashlib,json,os,shutil,signal,socket,ssl,subprocess,tempfile,threading,time,unittest
from transaction import Transaction,Validator,TlsProbe,FILES,CID,SERVICE,DOMAIN,Refused

def openssl(args):
    p=subprocess.run(['openssl']+args,capture_output=True,timeout=20)
    assert p.returncode==0,'fixture generation failed'

class TransactionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.crypto=tempfile.TemporaryDirectory(prefix='workplan-cert-crypto-')
        cls.c=Path(cls.crypto.name)
        openssl(['req','-x509','-newkey','rsa:2048','-nodes','-days','90','-subj','/CN=TradePlan Isolated Test CA','-addext','keyUsage=critical,keyCertSign,cRLSign','-keyout',str(cls.c/'ca.key'),'-out',str(cls.c/'ca.pem')])
        for name,domain in [('old',DOMAIN),('new',DOMAIN),('wrong','trade.2884.kr.attacker.invalid')]:
            d=cls.c/name;d.mkdir(mode=0o700)
            openssl(['req','-new','-newkey','ec','-pkeyopt','ec_paramgen_curve:prime256v1','-nodes','-subj','/CN='+domain,'-keyout',str(d/'privkey.pem'),'-out',str(d/'request.csr')])
            ext=d/'extensions';ext.write_text('subjectAltName=DNS:'+domain+'\nextendedKeyUsage=serverAuth\nbasicConstraints=critical,CA:FALSE\n')
            openssl(['x509','-req','-in',str(d/'request.csr'),'-CA',str(cls.c/'ca.pem'),'-CAkey',str(cls.c/'ca.key'),'-CAcreateserial','-days','60','-extfile',str(ext),'-out',str(d/'cert.pem')])
            (d/'chain.pem').write_bytes((cls.c/'ca.pem').read_bytes())
            (d/'fullchain.pem').write_bytes((d/'cert.pem').read_bytes()+(d/'chain.pem').read_bytes())
            for f in FILES:(d/f).chmod(0o600)
        cls.validator=Validator(cls.c/'ca.pem')
        cls.old_id=cls.validator(cls.c/'old');cls.new_id=cls.validator(cls.c/'new')
    @classmethod
    def tearDownClass(cls):cls.crypto.cleanup()
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(prefix='workplan-native-tx-');self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name);self.cert=self.root/'certificates';self.cert.mkdir(mode=0o700)
        self.stage=self.root/'trade-native-staging';self.stage.mkdir(mode=0o700)
        self.source=self.root/'candidate';self.source.mkdir(mode=0o700)
        self.dirs=[self.cert/'_archive'/CID,self.cert/SERVICE[0]/SERVICE[1]]
        for d in self.dirs:d.mkdir(parents=True,mode=0o700)
        self.info={CID:{'desc':DOMAIN,'services':[{'subscriber':SERVICE[0],'service':SERVICE[1],'isPkg':False}],'is_default':False},'Other':{'desc':'other.invalid','services':[],'is_default':True}}
        self.info_path=self.cert/'_archive/INFO';self.info_path.write_text(json.dumps(self.info));self.info_path.chmod(0o600)
        for d in self.dirs:self.copy(self.c/'old',d)
        self.copy(self.c/'new',self.source)
        self.served=self.old_id['leaf'];self.calls=[];self.behavior='success'
        self.tx=Transaction(self.cert,self.stage,self.validator,self.importer,lambda:self.served,uid=os.geteuid(),verify_attempts=1)
    def copy(self,source,destination):
        for f in FILES:
            p=destination/f
            p.write_bytes((source/f).read_bytes());p.chmod(0o600)
    def importer(self,path,cid,description):
        self.calls.append((str(path),cid,description))
        if self.behavior=='throw':raise RuntimeError('SECRET-PRIVATE-CANARY')
        if self.behavior=='interrupt':raise KeyboardInterrupt()
        if self.behavior=='wait':
            (self.root/'import-entered').write_text('ready')
            time.sleep(30)
        if self.behavior=='rejected':return SimpleNamespace(api_success=False,state='rejected',code='api_error')
        for d in self.dirs:self.copy(path,d)
        self.served=self.validator(path)['leaf']
        if self.behavior=='empty-after-change':return SimpleNamespace(api_success=False,state='uncertain',code='empty_response')
        if self.behavior=='stale-tls':self.served=self.old_id['leaf']
        if self.behavior=='partial':self.copy(self.c/'old',self.dirs[1])
        if self.behavior=='mapping-change':
            changed=json.loads(self.info_path.read_text());changed[CID]['services'][0]['service']='different';self.info_path.write_text(json.dumps(changed))
        if self.behavior=='unrelated-change':
            changed=json.loads(self.info_path.read_text());changed['Other']['is_default']=False;self.info_path.write_text(json.dumps(changed))
        return SimpleNamespace(api_success=True,state='acknowledged',code='api_success')
    def last_run(self):return sorted(self.stage.glob('run-*'))[-1]
    def metadata(self):return json.loads((self.last_run()/'journal.json').read_text())
    def test_apply_and_explicit_rollback_preserve_mapping(self):
        result=self.tx.apply(self.source);self.assertEqual(result['state'],'deployed')
        self.assertEqual(len(self.calls),1);self.assertEqual(self.tx.snapshot()['info'],self.info)
        run=self.stage/result['run']
        for name in ['archive','service']:
            for f in FILES:self.assertEqual((run/('backup-'+name)/f).read_bytes(),(self.c/'old'/f).read_bytes())
        self.assertTrue(all((p.stat().st_mode&0o777)==0o600 for p in run.rglob('*') if p.is_file()))
        restored=self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)
        self.assertEqual(restored['state'],'rolled-back');self.assertEqual(self.served,self.old_id['leaf'])
        self.assertEqual(self.tx.snapshot()['info'],self.info);self.assertEqual(len(self.calls),2)
    def test_uncertain_import_keeps_backup_and_does_not_retry(self):
        self.behavior='empty-after-change';result=self.tx.apply(self.source)
        self.assertEqual(result['state'],'recovery-required');self.assertEqual(len(self.calls),1)
        with self.assertRaisesRegex(Refused,'unresolved_transaction'):self.tx.apply(self.source)
        with self.assertRaisesRegex(Refused,'completion_reconciliation_required'):self.tx.rollback(result['run'],self.tx.snapshot()['observation'])
        self.behavior='success'
        self.assertEqual(self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)['state'],'rolled-back')
    def test_rejected_import_keeps_original_and_blocks_new_run(self):
        self.behavior='rejected';before=self.tx.snapshot()['observation'];result=self.tx.apply(self.source)
        self.assertEqual(result['state'],'recovery-required');self.assertEqual(self.tx.snapshot()['observation'],before)
        self.assertEqual(len(self.calls),1)
    def test_api_success_with_stale_tls_is_not_deployed(self):
        self.behavior='stale-tls';self.assertEqual(self.tx.apply(self.source)['state'],'recovery-required')
        self.assertEqual(len(self.calls),1)
    def test_delayed_tls_reconciliation_does_not_repeat_import(self):
        self.tx.verify_attempts=2;self.tx.verify_delay=.01;served_after_import=[]
        def served():
            if not self.calls:return self.old_id['leaf']
            served_after_import.append(True)
            return self.old_id['leaf'] if len(served_after_import)==1 else self.served
        self.tx.served=served
        self.assertEqual(self.tx.apply(self.source)['state'],'deployed')
        self.assertEqual(len(self.calls),1);self.assertEqual(len(served_after_import),2)
    def tls_server(self,name):
        listener=socket.socket();listener.bind(('127.0.0.1',0));listener.listen(4);listener.settimeout(.1)
        context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.c/name/'fullchain.pem',self.c/name/'privkey.pem')
        stop=threading.Event()
        def serve():
            while not stop.is_set():
                try:connection,_=listener.accept()
                except socket.timeout:continue
                except OSError:break
                try:
                    with context.wrap_socket(connection,server_side=True):pass
                except ssl.SSLError:connection.close()
        thread=threading.Thread(target=serve);thread.start()
        def cleanup():stop.set();listener.close();thread.join(timeout=3);self.assertFalse(thread.is_alive())
        self.addCleanup(cleanup)
        return listener.getsockname()[1]
    def test_real_tls_trusted_chain_hostname_and_fingerprint(self):
        port=self.tls_server('new')
        try:actual=TlsProbe(port,self.c/'ca.pem')()
        except Refused as e:
            print(json.dumps({'fixtureTlsVerifyCode':getattr(e.__context__,'verify_code',None),'fixtureTlsVerifyMessage':getattr(e.__context__,'verify_message',None)}))
            raise
        self.assertEqual(actual,self.new_id['leaf'])
    def test_real_tls_wrong_hostname_rejected(self):
        port=self.tls_server('wrong')
        with self.assertRaisesRegex(Refused,'public_tls_verification_failed') as caught:TlsProbe(port,self.c/'ca.pem')()
        self.assertEqual(getattr(caught.exception.__context__,'verify_code',None),62)
    def test_real_tls_untrusted_chain_rejected(self):
        port=self.tls_server('new')
        with self.assertRaisesRegex(Refused,'public_tls_verification_failed') as caught:TlsProbe(port)()
        self.assertIn(getattr(caught.exception.__context__,'verify_code',None),(19,20))
    def test_partial_file_deployment_recovered_explicitly(self):
        self.behavior='partial';result=self.tx.apply(self.source);self.assertEqual(result['state'],'recovery-required')
        self.behavior='success';self.assertEqual(self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)['state'],'rolled-back')
    def test_mapping_change_blocks_rollback(self):
        self.behavior='mapping-change';result=self.tx.apply(self.source);self.assertEqual(result['state'],'recovery-required')
        with self.assertRaisesRegex(Refused,'service_mapping_changed'):self.tx.rollback(result['run'],'observation',True)
        self.assertEqual(len(self.calls),1)
    def test_unrelated_default_change_blocks_rollback(self):
        self.behavior='unrelated-change';result=self.tx.apply(self.source);self.assertEqual(result['state'],'recovery-required')
        with self.assertRaisesRegex(Refused,'rollback_mapping_conflict'):self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)
        self.assertEqual(len(self.calls),1)
    def test_observation_change_refuses_rollback(self):
        result=self.tx.apply(self.source)
        with self.assertRaisesRegex(Refused,'live_changed_before_rollback'):self.tx.rollback(result['run'],'stale-observation',True)
        self.assertEqual(len(self.calls),1)
    def test_tampered_backup_refuses_rollback(self):
        result=self.tx.apply(self.source);(self.last_run()/'backup-archive/privkey.pem').write_text('CORRUPTED')
        with self.assertRaisesRegex(Refused,'backup_changed'):self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)
        self.assertEqual(len(self.calls),1)
    def test_invalid_candidate_never_imported(self):
        self.copy(self.c/'wrong',self.source)
        with self.assertRaisesRegex(Refused,'exact_san_required'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[]);self.assertEqual(self.metadata()['state'],'precondition-refused')
    def test_wrong_key_never_imported(self):
        (self.source/'privkey.pem').write_bytes((self.c/'old/privkey.pem').read_bytes())
        with self.assertRaisesRegex(Refused,'key_mismatch'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_wrong_fullchain_never_imported(self):
        (self.source/'fullchain.pem').write_bytes((self.c/'old/fullchain.pem').read_bytes())
        with self.assertRaisesRegex(Refused,'fullchain_mismatch'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_private_key_permissions_refused(self):
        (self.source/'privkey.pem').chmod(0o644)
        with self.assertRaisesRegex(Refused,'unsafe_file_permissions'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_symlink_candidate_refused(self):
        p=self.source/'privkey.pem';p.unlink();p.symlink_to(self.c/'new/privkey.pem')
        with self.assertRaises(OSError):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_group_writable_staging_refused(self):
        self.stage.chmod(0o770)
        with self.assertRaisesRegex(Refused,'unsafe_directory'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_writable_parent_outside_staging_bound_refused(self):
        self.root.chmod(0o777)
        with self.assertRaisesRegex(Refused,'unsafe_directory'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_candidate_directory_symlink_refused(self):
        alias=self.root/'candidate-alias';alias.symlink_to(self.source,target_is_directory=True)
        with self.assertRaisesRegex(Refused,'noncanonical_directory'):self.tx.apply(alias)
        self.assertEqual(self.calls,[])
    def test_hardlinked_private_key_refused(self):
        os.link(self.source/'privkey.pem',self.root/'extra-key-link')
        with self.assertRaisesRegex(Refused,'unsafe_file'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_backup_and_journal_exist_before_import(self):
        original=self.tx.importer
        def assert_before_import(path,cid,description):
            run=Path(path).parent
            self.assertEqual(json.loads((run/'journal.json').read_text())['state'],'importing')
            for label in ['archive','service']:
                for filename in FILES:self.assertEqual((run/('backup-'+label)/filename).read_bytes(),(self.c/'old'/filename).read_bytes())
            self.assertEqual((run/'INFO.backup').read_bytes(),self.info_path.read_bytes())
            return original(path,cid,description)
        self.tx.importer=assert_before_import
        self.assertEqual(self.tx.apply(self.source)['state'],'deployed')
    def test_duplicate_description_refused(self):
        self.info['Duplicate']={'desc':DOMAIN};self.info_path.write_text(json.dumps(self.info))
        with self.assertRaisesRegex(Refused,'certificate_identity_changed'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_concurrent_import_lock_refused(self):
        with self.tx.lock():
            with self.assertRaisesRegex(Refused,'locked'):self.tx.apply(self.source)
        self.assertEqual(self.calls,[])
    def test_exception_and_interrupt_leave_no_secret_in_journal(self):
        for behavior in ['throw','interrupt']:
            self.behavior=behavior;result=self.tx.apply(self.source)
            self.assertEqual(result['state'],'recovery-required');self.assertNotIn('SECRET-PRIVATE-CANARY',json.dumps(self.metadata()))
            self.behavior='success';self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)
    def test_sigterm_retains_importing_journal_and_releases_lock(self):
        pid=os.fork()
        if pid==0:
            self.behavior='wait';self.tx.apply(self.source);os._exit(0)
        try:
            deadline=time.monotonic()+10
            while not (self.root/'import-entered').exists() and time.monotonic()<deadline:time.sleep(.02)
            self.assertTrue((self.root/'import-entered').exists())
            os.kill(pid,signal.SIGTERM);_,status=os.waitpid(pid,0);pid=0
            self.assertTrue(os.WIFSIGNALED(status));self.assertEqual(self.metadata()['state'],'importing')
            with self.assertRaisesRegex(Refused,'unresolved_transaction'):self.tx.apply(self.source)
            run=self.last_run();self.assertTrue((run/'backup-archive/privkey.pem').is_file())
            # The fake importer ran only in the now-reaped child; no remote API.
            self.assertEqual(self.tx.rollback(run.name,self.tx.snapshot()['observation'],True)['state'],'rolled-back')
        finally:
            if pid:os.kill(pid,signal.SIGTERM);os.waitpid(pid,0)


    def interrupted_rollback(self,after_write=False):
        result=self.tx.apply(self.source)
        # Read the shared filesystem so a child update is visible after waitpid.
        self.tx.served=lambda:self.validator(self.dirs[1])['leaf']
        original=self.tx.importer
        def interrupted(path,cid,description):
            if after_write:original(path,cid,description)
            (self.root/'rollback-entered').write_text('ready')
            time.sleep(30)
        self.tx.importer=interrupted
        pid=os.fork()
        if pid==0:
            self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)
            os._exit(0)
        try:
            deadline=time.monotonic()+10
            while not (self.root/'rollback-entered').exists() and time.monotonic()<deadline:time.sleep(.02)
            self.assertTrue((self.root/'rollback-entered').exists())
            os.kill(pid,signal.SIGTERM);_,status=os.waitpid(pid,0);pid=0
            self.assertTrue(os.WIFSIGNALED(status))
            self.assertEqual(self.metadata()['state'],'rolling-back')
            self.tx.importer=original
            with self.assertRaisesRegex(Refused,'unresolved_transaction'):self.tx.apply(self.source)
            with self.assertRaisesRegex(Refused,'completion_reconciliation_required'):
                self.tx.rollback(result['run'],self.tx.snapshot()['observation'])
            with self.assertRaisesRegex(Refused,'live_changed_before_rollback'):
                self.tx.rollback(result['run'],'outdated-observation',True)
            self.assertEqual(len(self.calls),1)
            # The only in-flight importer was in the child, now positively reaped.
            restored=self.tx.rollback(result['run'],self.tx.snapshot()['observation'],True)
            self.assertEqual(restored['state'],'rolled-back')
            self.assertEqual(self.tx.served(),self.old_id['leaf'])
            self.assertEqual(self.tx.snapshot()['info'],self.info)
            for name in ['archive','service']:
                for f in FILES:self.assertEqual((self.last_run()/('backup-'+name)/f).read_bytes(),(self.c/'old'/f).read_bytes())
            self.assertEqual(len(self.calls),2)
        finally:
            if pid:os.kill(pid,signal.SIGTERM);os.waitpid(pid,0)
    def test_sigterm_during_rollback_before_write_can_recover_explicitly(self):
        self.interrupted_rollback(False)
    def test_sigterm_during_rollback_after_write_can_recover_explicitly(self):
        self.interrupted_rollback(True)

if __name__=='__main__':unittest.main(verbosity=2)
