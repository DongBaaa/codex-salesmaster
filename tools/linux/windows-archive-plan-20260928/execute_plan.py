"""Exact pinned plan only. Default preflight is local read-only.

Upload/delete modes require separate explicit user approval of the plan; an
argument is an execution guard, never evidence that approval was received.
"""
import argparse
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
import hashlib
import json
import msvcrt
import os
from pathlib import Path, PureWindowsPath
import shlex
import stat
import subprocess
import tarfile

from verify_archive import safe_name

PLAN = Path(r'C:\Users\beene\Documents\Codex\disk-capacity-followup-20260928\reviewed-archive-cleanup-plan.json')
PLAN_SHA = '96a64537048478645bcb867816ef514a7ee6e602854472912e41e054d19ce328'
EVIDENCE = PLAN.parent / 'execution'
REMOTE = '/mnt/ssd2/archives/tradeplan/windows-20260928'
SSH = r'C:\Windows\System32\OpenSSH\ssh.exe'
SCP = r'C:\Windows\System32\OpenSSH\scp.exe'
SSH_ARGS = [SSH, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', 'georaeplan-linux']
PROTECTED = [
    Path(r'C:\Users\beene\AppData\Local\거래플랜\data\거래플랜.db'),
    Path(r'D:\거래플랜\.git\index'),
    Path(r'D:\거래플랜\Desktop\거래플랜.Desktop.App\bin\Release\net8.0-windows\거래플랜.Desktop.App.dll'),
    Path(r'D:\거래플랜\Server\거래플랜.Server.Api\bin\Release\net8.0\거래플랜.Server.Api.dll'),
]
KERNEL = ctypes.WinDLL('kernel32', use_last_error=True)
KERNEL.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                              ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
KERNEL.CreateFileW.restype = wintypes.HANDLE
KERNEL.CloseHandle.argtypes = [wintypes.HANDLE]
KERNEL.GetFinalPathNameByHandleW.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
KERNEL.SetFileInformationByHandle.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]


def reparse_guard(path):
    for part in (path, *path.parents):
        if part.exists() and part.lstat().st_file_attributes & 0x400:
            raise ValueError('Reparse path refused')


def validate_plan(plan):
    roots = {r['index']: Path(r['path']) for r in plan['roots']}
    if len(roots) != 9 or set(roots) != set(range(9)) or len(plan['files']) != 10122:
        raise ValueError('Plan population differs')
    seen = set()
    for row in plan['files']:
        root = roots[row['rootIndex']]
        relative = safe_name(row['relative'])
        expected = root.joinpath(*relative.split('/'))
        path = Path(row['path'])
        if path != expected or not path.is_absolute() or not path.is_relative_to(root):
            raise ValueError('Noncanonical plan path')
        if any(p.casefold() == '.git' for p in path.parts) or ':' in str(PureWindowsPath(relative)):
            raise ValueError('Protected boundary')
        if str(path).casefold() in seen:
            raise ValueError('Repeated plan path')
        seen.add(str(path).casefold())
        if type(row['deleteAfterVerifiedArchive']) is not bool or row['linkCount'] != 1:
            raise ValueError('Invalid file policy')
    if sum(r['deleteAfterVerifiedArchive'] for r in plan['files']) != 6612:
        raise ValueError('Deletion population differs')
    return roots


def load_plan():
    reparse_guard(PLAN)
    raw = PLAN.read_bytes()
    if hashlib.sha256(raw).hexdigest() != PLAN_SHA:
        raise ValueError('Approval plan changed')
    plan = json.loads(raw)
    validate_plan(plan)
    if plan['archiveDestination'] != REMOTE:
        raise ValueError('Destination changed')
    return plan


