"""Execute the candidate shell with explicit fixture path/root/command substitutions.
Crypto and the real native adapter are exercised separately, not by these stubs.
"""
from pathlib import Path
import json,os,subprocess,sys,tempfile,unittest

class ShellTests(unittest.TestCase):
    def scenario(self,preflight=0,apply=0,due=True,existing=False,mode='scheduled',legacy_lock=False,tls_hang=False):
        with tempfile.TemporaryDirectory(prefix='workplan-native-shell-') as tmp:
            root=Path(tmp);base=root/'base';bin=root/'bin';bin.mkdir();base.mkdir()
            legacy=base/'state';legacy.mkdir();legacy_status=legacy/'last-status-trade.2884.kr.txt';legacy_status.write_text('LEGACY_STATUS_UNCHANGED')
            if legacy_lock:
                (legacy/'renew.lock').mkdir();(legacy/'renew.lock/pid').write_text(str(os.getpid()))
            events=root/'events';web=root/'web';(web/'.well-known/acme-challenge').mkdir(parents=True)
            source=Path(__file__).with_name('renew-and-deploy.sh').read_text()
            self.assertNotIn('SYNO_USE_TEMP_ADMIN',source)
            self.assertNotIn('--deploy-hook',source)
            self.assertLess(source.index('"$NATIVE_CLI" --preflight'),source.index('renew_seconds='))
            # Explicit test-only substitutions, never used in the production candidate.
            work=base/'trade-http01'
            self.assertEqual(source.count('BASE_DIR="/volume1/workplan-certificate-automation/trade-http01"'),1)
            source=source.replace('BASE_DIR="/volume1/workplan-certificate-automation/trade-http01"','BASE_DIR="'+str(work)+'"')
            source=source.replace('ACME_BIN="/volume1/workplan-certificate-automation/vendor/', 'ACME_BIN="'+str(base)+'/vendor/')
            source=source.replace('PATH=/usr/syno/bin:/usr/local/bin:/usr/bin:/bin','PATH='+str(bin)+':/usr/bin:/bin')
            if tls_hang:source=source.replace('timeout 20 openssl','timeout 1 openssl')
            candidate=root/'candidate.sh';candidate.write_text(source)
            def executable(path,code):
                path.parent.mkdir(parents=True,exist_ok=True)
                path.write_text('#!'+sys.executable+'\n'+code);path.chmod(0o700)
            executable(bin/'id',"print('0')\n")
            executable(bin/'stat',"import sys\nprint('0' if sys.argv[2]=='%u' else '755')\n")
            executable(bin/'python3',f'''import sys,json
from pathlib import Path
mode=sys.argv[-1]
with Path({str(events)!r}).open('a') as f:f.write(mode+'\\n')
code={preflight} if mode=='--preflight' else {apply}
print(json.dumps({{'state':'ready' if mode=='--preflight' else 'deployed'}}))
raise SystemExit(code)
''')
            executable(bin/'openssl',f'''import sys
a=sys.argv[1:]
if a[0]=='s_client' and {tls_hang!r}:
 import time;time.sleep(4)
if a[0]=='s_client' or '-outform' in a: print('PUBLIC_FIXTURE')
elif '-checkend' in a:
 public=any('public-cert.' in v for v in a)
 raise SystemExit(1 if public and {due!r} else 0)
elif '-ext' in a:print('DNS:trade.2884.kr')
elif '-pubkey' in a or a[0]=='pkey':print('PUBLIC_KEY_FIXTURE')
elif '-issuer' in a:print('issuer=Fixture')
elif '-fingerprint' in a:print('sha256 Fingerprint='+'AA'*32)
else:print('subject=Fixture')
''')
            executable(base/'vendor/acme.sh-3.1.4/acme.sh',f'''import sys
from pathlib import Path
with Path({str(events)!r}).open('a') as f:f.write('acme\\n')
a=sys.argv[1:]
assert a[a.index('--home')+1]==a[a.index('--config-home')+1]==a[a.index('--cert-home')+1]
config=Path(a[a.index('--cert-home')+1])/'trade.2884.kr_ecc'
config.mkdir(parents=True,exist_ok=True)
(config/'fullchain.cer').write_text('CERT_FIXTURE')
(config/'trade.2884.kr.key').write_text('KEY_FIXTURE')
''')
            if existing:
                config=work/'config-http01-trade.2884.kr/trade.2884.kr_ecc';config.mkdir(parents=True)
                (config/'fullchain.cer').write_text('CERT_FIXTURE');(config/'trade.2884.kr.key').write_text('KEY_FIXTURE')
            env={'PATH':'/usr/bin:/bin','WORKPLAN_ACME_HTTP_WEBROOT':str(web)}
            run=subprocess.run(['sh',str(candidate),mode],env=env,capture_output=True,text=True,timeout=20)
            result=events.read_text().splitlines() if events.exists() else []
            self.assertFalse((work/'state/renew.lock').exists(),run.stderr)
            self.assertEqual((legacy/'renew.lock').exists(),legacy_lock)
            return run,result,legacy_status.read_text()
    def test_preflight_failure_stops_issue(self):
        r,e,_=self.scenario(preflight=2)
        self.assertNotEqual(r.returncode,0);self.assertEqual(e,['--preflight'])
    def test_preflight_failure_stops_not_due_shortcut(self):
        r,e,_=self.scenario(preflight=2,due=False)
        self.assertNotEqual(r.returncode,0);self.assertEqual(e,['--preflight'])
    def test_preflight_failure_stops_existing_certificate_retry(self):
        r,e,_=self.scenario(preflight=2,existing=True)
        self.assertNotEqual(r.returncode,0);self.assertEqual(e,['--preflight'])
    def test_not_due_only_after_preflight(self):
        r,e,_=self.scenario(due=False)
        self.assertEqual(r.returncode,0,r.stderr);self.assertEqual(e,['--preflight'])
    def test_new_issue_then_native_apply(self):
        r,e,_=self.scenario()
        self.assertEqual(r.returncode,0,r.stderr);self.assertEqual(e,['--preflight','acme','--apply-http01'])
    def test_native_failure_is_not_reported_deployed(self):
        r,e,_=self.scenario(apply=2)
        self.assertNotEqual(r.returncode,0);self.assertEqual(e,['--preflight','acme','--apply-http01'])
        self.assertNotIn('deploy_ok',r.stdout)
    def test_existing_candidate_uses_native_once(self):
        r,e,_=self.scenario(existing=True)
        self.assertEqual(r.returncode,0,r.stderr);self.assertEqual(e,['--preflight','--apply-http01'])

