"""Unintegrated NAS deployment candidate. No command-line entry point.

All mutation is limited to a private transaction tree and the injected import
function. Integration must provide the verified native API and trusted TLS probe.
"""
from pathlib import Path
from contextlib import contextmanager
from datetime import datetime, timezone
import fcntl, hashlib, json, os, re, socket, ssl, stat, subprocess, time, uuid

FILES=('cert.pem','chain.pem','fullchain.pem','privkey.pem')
DOMAIN='trade.2884.kr'
CID='arrkjO'
SERVICE=('ReverseProxy','24c4ba80-609c-434a-ad0c-862c966d81e2')

class Refused(Exception): pass
def digest(value):return hashlib.sha256(value).hexdigest()
def utc():return datetime.now(timezone.utc).isoformat()
def unique(pairs):
    value={}
    for k,v in pairs:
        if k in value:raise Refused('duplicate_json_key')
        value[k]=v
    return value

def secure_directory(path, bound, uid):
    path,bound=Path(path),Path(bound)
    if not path.is_absolute() or path.resolve()!=path or bound.resolve()!=bound:
        raise Refused('noncanonical_directory')
    try:path.relative_to(bound)
    except ValueError:raise Refused('outside_bound')
    above_bound=False
    for item in [path]+list(path.parents):
        st=item.lstat()
        allowed_owners=(uid,0) if above_bound else (uid,)
        sticky_root_parent=above_bound and st.st_uid==0 and bool(st.st_mode&stat.S_ISVTX)
        if not stat.S_ISDIR(st.st_mode) or st.st_uid not in allowed_owners or (stat.S_IMODE(st.st_mode)&0o022 and not sticky_root_parent):
            raise Refused('unsafe_directory')
        if item==bound:above_bound=True

def read_file(path,bound,uid,private=False):
    secure_directory(path.parent,bound,uid)
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW)
    try:
        before=os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_uid!=uid or before.st_nlink!=1:
            raise Refused('unsafe_file')
        mode=stat.S_IMODE(before.st_mode)
        if mode&0o022 or (private and mode&0o077) or before.st_size>4*1024*1024:
            raise Refused('unsafe_file_permissions_or_size')
        chunks=[]
        while True:
            block=os.read(fd,65536)
            if not block:break
            chunks.append(block)
            if sum(map(len,chunks))>4*1024*1024:raise Refused('file_grew')
        after=os.fstat(fd)
        if (before.st_ino,before.st_size,before.st_mtime_ns,before.st_ctime_ns)!=(after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns):
            raise Refused('file_changed_while_reading')
        return b''.join(chunks)
    finally:os.close(fd)

def write_private(path,content):
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
    try:
        with os.fdopen(fd,'wb',closefd=False) as f:f.write(content);f.flush();os.fsync(fd)
    finally:os.close(fd)

def sync_directory(path):
    fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY)
    try:os.fsync(fd)
    finally:os.close(fd)

def journal(run,data):
    value=dict(data,updatedUtc=utc())
    temp=run/('journal-'+uuid.uuid4().hex+'.tmp')
    write_private(temp,json.dumps(value,sort_keys=True).encode())
    os.replace(temp,run/'journal.json')
    sync_directory(run)

class Validator:
    def __init__(self,ca_file=None):self.ca_file=ca_file
    def command(self,args):
        p=subprocess.run(['openssl']+args,stdin=subprocess.DEVNULL,capture_output=True,timeout=15)
        if p.returncode:raise Refused('certificate_validation_failed')
        return p.stdout
    def __call__(self,directory,minimum_seconds=0):
        d=Path(directory)
        self.command(['x509','-in',str(d/'cert.pem'),'-checkend',str(minimum_seconds),'-noout'])
        san=self.command(['x509','-in',str(d/'cert.pem'),'-noout','-ext','subjectAltName']).decode()
        if 'DNS:'+DOMAIN not in re.split(r'[\s,]+',san):raise Refused('exact_san_required')
        public=self.command(['x509','-in',str(d/'cert.pem'),'-pubkey','-noout'])
        if public!=self.command(['pkey','-in',str(d/'privkey.pem'),'-pubout']):raise Refused('key_mismatch')
        args=['verify','-purpose','sslserver','-verify_hostname',DOMAIN,'-untrusted',str(d/'chain.pem')]
        if self.ca_file:args+=['-CAfile',str(self.ca_file)]
        self.command(args+[str(d/'cert.pem')])
        def blocks(name):
            data=(d/name).read_bytes()
            found=re.findall(rb'-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+)-----END CERTIFICATE-----',data)
            if not found:raise Refused('certificate_chain_missing')
            return [re.sub(rb'\s+',b'',x) for x in found]
        leaf,chain,full=blocks('cert.pem'),blocks('chain.pem'),blocks('fullchain.pem')
        if len(leaf)!=1 or full!=leaf+chain:raise Refused('fullchain_mismatch')
        return {'leaf':digest(self.command(['x509','-in',str(d/'cert.pem'),'-outform','DER'])),
                'publicKey':digest(public),'chain':digest(b'\n'.join(chain))}

