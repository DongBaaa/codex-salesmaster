from pathlib import Path
from datetime import datetime
import subprocess,json,hashlib
root=Path(__file__).parent
names=['transaction.py','native_api.py','native_deploy.py','renew-and-deploy.sh','test_transaction.py','test_native_api.py','test_integration.py','test_shell.py','test_coordinator.py','test_isolation.py','install_support.py','install_trade.py','payload-hashes.json','test_install_trade.py','dsm-command.sh']
files={n:(root/n).read_text(encoding='utf-8') for n in names}
remote='''from pathlib import Path
import tempfile,subprocess,json,os
files=FILES
with tempfile.TemporaryDirectory(prefix='trade-http01-runtime-') as d:
 p=Path(d)
 for name,text in files.items():(p/name).write_text(text)
 results=[]
 for command in [['sh','-n','renew-and-deploy.sh'],['sh','-n','dsm-command.sh'],['python3','-B','-m','unittest','test_transaction','test_native_api','test_integration','test_shell','test_coordinator','test_isolation','test_install_trade','-v']]:
  r=subprocess.run(command,cwd=p,capture_output=True,text=True,timeout=240)
  results.append({'command':command,'exitCode':r.returncode,'stdout':r.stdout,'stderr':r.stderr})
 print(json.dumps({'checks':results,'actualTestUid':os.geteuid(),'rootIdentityMocked':True,'apiNamespaceMocked':True,'realNasApiInvocations':0,'realOpenSsl':True,'productionChanged':False}))
 raise SystemExit(any(r['exitCode'] for r in results))
'''.replace('FILES',repr(files))
ssh=['C:/Windows/System32/OpenSSH/ssh.exe','-T','-p','2222','-i','C:/Users/beene/.ssh/itwserver_codex_ed25519','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','itw@192.168.0.199','python3 -']
p=subprocess.run(ssh,input=remote,encoding='utf-8',capture_output=True,timeout=280)
if not p.stdout:raise RuntimeError('runner_no_json')
result=json.loads(p.stdout);result.update(at=datetime.now().astimezone().isoformat(),files={n:hashlib.sha256((root/n).read_bytes()).hexdigest() for n in names},status='isolated-tests-passed' if p.returncode==0 else 'isolated-tests-failed',nasUploaded=False,automaticRenewalComplete=False)
(root/'verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'status':result['status'],'checks':[{'exitCode':r['exitCode'],'tail':r['stderr'][-2200:]} for r in result['checks']]}));raise SystemExit(p.returncode)
