import unittest
from pathlib import Path
import native_api,native_deploy,transaction

class TradeScope(unittest.TestCase):
    def test_api_cannot_import_work_description_or_work_staging(self):
        with self.assertRaises(ValueError):native_api.import_arguments('/volume1/workplan-certificate-automation/trade-native-staging/run-1/candidate','arrkjO','work.2884.kr')
        with self.assertRaises(ValueError):native_api.import_arguments('/volume1/workplan-certificate-automation/native-staging/run-1/candidate','arrkjO','trade.2884.kr')
    def test_observed_certificate_binding(self):
        self.assertEqual((transaction.DOMAIN,transaction.CID,transaction.SERVICE),('trade.2884.kr','arrkjO',('ReverseProxy','24c4ba80-609c-434a-ad0c-862c966d81e2')))
        self.assertEqual(str(native_deploy.STAGE),'/volume1/workplan-certificate-automation/trade-native-staging')
        self.assertEqual(str(native_deploy.SOURCE),'/volume1/workplan-certificate-automation/trade-http01/config-http01-trade.2884.kr/trade.2884.kr_ecc')
    def test_no_work_runtime_target_or_legacy_deployer(self):
        source=Path(__file__).with_name('renew-and-deploy.sh').read_text()
        for forbidden in ('/work-http01','SYNO_USE_TEMP_ADMIN','--deploy-hook','--dns dns_nsupdate'):
            self.assertNotIn(forbidden,source)
        self.assertIn('BASE_DIR="/volume1/workplan-certificate-automation/trade-http01"',source)

if __name__=='__main__':unittest.main()