class TlsProbe:
    def __init__(self,port=443,ca_file=None):
        if not isinstance(port,int) or not 1<=port<=65535:raise Refused('invalid_tls_port')
        self.port=port
        self.context=ssl.create_default_context(cafile=str(ca_file) if ca_file else None)
    def __call__(self):
        try:
            with socket.create_connection(('127.0.0.1',self.port),timeout=10) as connection:
                with self.context.wrap_socket(connection,server_hostname=DOMAIN) as tls:
                    return digest(tls.getpeercert(binary_form=True))
        except (OSError,ssl.SSLError):raise Refused('public_tls_verification_failed') from None

class Transaction:
    def __init__(self,certificate_root,staging_root,validator,importer,served_fingerprint,uid=0,verify_attempts=6,verify_delay=2):
        self.cert=Path(certificate_root);self.stage=Path(staging_root)
        self.validate=validator;self.importer=importer;self.served=served_fingerprint;self.uid=uid
        if not 1<=verify_attempts<=12 or not 0<=verify_delay<=5:raise Refused('invalid_verification_budget')
        self.verify_attempts=verify_attempts;self.verify_delay=verify_delay
    @contextmanager
    def lock(self):
        secure_directory(self.stage,self.stage,self.uid)
        if stat.S_IMODE(self.stage.stat().st_mode)!=0o700:raise Refused('staging_not_private')
        fd=os.open(self.stage/'transaction.lock',os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
        try:
            st=os.fstat(fd)
            if not stat.S_ISREG(st.st_mode) or st.st_uid!=self.uid or st.st_nlink!=1 or stat.S_IMODE(st.st_mode)!=0o600:raise Refused('unsafe_lock')
            try:fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
            except BlockingIOError:raise Refused('locked')
            # An unresolved earlier request must be reconciled before another run.
            yield
        finally:os.close(fd)
    def snapshot(self):
        raw=read_file(self.cert/'_archive/INFO',self.cert,self.uid)
        info=json.loads(raw,object_pairs_hook=unique)
        if not isinstance(info,dict):raise Refused('invalid_inventory')
        matches=[k for k,v in info.items() if isinstance(v,dict) and v.get('desc')==DOMAIN]
        if matches!=[CID]:raise Refused('certificate_identity_changed')
        services=info[CID].get('services')
        if not isinstance(services,list) or [(v.get('subscriber'),v.get('service')) for v in services]!=[SERVICE]:raise Refused('service_mapping_changed')
        dirs={'archive':self.cert/'_archive'/CID,'service':self.cert/SERVICE[0]/SERVICE[1]}
        contents={name:{f:read_file(path/f,self.cert,self.uid,f=='privkey.pem') for f in FILES} for name,path in dirs.items()}
        hashes={name:{f:digest(data) for f,data in files.items()} for name,files in contents.items()}
        # Opaque observation is used as a concurrency guard, not exposed file hashes.
        observation=digest(json.dumps({'info':info,'files':hashes},sort_keys=True).encode())
        return {'info':info,'rawInfo':raw,'directories':dirs,'contents':contents,'hashes':hashes,'observation':observation}
    def write_set(self,path,contents):
        path.mkdir(mode=0o700)
        for name,data in contents.items():write_private(path/name,data)
        sync_directory(path)
        return path
    def pending(self,except_run=None):
        for path in self.stage.glob('run-*/journal.json'):
            if except_run and path.parent==except_run:continue
            data=json.loads(read_file(path,self.stage,self.uid,True),object_pairs_hook=unique)
            if data['state'] not in ('deployed','rolled-back','precondition-refused'):
                raise Refused('unresolved_transaction')
    def verify_target(self,baseline,expected):
        after=self.snapshot()
        # Preserve default assignment, every other certificate, and mapping metadata.
        if after['info']!=baseline['info']:raise Refused('inventory_changed')
        for path in after['directories'].values():
            if self.validate(path)!=expected:raise Refused('installed_certificate_mismatch')
        if self.served()!=expected['leaf']:raise Refused('served_certificate_mismatch')
        return after
    def wait_for_target(self,baseline,expected):
        for attempt in range(self.verify_attempts):
            try:return self.verify_target(baseline,expected)
            except Refused as e:
                if str(e) not in ('installed_certificate_mismatch','served_certificate_mismatch','public_tls_verification_failed') or attempt+1==self.verify_attempts:raise
                # Read-only reconciliation polling; never repeat the import here.
                time.sleep(self.verify_delay)
    def preflight(self):
        with self.lock():
            self.pending()
            baseline=self.snapshot()
            expected=self.validate(baseline['directories']['archive'])
            self.verify_target(baseline,expected)
            return {'state':'ready','fingerprint':expected['leaf']}
    def apply(self,source,source_format='pem'):
        formats={'pem':dict(zip(FILES,FILES)), 'acme-ecc':{
            'cert.pem':DOMAIN+'.cer','chain.pem':'ca.cer',
            'fullchain.pem':'fullchain.cer','privkey.pem':DOMAIN+'.key'}}
        if source_format not in formats:raise Refused('unsupported_source_format')
        names=formats[source_format]
        with self.lock():
            self.pending()
            baseline=self.snapshot()
            original=self.validate(baseline['directories']['archive'])
            self.verify_target(baseline,original)
            source=Path(source)
            candidate={f:read_file(source/names[f],source,self.uid,f=='privkey.pem') for f in FILES}
            run=self.stage/('run-'+uuid.uuid4().hex);run.mkdir(mode=0o700);sync_directory(self.stage)
            meta={'state':'preparing','id':run.name,'createdUtc':utc(),'certificateId':CID,'description':DOMAIN,'baselineObservation':baseline['observation'],'original':original,'baselineInfo':baseline['info'],'backupHashes':baseline['hashes']}
            journal(run,meta)
            try:
                staged=self.write_set(run/'candidate',candidate)
                expected=self.validate(staged,2592000);meta['candidate']=expected
                for name,files in baseline['contents'].items():self.write_set(run/('backup-'+name),files)
                write_private(run/'INFO.backup',baseline['rawInfo'])
                for name,files in baseline['hashes'].items():
                    for f,value in files.items():
                        if digest(read_file(run/('backup-'+name)/f,self.stage,self.uid,f=='privkey.pem'))!=value:raise Refused('backup_hash_mismatch')
                # No source or live certificate changes are allowed during preparation.
                if candidate!={f:read_file(source/names[f],source,self.uid,f=='privkey.pem') for f in FILES}:raise Refused('candidate_changed')
                if self.snapshot()['observation']!=baseline['observation']:raise Refused('live_changed_before_import')
            except Exception:
                meta['state']='precondition-refused';journal(run,meta);raise
            meta['state']='importing';journal(run,meta)
            try:
                result=self.importer(staged,CID,DOMAIN)
                meta['apiState']=result.state;meta['apiCode']=result.code
                if not result.api_success:raise Refused('api_not_acknowledged')
                meta['importAcknowledged']=True;meta['state']='verifying';journal(run,meta)
                self.wait_for_target(baseline,expected)
            except BaseException as e:
                meta['state']='recovery-required';meta['failureType']=type(e).__name__;journal(run,meta)
                return {'state':meta['state'],'run':run.name,'code':meta.get('apiCode','interrupted')}
            meta['state']='deployed';journal(run,meta)
            return {'state':'deployed','run':run.name,'fingerprint':expected['leaf']}
    def rollback(self,run_name,expected_observation,confirmed_no_inflight=False):
        if not confirmed_no_inflight:raise Refused('completion_reconciliation_required')
        if not re.fullmatch(r'run-[0-9a-f]{32}',run_name):raise Refused('invalid_run')
        run=self.stage/run_name
        with self.lock():
            self.pending(except_run=run)
            meta=json.loads(read_file(run/'journal.json',self.stage,self.uid,True),object_pairs_hook=unique)
            # SIGTERM can leave the durable journal in rolling-back. Recovery is
            # still explicit: the caller must confirm the old request ended,
            # and the observation, mapping and backup checks below must pass.
            if meta['state'] not in ('importing','verifying','deployed','recovery-required','rolling-back'):raise Refused('rollback_state_refused')
            current=self.snapshot()
            if current['observation']!=expected_observation:raise Refused('live_changed_before_rollback')
            if current['info']!=meta['baselineInfo']:raise Refused('rollback_mapping_conflict')
            for name,files in meta['backupHashes'].items():
                for f,value in files.items():
                    if digest(read_file(run/('backup-'+name)/f,self.stage,self.uid,f=='privkey.pem'))!=value:raise Refused('backup_changed')
            if self.validate(run/'backup-archive')!=meta['original']:raise Refused('backup_invalid')
            if self.snapshot()['observation']!=expected_observation:raise Refused('live_changed_before_rollback')
            meta['state']='rolling-back';journal(run,meta)
            try:
                result=self.importer(run/'backup-archive',CID,DOMAIN)
                if not result.api_success:raise Refused('rollback_not_acknowledged')
                baseline={'info':meta['baselineInfo']}
                self.wait_for_target(baseline,meta['original'])
            except BaseException as e:
                meta['state']='recovery-required';meta['failureType']=type(e).__name__;journal(run,meta)
                return {'state':'recovery-required','run':run.name,'code':'rollback_failed'}
            meta['state']='rolled-back';journal(run,meta)
            return {'state':'rolled-back','run':run.name,'fingerprint':meta['original']['leaf']}
