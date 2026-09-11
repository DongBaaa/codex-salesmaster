"""Trade-only new-file installer. No certificate issuance/import or task changes."""
from pathlib import Path
from dataclasses import dataclass
import json,os,stat,shlex,tempfile,uuid,sys
# python -I deliberately omits the script directory. Add only this verified,
# non-writable source directory before importing the separately pinned helper.
if __name__=='__main__':
    here=Path(__file__).absolute().parent;uid=os.geteuid()
    if here.resolve()!=here:raise PermissionError('noncanonical_installer_directory')
    for path in [here]+list(here.parents):
        st=path.lstat();sticky_parent=path!=here and st.st_uid==0 and bool(st.st_mode&stat.S_ISVTX)
        if not stat.S_ISDIR(st.st_mode) or st.st_uid not in (0,uid) or (st.st_mode&0o022 and not sticky_parent):raise PermissionError('unsafe_installer_directory')
    st=(here/'install_support.py').lstat()
    if not stat.S_ISREG(st.st_mode) or st.st_uid!=uid or st.st_nlink!=1 or st.st_mode&0o022:raise PermissionError('unsafe_installer_support')
    sys.path.insert(0,str(here))
from install_support import Effects,Refused,sha,secure_dir,read,create,write_report,LEGACY_HASH,ACME_HASH
WORK_HTTP_HASH='8b0727e8b85e59fb35791029f3d1c6b49ca2305a7c11b1bb6f3f0b6dc94d20d1'

@dataclass(frozen=True)
class Paths:
    config:Path=Path('/usr/local/etc/nginx/sites-enabled/tradeplan-http01.conf')
    base:Path=Path('/volume1/workplan-certificate-automation')
    web:Path=Path('/volume1/web')
    protected:Path=Path('/usr/local/etc/nginx/sites-enabled/workdoctor-http-redirect.conf')
    @property
    def runtime(self):return self.base/'trade-http01'
    @property
    def stage(self):return self.base/'trade-native-staging'
    @property
    def legacy(self):return self.base/'bin/renew-and-deploy.sh'
    @property
    def vendor(self):return self.base/'vendor/acme.sh-3.1.4/acme.sh'

class TradeEffects(Effects):
    def syntax(self):
        import subprocess
        from install_support import ENV
        r=subprocess.run(['nginx','-t'],stdin=subprocess.DEVNULL,env=ENV,capture_output=True,timeout=30)
        if r.returncode or b'conflicting server name "trade.2884.kr"' in r.stdout+r.stderr:raise Refused('nginx_syntax_or_trade_conflict')
    def http_before(self):
        return [self.http('trade.2884.kr',p,local=True)[:2] for p in ['/','/healthz','/.well-known/acme-challenge/TRADE_PREINSTALL_MISSING']]

def publish_new(path,data):
    # Hard-link publication refuses an existing file atomically, unlike rename.
    fd,name=tempfile.mkstemp(prefix='.trade-http01-',dir=path.parent.parent)
    try:
        os.fchmod(fd,0o600)
        with os.fdopen(fd,'wb',closefd=False) as f:f.write(data);f.flush();os.fsync(fd)
        os.link(name,path)
        return path.stat().st_ino
    finally:
        os.close(fd);os.unlink(name)

def candidate_config(web):
    return ('''server {
    listen 80;
    listen [::]:80;
    server_name trade.2884.kr;
    location ~ ^/\\.well-known/acme-challenge/[A-Za-z0-9_-]+$ {
        root WEBROOT;
        default_type text/plain;
        try_files $uri =404;
    }
    location / { return 308 https://trade.2884.kr$request_uri; }
}
'''.replace('WEBROOT',str(web))).encode()