class LegacyIsolationTests(ShellTests):
    def test_unresponsive_tls_fails_without_issue_and_releases_lock(self):
        r,e,status=self.scenario(due=False,tls_hang=True)
        self.assertNotEqual(r.returncode,0);self.assertEqual(e,['--preflight'])
        self.assertIn('public_certificate_unavailable',r.stdout)
    def test_check_only_not_due_never_issues(self):
        r,e,status=self.scenario(due=False,mode='--check-only')
        self.assertEqual(r.returncode,0,r.stderr);self.assertEqual(e,['--preflight'])
    def test_check_only_due_never_issues_or_imports(self):
        r,e,status=self.scenario(due=True,existing=True,mode='--check-only')
        self.assertNotEqual(r.returncode,0);self.assertEqual(e,['--preflight'])
        self.assertIn('renewal_due_check_only_no_issue',r.stdout)
    def test_work_not_due_preserves_legacy_status(self):
        r,e,status=self.scenario(due=False)
        self.assertEqual(r.returncode,0,r.stderr)
        self.assertEqual(status,'LEGACY_STATUS_UNCHANGED')
    def test_legacy_lock_does_not_skip_work_check(self):
        r,e,status=self.scenario(due=False,legacy_lock=True)
        self.assertEqual(r.returncode,0,r.stderr)
        self.assertEqual(e,['--preflight'])
        self.assertEqual(status,'LEGACY_STATUS_UNCHANGED')

def load_tests(loader,tests,pattern):
    return unittest.TestSuite([loader.loadTestsFromTestCase(ShellTests), unittest.TestSuite(LegacyIsolationTests(n) for n in LegacyIsolationTests.__dict__ if n.startswith('test_'))])

if __name__=='__main__':unittest.main(verbosity=2)
