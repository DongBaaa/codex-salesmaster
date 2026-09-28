#!/usr/bin/env python3
"""Check or atomically replace only the TradePlan release script. Never runs it.

Default is read-only. --apply requires both reviewed content hashes. For rollback,
use the saved backup as --candidate and reverse the expected hashes.
"""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import stat
import subprocess
import tempfile
import uuid

OPS = Path('/srv/georaeplan/ops')
TARGET_NAME = 'apply-release.sh'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def regular_bytes(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        metadata = os.fstat(fd)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1:
            raise ValueError('Expected a regular, single-link file')
        if metadata.st_size > 2 * 1024 * 1024:
            raise ValueError('Script exceeds size limit')
        with os.fdopen(os.dup(fd), 'rb') as stream:
            data = stream.read(2 * 1024 * 1024 + 1)
        if len(data) != metadata.st_size:
            raise ValueError('File changed during read')
        return data, metadata
    finally:
        os.close(fd)


def assert_syntax(data):
    result = subprocess.run(['/bin/bash', '-n'], input=data, capture_output=True, timeout=10)
    if result.returncode:
        raise ValueError('Bash syntax check failed')


def shell_runs_release(commandline):
    args = commandline.rstrip(b'\0').decode('utf-8', errors='replace').split('\0')
    if len(args) < 2:
        return False
    if '-c' in args:
        index = args.index('-c')
        if index + 1 >= len(args):
            return False
        try:
            words = shlex.split(args[index + 1])
        except ValueError:
            raise RuntimeError('Cannot inspect running shell command safely')
        # An installer/checker receiving this filename is not executing it.
        return any(Path(word).name == TARGET_NAME and
                   (index == 0 or words[index - 1] not in ('--candidate', '--script', '-n', 'sha256sum', 'stat'))
                   for index, word in enumerate(words))
    words = [word for word in args[1:] if not word.startswith('-')]
    return bool(words and Path(words[0]).name == TARGET_NAME)


def assert_idle():
    """Inspect only shell command lines; never emit arguments or other processes."""
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            comm = (process / 'comm').read_text().strip()
            if comm not in ('bash', 'sh', 'dash'):
                continue
            if shell_runs_release((process / 'cmdline').read_bytes()):
                raise RuntimeError('An apply-release shell is running; retry after it finishes')
        except (FileNotFoundError, ProcessLookupError):
            continue


def require_directory(path):
    if not path.is_absolute() or path.resolve(strict=True) != path:
        raise ValueError('Operations directory must be a canonical directory without symlinks')
    if not stat.S_ISDIR(path.lstat().st_mode):
        raise ValueError('Operations directory is not a directory')


def prepared_file(ops, data, original_stat, prefix):
    fd, name = tempfile.mkstemp(dir=ops, prefix=prefix, suffix='.sh')
    path = Path(name)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fchown(stream.fileno(), original_stat.st_uid, original_stat.st_gid)
            os.fchmod(stream.fileno(), stat.S_IMODE(original_stat.st_mode))
            os.fsync(stream.fileno())
        assert_syntax(regular_bytes(path)[0])
        return path
    except BaseException:
        path.unlink(missing_ok=True)
        raise


def verify_target(target, expected, metadata):
    data, actual = regular_bytes(target)
    if digest(data) != expected:
        raise ValueError('Installed hash does not match')
    if (actual.st_uid, actual.st_gid, stat.S_IMODE(actual.st_mode)) != (
            metadata.st_uid, metadata.st_gid, stat.S_IMODE(metadata.st_mode)):
        raise ValueError('Installed ownership or permissions differ')
    assert_syntax(data)


def install(ops, candidate, expected_current, expected_candidate, apply=False):
    for value in (expected_current, expected_candidate):
        if not re.fullmatch('[0-9a-f]{64}', value):
            raise ValueError('Exact lowercase SHA-256 hashes are required')
    require_directory(ops)
    target = ops / TARGET_NAME
    old_data, old_stat = regular_bytes(target)
    new_data, _ = regular_bytes(candidate)
    if digest(old_data) != expected_current or digest(new_data) != expected_candidate:
        raise ValueError('Reviewed current or candidate hash changed')
    if os.listxattr(target, follow_symlinks=False):
        raise ValueError('Additional file metadata requires explicit review')
    assert_syntax(new_data)
    assert_idle()
    result = {'mode': 'apply' if apply else 'check', 'target': str(target),
              'oldSha256': expected_current, 'newSha256': expected_candidate,
              'ownerUid': old_stat.st_uid, 'ownerGid': old_stat.st_gid,
              'permissions': oct(stat.S_IMODE(old_stat.st_mode)),
              'scriptExecuted': False, 'serviceRestarted': False, 'changed': False}
    lock = ops / '.apply-release.lock'
    flags = os.O_RDWR | os.O_NOFOLLOW | os.O_NONBLOCK
    if apply:
        flags |= os.O_CREAT
    elif not lock.exists() and not lock.is_symlink():
        return result
    lock_fd = os.open(lock, flags, 0o600)
    staging = None
    try:
        if not stat.S_ISREG(os.fstat(lock_fd).st_mode) or os.fstat(lock_fd).st_nlink != 1:
            raise ValueError('Unsafe release lock')
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if not apply:
            return result
        verify_target(target, expected_current, old_stat)
        assert_idle()
        if old_data == new_data:
            return result
        backup = prepared_file(ops, old_data, old_stat, 'apply-release.preinstall-' + uuid.uuid4().hex + '-')
        result['backup'] = str(backup)
        result['backupSha256'] = digest(regular_bytes(backup)[0])
        if result['backupSha256'] != expected_current:
            raise RuntimeError('Backup hash mismatch')
        staging = prepared_file(ops, new_data, old_stat, '.apply-release.pending-')
        verify_target(target, expected_current, old_stat)
        assert_idle()
        directory_fd = os.open(ops, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            # Persist backup and staged file names before changing the live name.
            os.fsync(directory_fd)
            os.replace(staging, target)
            staging = None
            try:
                os.fsync(directory_fd)
                verify_target(target, expected_candidate, old_stat)
            except BaseException as failure:
                # Do not overwrite a concurrent, unrelated edit while recovering.
                if digest(regular_bytes(target)[0]) != expected_candidate:
                    raise RuntimeError('Postcheck failed and target changed; preserve backup for manual review: ' + str(backup)) from failure
                recovery = prepared_file(ops, old_data, old_stat, '.apply-release.recovery-')
                try:
                    os.replace(recovery, target)
                    os.fsync(directory_fd)
                    verify_target(target, expected_current, old_stat)
                finally:
                    recovery.unlink(missing_ok=True)
                raise RuntimeError('Postcheck failed; original script restored. Backup: ' + str(backup)) from failure
        finally:
            os.close(directory_fd)
        result['changed'] = True
        return result
    finally:
        if staging is not None:
            staging.unlink(missing_ok=True)
        os.close(lock_fd)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--expected-current', required=True)
    parser.add_argument('--expected-candidate', required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    try:
        result = install(OPS, args.candidate, args.expected_current, args.expected_candidate, args.apply)
        print(json.dumps(result))
    except Exception as error:
        print(json.dumps({'status': 'failed', 'errorType': type(error).__name__, 'message': str(error),
                          'scriptExecuted': False, 'serviceRestarted': False}))
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