class Installer:
    def __init__(self,p,payload,hashes,effects=None,uid=0,expected=None):
        self.p=p;self.payload=Path(payload);self.hashes=hashes;self.e=effects or TradeEffects();self.uid=uid
        self.expected=expected or {'protected':WORK_HTTP_HASH,'legacy':LEGACY_HASH,'vendor':ACME_HASH}
    def protected(self):
        for n,h in self.expected.items():
            if sha(read(getattr(self.p,n),self.uid))!=h:raise Refused('protected_baseline_changed')
    def install(self):
        p=self.p;r=p.runtime;e=self.e
        for directory in [p.base,p.web,p.config.parent,self.payload]:secure_dir(directory,self.uid)
        for path in [p.config,r,p.stage]:
            if os.path.lexists(path):raise Refused('existing_target_preserved')
        self.protected()
        if set(self.hashes)!={'renew-and-deploy.sh','native_deploy.py','native_api.py','transaction.py'}:raise Refused('payload_set_invalid')
        files={n:read(self.payload/n,self.uid) for n in self.hashes}
        if any(sha(v)!=self.hashes[n] for n,v in files.items()):raise Refused('payload_changed')
        if any(c not in '/ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-' for c in str(p.web)):raise Refused('invalid_web_path')
        baseline=e.snapshot();http_before=e.http_before();e.syntax()
        r.mkdir(mode=0o700);r.chmod(0o700)
        for d in ['bin','native','state','logs','tmp','config-http01-trade.2884.kr','install-backup']:
            (r/d).mkdir(mode=0o700);(r/d).chmod(0o700)
        p.stage.mkdir(mode=0o700);p.stage.chmod(0o700)
        for n,v in files.items():create(r/('bin' if n.endswith('.sh') else 'native')/n,v,0o700 if n.endswith('.sh') else 0o600)
        create(r/'install-backup/baseline.json',json.dumps({'http':http_before,'https':baseline,'configWasAbsent':True}).encode())
        published=False;reload_attempted=False;inode=None;probe=None;body=None
        result={'state':'preparing','certificateIssueAttempted':False,'schedulerChanged':False}
        try:
            e.native_preflight(r)
            web=Path(tempfile.mkdtemp(prefix='tradeplan-acme-http01-',dir=p.web));web.chmod(0o755)
            for d in [web/'.well-known',web/'.well-known/acme-challenge']:d.mkdir(mode=0o755);d.chmod(0o755)
            token='TRADE_INSTALL_'+uuid.uuid4().hex;body=token.encode();probe=web/'.well-known/acme-challenge'/token;create(probe,body,0o644)
            candidate=candidate_config(web)
            create(r/'install-backup/candidate.conf',candidate)
            create(r/'install-backup/webroot.txt',(str(web)+'\n').encode())
            wrapper='#!/bin/sh\nset -eu\ntest -f '+shlex.quote(str(r/'state/install-verified.json'))+' || exit 1\nexport WORKPLAN_ACME_HTTP_WEBROOT='+shlex.quote(str(web))+'\nexec /bin/sh '+shlex.quote(str(r/'bin/renew-and-deploy.sh'))+' --scheduled\n'
            create(r/'bin/run-scheduled.sh',wrapper.encode(),0o700)
            self.protected();inode=publish_new(p.config,candidate);published=True
            e.syntax();reload_attempted=True;e.reload()
            e.challenge(token,body);e.redirects()
            if e.snapshot()!=baseline:raise Refused('public_baseline_changed')
            e.check_only(r,web);self.protected()
            if read(p.config,self.uid)!=candidate or p.config.stat().st_ino!=inode:raise Refused('concurrent_config_change')
            if read(probe,self.uid)!=body:raise Refused('probe_changed')
            probe.unlink();probe=None
            result.update(state='installed-verified',webroot=str(web),configHash=sha(candidate),publicBaselinePreserved=True,notDueVerified=True)
            create(r/'state/install-verified.json',json.dumps(result,sort_keys=True).encode())
        except BaseException as exc:
            result.update(state='install-failed',errorType=type(exc).__name__)
            if isinstance(exc,Refused):result['reason']=str(exc)
            if published:
                try:
                    if read(p.config,self.uid)!=candidate or p.config.stat().st_ino!=inode:raise Refused('concurrent_config_change')
                    p.config.unlink();e.syntax()
                    if reload_attempted:e.reload()
                    if e.snapshot()!=baseline or e.http_before()!=http_before:raise Refused('restored_baseline_mismatch')
                    self.protected();result.update(state='install-failed-http-restored',originalConfigAbsenceRestored=True)
                except BaseException as err:result.update(state='manual-recovery-required',rollbackErrorType=type(err).__name__)
        finally:
            if probe and os.path.lexists(probe):
                if not probe.is_symlink() and read(probe,self.uid)==body:probe.unlink()
                else:result['probeCleanupRefused']=True
            create(r/'install-result.json',json.dumps(result,sort_keys=True).encode())
        return result

def main():
    import sys,pwd
    if os.geteuid()!=0 or sys.argv[1:]!=['--install']:raise Refused('root_explicit_install_required')
    p=Paths()
    if Path('/etc/nginx/sites-enabled').resolve()!=p.config.parent:raise Refused('nginx_include_directory_mismatch')
    payload=Path(__file__).resolve().parent
    hashes=json.loads(read(payload/'payload-hashes.json',0))
    result=Installer(p,payload,hashes).install()
    result['report']=write_report(result,'/volume1/homes/boss',pwd.getpwnam('boss').pw_uid)
    print(json.dumps(result,sort_keys=True))
    return 0 if result['state']=='installed-verified' else 2
if __name__=='__main__':
    try:raise SystemExit(main())
    except Exception as e:
        print(json.dumps({'state':'refused','errorType':type(e).__name__}));raise SystemExit(2)
