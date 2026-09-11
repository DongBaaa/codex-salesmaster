from contextlib import redirect_stdout,redirect_stderr
from pathlib import Path
from unittest.mock import patch
import io,json,os,tempfile,unittest
import native_deploy
from transaction import Refused

class CoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(prefix='workplan-acme-config-');self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name)
        self.domain=self.root/'trade.2884.kr_ecc';self.domain.mkdir(mode=0o700)
    def check(self,text,account=False):
        path=self.root/'account.conf' if account else self.domain/'trade.2884.kr.conf'
        path.write_text(text);path.chmod(0o600)
        native_deploy.validate_acme_config(self.root,os.geteuid())
    def test_fresh_config_and_plain_domain_config_allowed(self):
        native_deploy.validate_acme_config(self.root,os.geteuid())
        self.check("Le_Domain='trade.2884.kr'\nLe_Webroot='/volume1/acme'\nLe_Keylength='ec-256'\nLe_DeployHook=''\n")
    def test_saved_deploy_reload_hook_and_install_path_refused(self):
        for key in ('Le_DeployHook','Le_ReloadCmd','Le_ReloadCmd_saved','Le_PreHook','Le_PostHook','Le_RenewHook','Le_RealCertPath','Le_RealKeyPath','Le_RealCACertPath','Le_RealFullChainPath','NOTIFY_HOOK','ACCOUNT_CONF_PATH'):
            with self.subTest(key=key),self.assertRaisesRegex(Refused,'saved_acme_side_effect_refused'):
                self.check(key+"='FIXTURE'\n")
    def test_account_saved_hook_refused(self):
        with self.assertRaisesRegex(Refused,'saved_acme_side_effect_refused'):self.check("SAVED_Le_DeployHook='FIXTURE'",True)
    def test_shell_expressions_never_evaluated(self):
        for text in ('Le_Domain=$(echo trade.2884.kr)','source /tmp/script','Le_Domain="`echo trade.2884.kr`"',"X='ok'; echo PRIVATE-CANARY"):
            with self.subTest(text=text),self.assertRaisesRegex(Refused,'nonliteral_acme_config'):self.check(text)
    def test_duplicate_and_wrong_domain_refused(self):
        for text in ("Le_Domain='work.2884.kr'","X='a'\nX='b'"):
            with self.assertRaises(Refused):self.check(text)
    def test_nonroot_cli_refuses_before_api_and_sanitizes(self):
        out=io.StringIO()
        with patch.object(native_deploy.os,'geteuid',return_value=1000),redirect_stdout(out):
            self.assertEqual(native_deploy.main(['--preflight']),2)
        self.assertEqual(json.loads(out.getvalue()),{'state':'refused','errorType':'PermissionError'})
    def test_rollback_cli_requires_explicit_observation_and_confirmation(self):
        with redirect_stderr(io.StringIO()),self.assertRaises(SystemExit) as e:
            native_deploy.main(['--rollback','run-'+'1'*32])
        self.assertEqual(e.exception.code,2)

if __name__=='__main__':unittest.main(verbosity=2)