def create_handle(path, access, share, flags):
    handle = KERNEL.CreateFileW(str(path), access, share, None, 3, flags, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    return handle


@contextmanager
def locked_file(path, delete=False):
    """Pin parent directory names and exclusive file handle; refuse rename races."""
    reparse_guard(path)
    handles = []
    stream = None
    try:
        # Share read/write, never delete: the checked directory chain cannot move.
        for parent in reversed(path.parents):
            handle = create_handle(parent, 0, 3, 0x02000000 | 0x00200000)
            handles.append(handle)
            reparse_guard(parent)
        handle = create_handle(path, 0x80000000 | (0x00010000 if delete else 0), 0, 0x00200000)
        handles.append(handle)
        name = ctypes.create_unicode_buffer(32768)
        size = KERNEL.GetFinalPathNameByHandleW(handle, name, len(name), 0)
        if not size or size >= len(name) or Path(name.value.removeprefix('\\\\?\\')) != path:
            raise ValueError('Final file path changed')
        reparse_guard(path)
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
        handles.pop()  # The Python descriptor now owns the file handle.
        stream = os.fdopen(fd, 'rb')
        yield stream, handle
    finally:
        if stream is not None:
            stream.close()
        for handle in reversed(handles):
            KERNEL.CloseHandle(handle)


def check_file(stream, row):
    before = os.fstat(stream.fileno())
    if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_file_attributes & 0x400:
        raise ValueError('Nonregular or linked file')
    if (before.st_size, before.st_mtime_ns, str(before.st_ino)) != (row['bytes'], row['mtimeNs'], row['fileId']):
        raise ValueError('File identity/metadata changed: ' + row['relative'])
    stream.seek(0)
    if hashlib.file_digest(stream, 'sha256').hexdigest() != row['sha256']:
        raise ValueError('File content changed: ' + row['relative'])
    stream.seek(0)


def check_processes(plan):
    # No command text is returned or logged; inspect exact candidate references only.
    script = '$r=@(' + ','.join("'" + r['path'].replace("'", "''") + "'" for r in plan['roots']) + ');'
    script += '$p=@(Get-CimInstance Win32_Process | Where-Object {$c=$_.CommandLine; $c -and ($r | Where-Object {$c.IndexOf($_,[StringComparison]::OrdinalIgnoreCase) -ge 0}) -and $_.ProcessId -ne $PID}); if($p.Count){$p | Select-Object ProcessId,Name | ConvertTo-Json -Compress;exit 1}'
    subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', script], check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def check_inventory(plan, allow_deleted=frozenset()):
    roots = validate_plan(plan)
    expected = {i: set() for i in roots}
    for row in plan['files']:
        if row['path'] not in allow_deleted:
            expected[row['rootIndex']].add(row['path'].casefold())
    for i, root in roots.items():
        reparse_guard(root)
        actual = set()
        for base, dirs, files in os.walk(root, followlinks=False):
            for name in dirs + files:
                path = Path(base) / name
                reparse_guard(path)
                if name.casefold() == '.git':
                    raise ValueError('Embedded Git boundary')
            actual.update(str(Path(base) / name).casefold() for name in files)
        if actual != expected[i]:
            raise ValueError('Root file inventory changed: ' + str(i))


def record(name, value):
    reparse_guard(EVIDENCE)
    EVIDENCE.mkdir(exist_ok=True)
    with (EVIDENCE / name).open('x', encoding='utf-8') as output:
        json.dump(value, output, ensure_ascii=False, indent=2)
        output.flush()
        os.fsync(output.fileno())


def ssh(command, **kwargs):
    return subprocess.run([*SSH_ARGS, command], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs)


def remote_command(mode, index=None):
    parts = ['python3', REMOTE + '/receive_archive.py', mode]
    if index is not None:
        parts += ['--index', str(index)]
    return shlex.join(parts)


def bootstrap_command():
    # Fixed destination only. Validate the SSD before creating the missing parents.
    code = """import json, os, pathlib, subprocess
root=pathlib.Path('/mnt/ssd2/archives/tradeplan/windows-20260928')
mount=json.loads(subprocess.check_output(['findmnt','--json','--target','/mnt/ssd2','--output','TARGET,SOURCE,FSTYPE'],text=True))['filesystems']
if mount != [{'target':'/mnt/ssd2','source':'/dev/sdb1','fstype':'ext4'}]: raise RuntimeError('SSD mount differs')
if root.exists() or root.is_symlink(): raise RuntimeError('Preserve existing archive attempt')
for part in (root,*root.parents):
 if part.is_symlink(): raise RuntimeError('Linked archive path')
os.umask(0o077)
for part in (root.parent.parent,root.parent,root):
 if not part.exists(): part.mkdir(mode=0o700)
 if not part.is_dir() or part.is_symlink() or part.stat().st_dev != pathlib.Path('/mnt/ssd2').stat().st_dev: raise RuntimeError('Unexpected archive filesystem')
"""
    return shlex.join(['python3', '-c', code])


def preflight(plan):
    check_processes(plan)
    check_inventory(plan)
    for row in plan['files']:
        with locked_file(Path(row['path'])) as (stream, _):
            check_file(stream, row)
    return {'verified': True, 'planSha256': PLAN_SHA, 'files': len(plan['files']), 'uploads': 0, 'deletions': 0}


def protected_hashes():
    result = {}
    for path in PROTECTED:
        with locked_file(path) as (stream, _):
            result[str(path)] = hashlib.file_digest(stream, 'sha256').hexdigest()
    return result


def upload(plan):
    preflight(plan)
    record('protected-before.json', protected_hashes())
    ssh(bootstrap_command())
    upload_files = [PLAN, Path(__file__).parent / 'receive_archive.py', Path(__file__).parent / 'verify_archive.py']
    for path in upload_files:
        name = 'plan.json' if path == PLAN else path.name
        subprocess.run([SCP, '-P', '2222', '-i', str(Path.home() / '.ssh/itwserver_codex_ed25519'),
                        '-o', 'BatchMode=yes', str(path), 'itw@192.168.0.199:' + REMOTE + '/' + name], check=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    ssh('chmod 600 ' + ' '.join(shlex.quote(REMOTE + '/' + n) for n in ('plan.json', 'receive_archive.py', 'verify_archive.py')))
    reports = []
    for root in plan['roots']:
        index = root['index']
        check_processes(plan)
        proc = subprocess.Popen([*SSH_ARGS, remote_command('receive', index)], stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            with tarfile.open(fileobj=proc.stdin, mode='w|gz', compresslevel=1, format=tarfile.PAX_FORMAT) as bundle:
                for row in plan['files']:
                    if row['rootIndex'] != index:
                        continue
                    with locked_file(Path(row['path'])) as (stream, _):
                        check_file(stream, row)
                        info = tarfile.TarInfo(row['relative'])
                        info.size = row['bytes']
                        info.mode = 0o600
                        info.mtime = 0
                        bundle.addfile(info, stream)
            proc.stdin.close()
            proc.stdin = None
            stdout, stderr = proc.communicate(timeout=180)
            if proc.returncode:
                raise RuntimeError('Remote archive receiver failed; preserve local originals')
            report = json.loads(stdout)
            if report.get('received') is not True or report.get('rootIndex') != index:
                raise ValueError('Receiver report differs')
            record(f'received-{index}.json', report)
            reports.append(report)
        finally:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=30)
        print('archived root ' + str(index), flush=True)
    verified = verify_remote(plan)
    record('all-verified.json', verified)


def verify_remote(plan):
    result = json.loads(ssh(remote_command('verify-all'), timeout=1800).stdout)
    expected = {r['index']: r['files'] for r in plan['roots']}
    actual = {r['rootIndex']: r['files'] for r in result['archives'] if r['verified'] and r.get('sample')}
    if result.get('verified') is not True or result['planSha256'] != PLAN_SHA or actual != expected or result['fileCount'] != 10122:
        raise ValueError('Incomplete independent archive verification')
    return result


def delete(plan):
    stored = json.loads((EVIDENCE / 'all-verified.json').read_text(encoding='utf-8'))
    current = verify_remote(plan)  # Verify all archived bytes again immediately before any deletion.
    if stored['planSha256'] != PLAN_SHA or stored['archives'] != current['archives']:
        raise ValueError('Remote archived set changed')
    protected = json.loads((EVIDENCE / 'protected-before.json').read_text(encoding='utf-8'))
    if protected != protected_hashes():
        raise ValueError('Protected original or runtime changed before cleanup')
    check_processes(plan)
    check_inventory(plan)
    # No automatic resume after partial deletion. Preserve journal and archive for reviewed recovery.
    journal = EVIDENCE / 'deletion.jsonl'
    with journal.open('x', encoding='utf-8') as log:
        for row in plan['files']:
            if not row['deleteAfterVerifiedArchive']:
                continue
            path = Path(row['path'])
            with locked_file(path, delete=True) as (stream, handle):
                check_file(stream, row)
                log.write(json.dumps({'phase': 'intent', 'path': row['path'], 'sha256': row['sha256'], 'fileId': row['fileId']}) + '\n')
                log.flush()
                os.fsync(log.fileno())
                disposition = wintypes.BOOL(True)
                if not KERNEL.SetFileInformationByHandle(handle, 4, ctypes.byref(disposition), ctypes.sizeof(disposition)):
                    raise ctypes.WinError(ctypes.get_last_error())
            if path.exists():
                raise ValueError('Deleted handle still has original path')
            log.write(json.dumps({'phase': 'deleted', 'path': row['path']}) + '\n')
            log.flush()
            os.fsync(log.fileno())
    deleted = {r['path'] for r in plan['files'] if r['deleteAfterVerifiedArchive']}
    check_inventory(plan, deleted)
    for row in plan['files']:
        if not row['deleteAfterVerifiedArchive']:
            with locked_file(Path(row['path'])) as (stream, _):
                check_file(stream, row)
    if protected != protected_hashes():
        raise ValueError('Protected original or runtime changed during cleanup')
    record('completed.json', {'completed': True, 'planSha256': PLAN_SHA, 'deleted': len(deleted),
                              'keptLocal': len(plan['files']) - len(deleted), 'archive': REMOTE,
                              'protectedHashesUnchanged': True})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['preflight', 'upload', 'delete'], default='preflight', nargs='?')
    parser.add_argument('--approved-plan-sha256')
    args = parser.parse_args()
    plan = load_plan()
    if args.mode != 'preflight' and args.approved_plan_sha256 != PLAN_SHA:
        raise ValueError('Explicit approval of this exact plan is required')
    if args.mode == 'preflight':
        print(json.dumps(preflight(plan)))
    elif args.mode == 'upload':
        upload(plan)
    else:
        delete(plan)


if __name__ == '__main__':
    main()
