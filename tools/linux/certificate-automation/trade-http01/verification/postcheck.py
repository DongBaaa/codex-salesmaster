from pathlib import Path
from datetime import datetime,timezone
import os,stat,json,tempfile,pwd,hashlib,re
BASE=Path('/volume1/workplan-certificate-automation/trade-http01')
STAGE=Path('/volume1/workplan-certificate-automation/trade-native-staging')
def info(p):
    s=p.lstat();return {'uid':s.st_uid,'mode':oct(stat.S_IMODE(s.st_mode)),'symlink':stat.S_ISLNK(s.st_mode),'regular':stat.S_ISREG(s.st_mode),'links':s.st_nlink}
def read(p):
    if p.resolve()!=p:raise ValueError('noncanonical_path')
    fd=os.open(p,os.O_RDONLY|os.O_NOFOLLOW)
    with os.fdopen(fd,'rb') as f:
        s=os.fstat(f.fileno())
        if not stat.S_ISREG(s.st_mode) or s.st_uid!=0 or s.st_mode&0o022 or s.st_size>1024*1024:raise ValueError('unsafe_file')
        data=f.read(1024*1024+1)
        if len(data)>1024*1024:raise ValueError('oversized_file')
        return data
def digest(p):return hashlib.sha256(read(p)).hexdigest()
if os.getuid()!=0:raise SystemExit('root_required')
result={'at':datetime.now(timezone.utc).isoformat(),'readOnly':True,'privateKeyContentsRead':False,'runtimeHashes':{n:digest(BASE/n) for n in ['bin/renew-and-deploy.sh','bin/run-scheduled.sh','native/native_deploy.py','native/native_api.py','native/transaction.py']},'runtime':info(BASE),'renewLockExists':os.path.lexists(BASE/'state/renew.lock'),'installMarker':json.loads(read(BASE/'state/install-verified.json')),'httpHash':digest(Path('/usr/local/etc/nginx/sites-enabled/tradeplan-http01.conf')),'workHttpHash':digest(Path('/usr/local/etc/nginx/sites-enabled/workdoctor-http-redirect.conf'))}
web=Path('/volume1/web/tradeplan-acme-http01-4drjksx1')
result['webDirectories']={n:info(web/n) for n in ['.','.well-known','.well-known/acme-challenge']}
last={}
for line in read(BASE/'state/last-status-trade.2884.kr.txt').decode().splitlines():
    k,_,v=line.partition('=')
    if k=='status' and v in ('not_due','deployed','failed','staging_passed'):last[k]=v
    if k=='timestamp_utc' and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ',v):last[k]=v
result['lastStatus']=last
result['journals']=[]
for path in sorted(STAGE.glob('run-*/journal.json')):
    j=json.loads(read(path))
    row={'run':path.parent.name,'state':j.get('state'),'apiState':j.get('apiState'),'importAcknowledged':j.get('importAcknowledged'),'certificateId':j.get('certificateId'),'description':j.get('description'),'backups':{}}
    for folder in ['backup-archive','backup-service']:
        row['backups'][folder]={n:info(path.parent/folder/n) for n in ['cert.pem','chain.pem','fullchain.pem','privkey.pem']}
    result['journals'].append(row)
fd,name=tempfile.mkstemp(prefix='trade-cert-postcheck-',suffix='.json',dir='/volume1/homes/boss')
with os.fdopen(fd,'w') as f:
    json.dump(result,f,sort_keys=True);f.flush();os.fsync(f.fileno());os.fchown(f.fileno(),pwd.getpwnam('boss').pw_uid,-1)
print('report='+name)
