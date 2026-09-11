import importlib.util,unittest,copy
from pathlib import Path
s=importlib.util.spec_from_file_location('runner',Path(__file__).with_name('run-once.py'));m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
class Runner(unittest.TestCase):
    def setUp(self):self.before={m.DOMAIN:{'sha256':'old'},'work.2884.kr':{'sha256':'work'}}
    def test_staging_never_uses_production_mode(self):
        args,env=m.command('staging');self.assertEqual(args[-1],'--staging-test');self.assertNotIn('WORKPLAN_ACME_RENEW_BEFORE_DAYS',env)
    def test_production_mode_is_explicit_once_threshold(self):
        args,env=m.command('production');self.assertEqual(args[-1],'--scheduled');self.assertEqual(env['WORKPLAN_ACME_RENEW_BEFORE_DAYS'],'90')
    def test_invalid_mode_refused(self):
        with self.assertRaises(m.Refused):m.command('force-all')
    def test_staging_certificate_change_refused(self):
        changed=copy.deepcopy(self.before);changed[m.DOMAIN]['sha256']='new'
        with self.assertRaises(m.Refused):m.validate_after('staging',self.before,changed,{'status':'staging_passed'})
    def test_production_wrong_other_domain_refused(self):
        changed=copy.deepcopy(self.before);changed[m.DOMAIN]['sha256']='new';changed['work.2884.kr']['sha256']='changed'
        with self.assertRaises(m.Refused):m.validate_after('production',self.before,changed,{'status':'deployed','fingerprint':'new'})
    def test_exit_success_without_changed_cert_refused(self):
        with self.assertRaises(m.Refused):m.validate_after('production',self.before,self.before,{'status':'deployed','fingerprint':'old'})
    def test_fingerprint_must_match_actual_tls(self):
        changed=copy.deepcopy(self.before);changed[m.DOMAIN]['sha256']='new'
        with self.assertRaises(m.Refused):m.validate_after('production',self.before,changed,{'status':'deployed','fingerprint':'wrong'})
        m.validate_after('production',self.before,changed,{'status':'deployed','fingerprint':'new'})
if __name__=='__main__':unittest.main()
