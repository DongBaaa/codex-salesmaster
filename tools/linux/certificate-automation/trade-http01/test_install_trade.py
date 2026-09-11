from pathlib import Path
import json,os,tempfile,unittest,subprocess,sys
from install_trade import Installer,Paths,TradeEffects,publish_new
from install_support import Refused,create,sha

class Fake(TradeEffects):
    def __init__(self,p,failure):self.p=p;self.failure=failure;self.events=[];self.snaps=0;self.reloads=0
    def snapshot(self):
        self.snaps+=1
        return {'tls':'changed' if self.failure=='tls' and self.snaps==2 else 'original'}
    def http_before(self):return [(404,None)]*3
    def native_preflight(self,r):
        if self.failure=='preflight':raise Refused('mock_preflight')
    def syntax(self):
        if self.failure=='syntax' and self.p.config.exists():raise Refused('mock_syntax')
    def reload(self):
        self.reloads+=1
        if self.failure=='reload' and self.reloads==1:raise Refused('mock_reload')
    def redirects(self):self.events.append('redirects')
    def challenge(self,token,body):
        if self.failure=='concurrent':self.p.config.write_bytes(b'EXTERNAL_CHANGE')
        if self.failure in ('challenge','concurrent'):raise Refused('mock_challenge')
        self.asserted_probe=body
    def check_only(self,r,w):
        if self.failure=='check':raise Refused('mock_check')

class InstallTrade(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name);self.uid=os.geteuid()
        base=self.root/'base';base.mkdir();web=self.root/'web';web.mkdir()
        config=self.root/'nginx/sites-enabled/trade.conf';config.parent.mkdir(parents=True)
        protected=config.parent/'work.conf';protected.write_bytes(b'WORK_PRESERVED')
        self.p=Paths(config,base,web,protected)
        for path,data in [(self.p.legacy,b'LEGACY_PRESERVED'),(self.p.vendor,b'VENDOR_PRESERVED')]:path.parent.mkdir(parents=True);path.write_bytes(data)
        self.expected={n:sha(getattr(self.p,n).read_bytes()) for n in ('protected','legacy','vendor')}
        self.payload=self.root/'payload';self.payload.mkdir(mode=0o700)
        self.hashes={}
        for n in ['renew-and-deploy.sh','native_deploy.py','native_api.py','transaction.py']:
            data=Path(__file__).with_name(n).read_bytes();create(self.payload/n,data);self.hashes[n]=sha(data)
    def instance(self,failure=None):
        self.fx=Fake(self.p,failure)
        return Installer(self.p,self.payload,self.hashes,self.fx,self.uid,self.expected)
    def test_success_enables_only_after_check_and_preserves_work(self):
        r=self.instance().install();self.assertEqual(r['state'],'installed-verified');self.assertEqual(self.fx.reloads,1)
        self.assertTrue((self.p.runtime/'state/install-verified.json').exists())
        self.assertEqual(self.p.protected.read_bytes(),b'WORK_PRESERVED');self.assertFalse(r['certificateIssueAttempted']);self.assertFalse(r['schedulerChanged'])
        self.assertNotIn('work.2884.kr',self.p.config.read_text());self.assertIn('https://trade.2884.kr$request_uri',self.p.config.read_text())
        self.assertFalse(list(Path(r['webroot']).rglob('TRADE_INSTALL_*')))
    def test_umask077_challenge_readable_private_state(self):
        previous=os.umask(0o077)
        try:r=self.instance().install()
        finally:os.umask(previous)
        for d in ['.','.well-known','.well-known/acme-challenge']:self.assertEqual((Path(r['webroot'])/d).stat().st_mode&0o777,0o755)
        self.assertEqual(self.p.stage.stat().st_mode&0o777,0o700);self.assertEqual(self.p.config.stat().st_mode&0o777,0o600)
    def failure(self,reason,reloads):
        r=self.instance(reason).install();self.assertEqual(r['state'],'install-failed-http-restored');self.assertFalse(self.p.config.exists());self.assertEqual(self.fx.reloads,reloads);self.assertFalse((self.p.runtime/'state/install-verified.json').exists());self.assertEqual(self.p.protected.read_bytes(),b'WORK_PRESERVED')
    def test_syntax_failure_removes_only_candidate_without_reload(self):self.failure('syntax',0)
    def test_uncertain_reload_restores_absence_and_reloads(self):self.failure('reload',2)
    def test_challenge_failure_restores_absence(self):self.failure('challenge',2)
    def test_check_only_failure_restores_absence(self):self.failure('check',2)
    def test_other_certificate_change_prevents_enable(self):self.failure('tls',2)
    def test_preflight_refusal_no_http_change(self):
        r=self.instance('preflight').install();self.assertEqual(r['state'],'install-failed');self.assertFalse(self.p.config.exists());self.assertEqual(self.fx.reloads,0)
    def test_concurrent_change_preserved_for_manual_recovery(self):
        r=self.instance('concurrent').install();self.assertEqual(r['state'],'manual-recovery-required');self.assertEqual(self.p.config.read_bytes(),b'EXTERNAL_CHANGE');self.assertEqual(self.fx.reloads,1)
    def test_existing_config_refused(self):
        self.p.config.write_bytes(b'EXISTING')
        with self.assertRaises(Refused):self.instance().install()
        self.assertEqual(self.p.config.read_bytes(),b'EXISTING')
    def test_existing_runtime_refused(self):
        self.p.runtime.mkdir()
        with self.assertRaises(Refused):self.instance().install()
    def test_existing_staging_journal_refused(self):
        self.p.stage.mkdir()
        with self.assertRaises(Refused):self.instance().install()
    def test_changed_work_baseline_refused_before_runtime_creation(self):
        self.p.protected.write_bytes(b'CHANGED')
        with self.assertRaises(Refused):self.instance().install()
        self.assertFalse(self.p.runtime.exists())
    def test_payload_tamper_refused(self):
        (self.payload/'native_api.py').write_bytes(b'TAMPERED')
        with self.assertRaises(Refused):self.instance().install()
        self.assertFalse(self.p.runtime.exists())
    def test_publication_never_overwrites_raced_file(self):
        self.p.config.write_bytes(b'RACED')
        with self.assertRaises(FileExistsError):publish_new(self.p.config,b'CANDIDATE')
        self.assertEqual(self.p.config.read_bytes(),b'RACED')
    def test_isolated_cli_imports_pinned_support_then_refuses_nonroot(self):
        if self.uid==0:self.skipTest('requires ordinary Linux test user')
        r=subprocess.run([sys.executable,'-I','-B',str(Path(__file__).with_name('install_trade.py')),'--install'],capture_output=True,text=True,timeout=10)
        self.assertEqual(r.returncode,2);self.assertEqual(json.loads(r.stdout),{'state':'refused','errorType':'Refused'})
        self.assertNotIn('ModuleNotFoundError',r.stderr)

if __name__=='__main__':unittest.main()
