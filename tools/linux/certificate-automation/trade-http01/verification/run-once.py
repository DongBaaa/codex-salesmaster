"""Bounded Trade certificate verification, with separate non-reusable run guards."""
from pathlib import Path
from datetime import datetime,timezone
import hashlib,http.client,json,os,re,signal,socket,ssl,stat,subprocess,tempfile
BASE=Path('/volume1/workplan-certificate-automation/trade-http01')
WEB='/volume1/web/tradeplan-acme-http01-4drjksx1'
DOMAIN='trade.2884.kr'
CONFIG=Path('/usr/local/etc/nginx/sites-enabled/tradeplan-http01.conf')
CONFIG_HASH='728d47b944b9a1116b7c0e29954d040f1873831e68b10148c9c379a7f34f9c2e'
OLD_CERT='65df3ec91d1861242f8b34acd3652dc67ea9e2ee68f5d3e24adf6f7c616a6591'
HASHES={'bin/renew-and-deploy.sh':'42d8dcbecf9bf8f8c47c67a801635c07a163a1d4e8cce8d9cd7a80ab6a681ac5','native/native_deploy.py':'92107f26756ab021532debdc3d63327670c647bc2b360969d1a1d827993f8051','native/native_api.py':'c69df9498b67a084a2f74ed6992715446e95c781b586698bc5818f51ef340b71','native/transaction.py':'bb44a250b80297a54493f4db4e1f4735a488826ace69233b6982988ed82010ce'}
PATH='/usr/syno/bin:/usr/syno/sbin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin'
ENV={'PATH':PATH,'LC_ALL':'C'}
class Refused(Exception):pass
def now():return datetime.now(timezone.utc).isoformat()
def read(path):
    path=Path(path)
    if path.resolve()!=path:raise Refused('noncanonical_file')
    for d in [path.parent]+list(path.parent.parents):
        s=d.lstat()
        if not stat.S_ISDIR(s.st_mode) or s.st_uid!=0 or s.st_mode&0o022:raise Refused('unsafe_parent')
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW)
    with os.fdopen(fd,'rb') as f:
        s=os.fstat(f.fileno())
        if not stat.S_ISREG(s.st_mode) or s.st_uid!=0 or s.st_nlink!=1 or s.st_mode&0o022 or s.st_size>1024*1024:raise Refused('unsafe_file')
        data=f.read(1024*1024+1)
        if len(data)>1024*1024:raise Refused('file_grew')
        return data
def digest(path):return hashlib.sha256(read(path)).hexdigest()
def guards():
    for relative,expected in HASHES.items():
        if digest(BASE/relative)!=expected:raise Refused('runtime_hash_changed')
    if digest(CONFIG)!=CONFIG_HASH:raise Refused('trade_http_changed')
    if digest('/usr/local/etc/nginx/sites-enabled/workdoctor-http-redirect.conf')!='8b0727e8b85e59fb35791029f3d1c6b49ca2305a7c11b1bb6f3f0b6dc94d20d1':raise Refused('work_http_changed')
    if digest('/volume1/workplan-certificate-automation/vendor/acme.sh-3.1.4/acme.sh')!='fcabf274d4f96966ec933879ae0257266e8ef2f7d16161f14b84dd896c0cac32':raise Refused('acme_vendor_changed')
    marker=json.loads(read(BASE/'state/install-verified.json'))
    if marker.get('state')!='installed-verified' or marker.get('webroot')!=WEB or marker.get('configHash')!=CONFIG_HASH:raise Refused('installation_marker_changed')
    if os.path.lexists(BASE/'state/renew.lock'):raise Refused('renewal_lock_present')
def snapshot():
    result={}
    for host,path,expected in [(DOMAIN,'/healthz',200),('work.2884.kr','/healthz',200),('rt.2884.kr','/',302),('itw.2884.kr','/',301)]:
        with socket.create_connection((host,443),timeout=15) as sock:
            with ssl.create_default_context().wrap_socket(sock,server_hostname=host) as tls:
                cert=tls.getpeercert();leaf=hashlib.sha256(tls.getpeercert(True)).hexdigest()
        c=http.client.HTTPSConnection(host,timeout=15)
        try:
            c.request('GET',path);r=c.getresponse();status=r.status;location=r.getheader('Location');r.read(4096)
        finally:c.close()
        if status!=expected:raise Refused('public_health_failed')
        result[host]={'sha256':leaf,'notAfter':cert['notAfter'],'status':status,'location':location}
    return result
def status():
    values={}
    for line in read(BASE/'state/last-status-trade.2884.kr.txt').decode().splitlines():
        k,_,v=line.partition('=')
        if k=='status' and v in ('staging_passed','deployed','not_due','failed'):values[k]=v
        if k=='timestamp_utc' and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ',v):values[k]=v
        if k=='detail':
            m=re.search(r'\bfingerprint=([0-9a-f]{64})\b',v)
            if m:values['fingerprint']=m[1]
    return values
