"""Prepared Work-only installer. CLI paths are fixed; not deployed by importing.

Does not issue/import certificates, modify DSM tasks, or send messages.
Leaves the new runtime disabled until HTTP and check-only checks succeed.
"""
from pathlib import Path
from dataclasses import dataclass
import hashlib,json,os,re,shlex,socket,ssl,stat,subprocess,tempfile,uuid,http.client,time

PAYLOADS = {'renew-and-deploy.sh': 'c6b8726f1bd7107ff04c0a1fb0c2fe1af99134e268d662e0d96e8b56119ef90b', 'native_deploy.py': '36d51e5f7b167e72c39190a056496254fffcb7a7981450a01fe2c5db6bc56070', 'native_api.py': '7898e58e3d3952543333b0dbcca5402df722472c68b8a10e47b90e3e81307349', 'transaction.py': 'efa0f31adcfced297b2672eeb287233696d6b53e46b95546bcbdf6aec4bb8f29'} # Filled with reviewed payload hashes by build-package.py.
LEGACY_HASH='ad01338456d175b447ffd642c1117f1a4f91eb0ad2faadc572dda59f2bab1c1b'
ACME_HASH='fcabf274d4f96966ec933879ae0257266e8ef2f7d16161f14b84dd896c0cac32'
DOMAIN='trade.2884.kr'
PATH='/usr/syno/bin:/usr/syno/sbin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin'
ENV={'PATH':PATH,'LC_ALL':'C'}
class Refused(Exception):pass
def sha(data):return hashlib.sha256(data).hexdigest()

@dataclass(frozen=True)
class Paths:
    config:Path=Path('/usr/local/etc/nginx/sites-enabled/tradeplan-http01.conf')
    base:Path=Path('/volume1/workplan-certificate-automation')
    web:Path=Path('/var/services/web')
    @property
    def runtime(self):return self.base/'trade-http01'
    @property
    def legacy(self):return self.base/'bin/renew-and-deploy.sh'
    @property
    def vendor(self):return self.base/'vendor/acme.sh-3.1.4/acme.sh'

def secure_dir(path,uid):
    if not path.is_absolute() or path.resolve()!=path:raise Refused('noncanonical_directory')
    for p in [path]+list(path.parents):
        s=p.lstat()
        sticky=s.st_uid==0 and bool(s.st_mode&stat.S_ISVTX)
        if not stat.S_ISDIR(s.st_mode) or s.st_uid not in (0,uid) or (s.st_mode&0o022 and not sticky):raise Refused('unsafe_directory')

def read(path,uid):
    secure_dir(path.parent,uid)
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW)
    try:
        s=os.fstat(fd)
        if not stat.S_ISREG(s.st_mode) or s.st_uid!=uid or s.st_nlink!=1 or s.st_mode&0o022 or s.st_size>2*1024*1024:raise Refused('unsafe_file')
        with os.fdopen(fd,'rb',closefd=False) as f:data=f.read(2*1024*1024+1)
        after=os.fstat(fd)
        if len(data)>2*1024*1024 or (s.st_size,s.st_mtime_ns,s.st_ctime_ns)!=(after.st_size,after.st_mtime_ns,after.st_ctime_ns):raise Refused('file_changed')
        return data
    finally:os.close(fd)

def create(path,data,mode=0o600):
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,mode)
    try:
        os.fchmod(fd,mode)
        with os.fdopen(fd,'wb',closefd=False) as f:f.write(data);f.flush();os.fsync(fd)
    finally:os.close(fd)

def write_report(result,directory,owner):
    fd,name=tempfile.mkstemp(prefix='tradeplan-http01-install-',suffix='.json',dir=directory)
    try:
        with os.fdopen(fd,'wb',closefd=False) as f:
            f.write(json.dumps(result,sort_keys=True).encode());f.flush();os.fsync(fd)
        os.fchown(fd,owner,-1)
    finally:os.close(fd)
    return name

def replace_config(path,data,mode):
    fd,name=tempfile.mkstemp(prefix='.tradeplan-http01-',dir=path.parent.parent)
    try:
        os.fchown(fd,mode[1],mode[2]);os.fchmod(fd,mode[0])
        with os.fdopen(fd,'wb',closefd=False) as f:f.write(data);f.flush();os.fsync(fd)
        os.replace(name,path)
        directory=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY)
        try:os.fsync(directory)
        finally:os.close(directory)
    finally:
        os.close(fd)
        if os.path.lexists(name):os.unlink(name)

class Effects:
    def command(self,args):
        result=subprocess.run(args,stdin=subprocess.DEVNULL,env=ENV,capture_output=True,timeout=180)
        if result.returncode:raise Refused('command_failed')
        return result.stdout
    def native_preflight(self,runtime):
        value=json.loads(self.command(['python3','-I','-B',str(runtime/'native/native_deploy.py'),'--preflight']))
        if value.get('state')!='ready':raise Refused('native_preflight_failed')
    def check_only(self,runtime,webroot):
        self.command(['env','-i','PATH='+PATH,'LC_ALL=C','WORKPLAN_ACME_HTTP_WEBROOT='+str(webroot),'/bin/sh',str(runtime/'bin/renew-and-deploy.sh'),'--check-only'])
        status=(runtime/'state/last-status-trade.2884.kr.txt').read_text()
        if '\nstatus=not_due\n' not in status:raise Refused('check_only_not_not_due')
    def syntax(self):self.command(['nginx','-t'])
    def reload(self):self.command(['nginx','-s','reload'])
    def http(self,host,path,secure=False,local=False):
        conn=http.client.HTTPSConnection(host,timeout=15,context=ssl.create_default_context()) if secure else http.client.HTTPConnection('127.0.0.1' if local else host,timeout=15)
        try:
            conn.request('GET',path,headers={'Host':host})
            r=conn.getresponse();body=r.read(1024*1024+1)
            if len(body)>1024*1024:raise Refused('response_too_large')
            return r.status,r.getheader('Location'),body
        finally:conn.close()
    def snapshot(self):
        result={}
        for host,path,expected in [(DOMAIN,'/healthz',200),('work.2884.kr','/healthz',200),('rt.2884.kr','/',302),('itw.2884.kr','/',301)]:
            status,location,_=self.http(host,path,True)
            if status!=expected:raise Refused('public_health_failed')
            with socket.create_connection((host,443),timeout=15) as s:
                with ssl.create_default_context().wrap_socket(s,server_hostname=host) as tls:fingerprint=sha(tls.getpeercert(binary_form=True))
            result[host]=[status,location,fingerprint]
        return result
    def redirects(self):
        for path in ['/','/healthz','/healthz?probe=http01','/account/signin','/search?Keyword=probe&Status=open']:
            status,location,_=self.http(DOMAIN,path,local=True)
            if (status,location)!=(308,'https://'+DOMAIN+path):raise Refused('redirect_changed')
    def challenge(self,token,body):
        path='/.well-known/acme-challenge/'+token
        for local in (True,False):
            for attempt in range(10):
                status,location,value=self.http(DOMAIN,path,local=local)
                if status==200 and value==body:break
                if attempt==9:raise Refused('challenge_route_failed')
                time.sleep(1)
