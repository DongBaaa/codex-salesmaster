"""Bounded continuation: preserve incomplete archive; retain original file plan."""
import argparse
import copy
import fcntl
import hashlib
import json
import os
from pathlib import Path
import sys

import receive_archive as original

CAP = 14 * 1024**3
RESERVE = 24 * 1024**3
PARTIAL = 'incomplete-root-8.tar.gz'


def amendment(root, expected_hash):
    path = root / 'continuation.json'
    original.regular(path)
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != expected_hash:
        raise ValueError('Continuation hash differs')
    data = json.loads(raw)
    if (data['originalPlanSha256'] != original.PLAN_SHA or
            data['totalArchiveCapBytes'] != CAP or data['minimumSsdFreeAfterBytes'] != RESERVE or
            data['rootIndex'] != 8 or len(data['completed']) != 8 or
            {r['rootIndex'] for r in data['completed']} != set(range(8))):
        raise ValueError('Continuation scope differs')
    return data


def adjusted_plan(plan, data):
    result = copy.deepcopy(plan)
    if plan['minimumSsdFreeAfterBytes'] != RESERVE:
        raise ValueError('Original reserve differs')
    # Count the preserved partial towards the total limit too.
    result['remoteArchiveCapBytes'] = CAP - data['partial']['bytes']
    return result


def check_partial(path, expected):
    original.regular(path)
    stat = path.stat()
    if stat.st_size != expected['bytes'] or stat.st_ino != expected['inode'] or original.hash_file(path) != expected['sha256']:
        raise ValueError('Incomplete archive identity differs')


def check_prefix(root, data):
    for report in data['completed']:
        p = root / f"root-{report['rootIndex']}.tar.gz"
        original.regular(p)
        if not report['verified'] or p.stat().st_size != report['archiveBytes'] or original.hash_file(p) != report['archiveSha256']:
            raise ValueError('Verified completed archive changed')


def sync_directory(root):
    directory_fd = os.open(root, os.O_DIRECTORY)
    try:
        os.fsync(directory_fd)
    finally:
        os.close(directory_fd)


def prepare(root, plan, data):
    names = {p.name for p in root.glob('*.tar.gz')}
    if names != {f'root-{i}.tar.gz' for i in range(9)}:
        raise ValueError('Unexpected existing archive set; no automatic resume')
    if (root / 'continuation-prepared.json').exists():
        raise ValueError('Continuation already prepared')
    check_prefix(root, data)
    check_partial(root / 'root-8.tar.gz', data['partial'])
    available = original.shutil.disk_usage(root).free
    total = sum(x['archiveBytes'] for x in data['completed']) + data['partial']['bytes'] + data['root8CompressedBytes']
    if total > CAP or available - data['root8CompressedBytes'] < RESERVE:
        raise ValueError('Exact continuation does not fit cap and reserve')
    os.rename(root / 'root-8.tar.gz', root / PARTIAL)
    with (root / 'continuation-prepared.json').open('x', encoding='utf-8') as out:
        json.dump({'prepared': True, 'partial': data['partial'], 'planSha256': original.PLAN_SHA}, out)
        out.flush()
        os.fsync(out.fileno())
    sync_directory(root)
    return {'prepared': True, 'preservedPartial': PARTIAL}


def run(mode, root, plan, data, digest):
    if mode == 'prepare':
        return prepare(root, plan, data)
    marker = json.loads((root / 'continuation-prepared.json').read_text())
    if marker != {'prepared': True, 'partial': data['partial'], 'planSha256': original.PLAN_SHA}:
        raise ValueError('Prepared marker differs')
    check_partial(root / PARTIAL, data['partial'])
    effective = adjusted_plan(plan, data)
    if mode == 'receive':
        check_prefix(root, data)
        result = original.receive(root, effective, 8, sys.stdin.buffer)
        if result['archiveBytes'] != data['root8CompressedBytes']:
            raise ValueError('Remainder archive size differs from exact rehearsal')
    else:
        result = original.verify_all(root, effective)
    result['continuationSha256'] = digest
    result['totalArchiveCapBytes'] = CAP
    result['preservedPartialBytes'] = data['partial']['bytes']
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['prepare', 'receive', 'verify-all'])
    parser.add_argument('--continuation-sha256', required=True)
    args = parser.parse_args()
    root = original.ROOT
    if Path(__file__).resolve().parent != root:
        raise ValueError('Unexpected script path')
    plan = original.load_plan()
    data = amendment(root, args.continuation_sha256)
    if original.hash_file(Path(__file__)) != data['receiverSha256']:
        raise ValueError('Continuation code hash differs')
    fd = os.open(root / '.writer.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        original.regular(root / '.writer.lock')
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        print(json.dumps(run(args.mode, root, plan, data, args.continuation_sha256)))
    finally:
        os.close(fd)


if __name__ == '__main__':
    main()