def validate_after(mode,before,after,last):
    if set(before)!=set(after) or DOMAIN not in before:raise Refused('snapshot_domain_set_changed')
    if mode=='staging':
        if before!=after or last.get('status')!='staging_passed':raise Refused('staging_validation_failed')
    elif mode=='production':
        if any(before[h]!=after[h] for h in before if h!=DOMAIN):raise Refused('other_domain_changed')
        if last.get('status')!='deployed' or last.get('fingerprint')!=after[DOMAIN]['sha256'] or before[DOMAIN]['sha256']==after[DOMAIN]['sha256']:raise Refused('production_deploy_unproven')
    else:raise Refused('unsupported_mode')
def command(mode):
    if mode=='staging':return ['/bin/sh',str(BASE/'bin/renew-and-deploy.sh'),'--staging-test'],dict(ENV,WORKPLAN_ACME_HTTP_WEBROOT=WEB)
    if mode=='production':return ['/bin/sh',str(BASE/'bin/renew-and-deploy.sh'),'--scheduled'],dict(ENV,WORKPLAN_ACME_HTTP_WEBROOT=WEB,WORKPLAN_ACME_RENEW_BEFORE_DAYS='90')
    raise Refused('unsupported_mode')
def execute(args,env,log):
    with log.open('xb') as f:
        p=subprocess.Popen(args,stdin=subprocess.DEVNULL,stdout=f,stderr=f,env=env,start_new_session=True)
        try:return p.wait(timeout=390)
        except subprocess.TimeoutExpired:
            os.killpg(p.pid,signal.SIGTERM)
            try:p.wait(timeout=15)
            except subprocess.TimeoutExpired:os.killpg(p.pid,signal.SIGKILL);p.wait(timeout=15)
            raise Refused('verification_timed_out_no_retry')
def write_private(path,value):
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
    with os.fdopen(fd,'w') as f:json.dump(value,f,sort_keys=True);f.flush();os.fsync(f.fileno())
def run(mode):
    args,env=command(mode);result={'mode':mode,'startedAt':now(),'state':'refused','productionCertificateIssueRequested':mode=='production','stagingCertificateIssueRequested':mode=='staging','commandAttempted':False};once=None
    try:
        guards()
        if mode=='production':
            prior=json.loads(read(BASE/'state/verification-staging-20260911.once/result.json'))
            if prior.get('state')!='verified' or prior.get('mode')!='staging' or prior.get('configHash')!=CONFIG_HASH:raise Refused('staging_not_verified')
        before=snapshot()
        if before[DOMAIN]['sha256']!=OLD_CERT:raise Refused('trade_certificate_baseline_changed')
        check=subprocess.run(['python3','-I','-B',str(BASE/'native/native_deploy.py'),'--preflight'],env=ENV,stdin=subprocess.DEVNULL,capture_output=True,timeout=45)
        if check.returncode or json.loads(check.stdout).get('state')!='ready':raise Refused('native_preflight_refused')
        path=BASE/('state/verification-'+mode+'-20260911.once')
        path.mkdir(mode=0o700);once=path
        result['attemptGuard']=str(once);result['before']=before
        write_private(once/'started.json',result)
        result['processStartedAt']=now()
        result['commandAttempted']=True
        result['exitCode']=execute(args,env,once/'private-process.log')
        result['processEndedAt']=now()
        result['lastStatus']=status();result['after']=snapshot();guards()
        if result['lastStatus'].get('timestamp_utc','') < result['processStartedAt'][:19]+'Z':raise Refused('status_not_from_this_execution')
        if result['exitCode']!=0:raise Refused('certificate_command_failed')
        validate_after(mode,before,result['after'],result['lastStatus'])
        result.update(state='verified',configHash=CONFIG_HASH,runtimeHashesVerified=True,otherDomainsPreserved=True)
    except Exception as exc:
        result.update(state='failed',errorType=type(exc).__name__)
        if isinstance(exc,Refused):result['reason']=str(exc)
    finally:
        result['endedAt']=now()
        if once:write_private(once/'result.json',result)
        import pwd
        fd,name=tempfile.mkstemp(prefix='trade-cert-'+mode+'-',suffix='.json',dir='/volume1/homes/boss')
        with os.fdopen(fd,'w') as f:json.dump(result,f,sort_keys=True);f.flush();os.fsync(f.fileno());os.fchown(f.fileno(),pwd.getpwnam('boss').pw_uid,-1)
        print('report='+name)
    return 0 if result['state']=='verified' else 2
if __name__=='__main__':
    import sys
    if os.geteuid()!=0 or sys.argv[1:] not in (['--staging'],['--production']):raise SystemExit('root_and_explicit_mode_required')
    os.umask(0o077)
    raise SystemExit(run(sys.argv[1][2:]))
