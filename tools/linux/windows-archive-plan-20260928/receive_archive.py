"""SSD-only receiver for the pinned nine-root archive plan. No source deletion."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tarfile

from verify_archive import verify, hash_file

PLAN_SHA = '96a64537048478645bcb867816ef514a7ee6e602854472912e41e054d19ce328'
ROOT = Path('/mnt/ssd2/archives/tradeplan/windows-20260928')


def regular(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise ValueError('Expected a single-link regular file')


def load_plan():
    for path in (ROOT, *ROOT.parents):
        if path.is_symlink():
            raise ValueError('Symlink directory refused')
    if Path(__file__).resolve().parent != ROOT:
        raise ValueError('Receiver location differs')
    mount = json.loads(subprocess.check_output(['findmnt', '--json', '--target', '/mnt/ssd2',
                                                '--output', 'TARGET,SOURCE,FSTYPE'], text=True))['filesystems']
    if len(mount) != 1 or mount[0] != {'target': '/mnt/ssd2', 'source': '/dev/sdb1', 'fstype': 'ext4'}:
        raise ValueError('Backup SSD mount identity changed')
    if ROOT.stat().st_dev != Path('/mnt/ssd2').stat().st_dev:
        raise ValueError('Archive is not on SSD')
    path = ROOT / 'plan.json'
    regular(path)
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != PLAN_SHA:
        raise ValueError('Pinned plan differs')
    plan = json.loads(raw)
    if plan['archiveDestination'] != str(ROOT):
        raise ValueError('Archive destination differs')
    return plan


def capacity(root, plan, additional=0):
    total = 0
    for path in root.glob('root-*.tar.gz'):
        regular(path)
        total += path.stat().st_size
    if total + additional > plan['remoteArchiveCapBytes']:
        raise ValueError('Archive cap exceeded')
    if shutil.disk_usage(root).free - additional < plan['minimumSsdFreeAfterBytes']:
        raise ValueError('SSD reserve would be exceeded')


def receive(root, plan, index, source):
    if index not in {r['index'] for r in plan['roots']}:
        raise ValueError('Unknown root')
    path = root / f'root-{index}.tar.gz'
    if path.exists() or path.is_symlink():
        raise ValueError('Archive attempt already exists; preserve it for inspection')
    capacity(root, plan)
    # O_EXCL and no cleanup on failure: never replace a previous attempt.
    with path.open('xb', buffering=0) as out:
        os.chmod(path, 0o600)
        while chunk := source.read(1024 * 1024):
            capacity(root, plan, len(chunk))
            out.write(chunk)
        out.flush()
        os.fsync(out.fileno())
    capacity(root, plan)
    return {'received': True, 'rootIndex': index, 'archiveBytes': path.stat().st_size}


def extract_sample(root, plan, index):
    candidates = [r for r in plan['files'] if r['rootIndex'] == index and 0 < r['bytes'] <= 1024 * 1024]
    if not candidates:
        raise ValueError('No bounded sample')
    row = min(candidates, key=lambda r: (r['bytes'], r['relative']))
    sample = root / 'samples' / f'root-{index}.sample'
    if sample.parent.is_symlink() or sample.is_symlink():
        raise ValueError('Linked sample path refused')
    sample.parent.mkdir(mode=0o700, exist_ok=True)
    if sample.exists():
        regular(sample)
        if sample.stat().st_size != row['bytes'] or hash_file(sample) != row['sha256']:
            raise ValueError('Existing extracted sample differs')
    else:
        with tarfile.open(root / f'root-{index}.tar.gz', 'r:gz') as bundle:
            member = bundle.getmember(row['relative'])
            if not member.isfile() or member.size != row['bytes']:
                raise ValueError('Invalid sample member')
            with bundle.extractfile(member) as source, sample.open('xb') as out:
                os.chmod(sample, 0o600)
                shutil.copyfileobj(source, out)
                out.flush()
                os.fsync(out.fileno())
        if sample.stat().st_size != row['bytes'] or hash_file(sample) != row['sha256']:
            raise ValueError('Extracted sample differs')
    return {'relative': row['relative'], 'bytes': row['bytes'], 'sha256': row['sha256']}


def verify_all(root, plan):
    expected = {f"root-{r['index']}.tar.gz" for r in plan['roots']}
    if {p.name for p in root.glob('root-*.tar.gz')} != expected:
        raise ValueError('Archive set is incomplete or contains extras')
    capacity(root, plan)
    reports = []
    for row in plan['roots']:
        path = root / f"root-{row['index']}.tar.gz"
        regular(path)
        report = verify(path, plan, row['index'])
        report['sample'] = extract_sample(root, plan, row['index'])
        reports.append(report)
    capacity(root, plan)
    return {'verified': True, 'planSha256': PLAN_SHA, 'archives': reports,
            'fileCount': sum(r['files'] for r in reports), 'ssdFreeBytes': shutil.disk_usage(root).free}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['receive', 'verify-all'])
    parser.add_argument('--index', type=int)
    args = parser.parse_args()
    plan = load_plan()
    fd = os.open(ROOT / '.writer.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        if not stat.S_ISREG(os.fstat(fd).st_mode) or os.fstat(fd).st_nlink != 1:
            raise ValueError('Invalid writer lock')
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        result = receive(ROOT, plan, args.index, sys.stdin.buffer) if args.mode == 'receive' else verify_all(ROOT, plan)
        print(json.dumps(result))
    finally:
        os.close(fd)


if __name__ == '__main__':
    main()
