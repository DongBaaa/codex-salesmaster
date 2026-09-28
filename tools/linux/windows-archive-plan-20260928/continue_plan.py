"""Explicitly reviewed continuation after original archive capacity failure."""
import argparse
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import tarfile

import execute_plan as original


def command(mode, digest):
    return shlex.join(['python3', original.REMOTE + '/continue_receive.py', mode,
                      '--continuation-sha256', digest])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['upload', 'delete'])
    parser.add_argument('--approved-plan-sha256', required=True)
    parser.add_argument('--continuation-sha256', required=True)
    args = parser.parse_args()
    if args.approved_plan_sha256 != original.PLAN_SHA:
        raise ValueError('Original approved file plan required')
    amendment = original.EVIDENCE / 'continuation.json'
    if hashlib.sha256(amendment.read_bytes()).hexdigest() != args.continuation_sha256:
        raise ValueError('Continuation differs')
    data = json.loads(amendment.read_text(encoding='utf-8'))
    if hashlib.sha256(Path(__file__).read_bytes()).hexdigest() != data['localExecutorSha256']:
        raise ValueError('Local continuation code changed')
    if hashlib.sha256(Path(__file__).with_name('continue_receive.py').read_bytes()).hexdigest() != data['receiverSha256']:
        raise ValueError('Receiver continuation code changed')
    plan = original.load_plan()
    before = json.loads((original.EVIDENCE / 'protected-before.json').read_text())
    if before != original.protected_hashes():
        raise ValueError('Protected originals changed')
    original.remote_command = lambda mode, index=None: command(mode, args.continuation_sha256)
    if args.mode == 'delete':
        original.delete(plan)
        return
    original.preflight(plan)
    guard = "from pathlib import Path; r=Path(" + repr(original.REMOTE) + "); assert not any((r/n).exists() or (r/n).is_symlink() for n in ['continuation.json','continue_receive.py','continuation-prepared.json','incomplete-root-8.tar.gz'])"
    original.ssh(shlex.join(['python3', '-c', guard]))
    # Upload only reviewed continuation code and its immutable audit manifest.
    for path in [amendment, Path(__file__).with_name('continue_receive.py')]:
        subprocess.run([original.SCP, '-P', '2222', '-i', str(Path.home() / '.ssh/itwserver_codex_ed25519'),
                        '-o', 'BatchMode=yes', str(path), 'itw@192.168.0.199:' + original.REMOTE + '/' + path.name],
                       check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    original.ssh('chmod 600 ' + shlex.quote(original.REMOTE + '/continuation.json') + ' ' + shlex.quote(original.REMOTE + '/continue_receive.py'))
    prepared = json.loads(original.ssh(command('prepare', args.continuation_sha256), timeout=600).stdout)
    original.record('continuation-prepared.json', prepared)
    proc = subprocess.Popen([*original.SSH_ARGS, command('receive', args.continuation_sha256)],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        with tarfile.open(fileobj=proc.stdin, mode='w|gz', compresslevel=1, format=tarfile.PAX_FORMAT) as bundle:
            for row in plan['files']:
                if row['rootIndex'] != 8:
                    continue
                with original.locked_file(Path(row['path'])) as (stream, _):
                    original.check_file(stream, row)
                    info = tarfile.TarInfo(row['relative'])
                    info.size, info.mode, info.mtime = row['bytes'], 0o600, 0
                    bundle.addfile(info, stream)
        proc.stdin.close()
        proc.stdin = None
        stdout, stderr = proc.communicate(timeout=180)
        if proc.returncode:
            raise RuntimeError('Continuation receiver failed; preserve all originals')
        result = json.loads(stdout)
        if not result.get('received') or result.get('rootIndex') != 8 or result.get('continuationSha256') != args.continuation_sha256:
            raise ValueError('Continuation receiver result differs')
        original.record('received-8.json', result)
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=30)
    print('Remaining archive received; verifying all files and extracted samples', flush=True)
    verified = original.verify_remote(plan)
    if verified.get('continuationSha256') != args.continuation_sha256:
        raise ValueError('Continuation verification hash differs')
    original.record('all-verified.json', verified)


if __name__ == '__main__':
    main()
