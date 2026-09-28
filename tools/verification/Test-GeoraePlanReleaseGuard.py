"""Linux tests of the one-file installer; temporary files only, no service calls."""
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch


parser = argparse.ArgumentParser()
parser.add_argument('--installer', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
spec = importlib.util.spec_from_file_location('release_guard', args.installer)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
OLD = b'#!/bin/bash\necho old\n'
NEW = b'#!/bin/bash\necho new\n'
sha = lambda data: hashlib.sha256(data).hexdigest()


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='georaeplan-guard-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.ops = self.root / 'ops'
        self.ops.mkdir()
        self.target = self.ops / 'apply-release.sh'
        self.target.write_bytes(OLD)
        self.target.chmod(0o775)
        self.candidate = self.root / 'candidate.sh'
        self.candidate.write_bytes(NEW)
        self.idle = patch.object(guard, 'assert_idle')
        self.idle_mock = self.idle.start()
        self.addCleanup(self.idle.stop)

    def run_install(self, apply=True, **overrides):
        values = dict(ops=self.ops, candidate=self.candidate, expected_current=sha(OLD),
                      expected_candidate=sha(NEW), apply=apply)
        values.update(overrides)
        return guard.install(**values)

    def unchanged(self):
        self.assertEqual(self.target.read_bytes(), OLD)
        self.assertEqual(stat.S_IMODE(self.target.stat().st_mode), 0o775)

    def test_check_creates_nothing(self):
        before = sorted(self.root.rglob('*'))
        result = self.run_install(apply=False)
        self.assertFalse(result['changed'])
        self.assertEqual(before, sorted(self.root.rglob('*')))
        self.unchanged()

    def test_shell_detection_covers_direct_and_publisher(self):
        self.assertTrue(guard.shell_runs_release(b'bash\0/srv/georaeplan/ops/apply-release.sh\0release-id\0'))
        self.assertTrue(guard.shell_runs_release(b'bash\0-c\0cd /srv/georaeplan/ops && HEALTH_CHECK_RETRIES=900 /bin/bash ./apply-release.sh release-id\0'))
        self.assertFalse(guard.shell_runs_release(b'bash\0-c\0python3 installer.py --candidate /tmp/apply-release.sh --expected-current abc\0'))
        self.assertTrue(guard.shell_runs_release(b'bash\0-n\0/tmp/apply-release.sh\0'))  # Conservative while syntax-checking directly.

    def test_install_preserves_metadata_and_backup(self):
        old = self.target.stat()
        result = self.run_install()
        self.assertTrue(result['changed'])
        self.assertEqual(self.target.read_bytes(), NEW)
        self.assertEqual(Path(result['backup']).read_bytes(), OLD)
        for path in (self.target, Path(result['backup'])):
            current = path.stat()
            self.assertEqual((current.st_uid, current.st_gid, stat.S_IMODE(current.st_mode)),
                             (old.st_uid, old.st_gid, 0o775))
        self.assertFalse(result['scriptExecuted'])
        self.assertFalse(result['serviceRestarted'])

    def test_restore_preserves_both_versions(self):
        first = self.run_install()
        second = self.run_install(candidate=Path(first['backup']), expected_current=sha(NEW), expected_candidate=sha(OLD))
        self.unchanged()
        self.assertEqual(Path(first['backup']).read_bytes(), OLD)
        self.assertEqual(Path(second['backup']).read_bytes(), NEW)

    def test_wrong_current_rejected_without_writes(self):
        with self.assertRaises(ValueError):
            self.run_install(expected_current='0' * 64)
        self.assertEqual([self.target], list(self.ops.iterdir()))
        self.unchanged()

    def test_wrong_candidate_rejected(self):
        with self.assertRaises(ValueError):
            self.run_install(expected_candidate='0' * 64)
        self.unchanged()

    def test_invalid_hash_rejected(self):
        with self.assertRaises(ValueError):
            self.run_install(expected_current='not-a-hash')
        self.unchanged()

    def test_invalid_shell_rejected(self):
        data = b'if then\n'
        self.candidate.write_bytes(data)
        with self.assertRaisesRegex(ValueError, 'syntax'):
            self.run_install(expected_candidate=sha(data))
        self.unchanged()

    def test_candidate_is_never_executed(self):
        marker = self.root / 'MUST_NOT_EXIST'
        data = f'#!/bin/bash\ntouch {marker}\n'.encode()
        self.candidate.write_bytes(data)
        self.run_install(expected_candidate=sha(data))
        self.assertFalse(marker.exists())

    def test_symlink_target_rejected(self):
        other = self.root / 'other.sh'
        self.target.rename(other)
        self.target.symlink_to(other)
        with self.assertRaises(OSError):
            self.run_install()
        self.assertEqual(other.read_bytes(), OLD)

    def test_hardlinked_target_rejected(self):
        os.link(self.target, self.root / 'other.sh')
        with self.assertRaises(ValueError):
            self.run_install()
        self.unchanged()

    def test_symlink_candidate_rejected(self):
        link = self.root / 'candidate-link.sh'
        link.symlink_to(self.candidate)
        with self.assertRaises(OSError):
            self.run_install(candidate=link)
        self.unchanged()

    def test_symlink_operations_directory_rejected(self):
        link = self.root / 'ops-link'
        link.symlink_to(self.ops, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.run_install(ops=link)
        self.unchanged()

    def test_symlink_lock_rejected(self):
        (self.ops / '.apply-release.lock').symlink_to(self.candidate)
        with self.assertRaises(OSError):
            self.run_install()
        self.unchanged()

    def test_busy_lock_rejected(self):
        with (self.ops / '.apply-release.lock').open('wb') as lock:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(BlockingIOError):
                self.run_install()
        self.unchanged()

    def test_active_apply_rejected(self):
        self.idle_mock.side_effect = RuntimeError('active apply')
        with self.assertRaises(RuntimeError):
            self.run_install()
        self.assertEqual([self.target], list(self.ops.iterdir()))
        self.unchanged()

    def test_additional_metadata_rejected(self):
        os.setxattr(self.target, 'user.guard-test', b'preserve')
        with self.assertRaisesRegex(ValueError, 'metadata'):
            self.run_install()
        self.assertEqual(os.getxattr(self.target, 'user.guard-test'), b'preserve')
        self.unchanged()

    def test_failure_before_rename_preserves_original(self):
        with patch.object(guard.os, 'replace', side_effect=OSError('injected rename failure')):
            with self.assertRaises(OSError):
                self.run_install()
        self.unchanged()
        self.assertFalse(list(self.ops.glob('.apply-release.pending-*')))
        self.assertEqual(len(list(self.ops.glob('apply-release.preinstall-*'))), 1)

    def test_postcheck_failure_restores_original(self):
        original = guard.verify_target
        def fail_new(target, expected, metadata):
            if expected == sha(NEW):
                raise ValueError('injected postcheck failure')
            return original(target, expected, metadata)
        with patch.object(guard, 'verify_target', side_effect=fail_new):
            with self.assertRaisesRegex(RuntimeError, 'original script restored'):
                self.run_install()
        self.unchanged()
        self.assertFalse(list(self.ops.glob('.apply-release.recovery-*')))

    def test_postcheck_concurrent_edit_not_overwritten(self):
        original = guard.verify_target
        foreign = b'#!/bin/bash\necho another-reviewed-edit\n'
        def change_new(target, expected, metadata):
            if expected == sha(NEW):
                target.write_bytes(foreign)
                raise ValueError('injected concurrent edit')
            return original(target, expected, metadata)
        with patch.object(guard, 'verify_target', side_effect=change_new):
            with self.assertRaisesRegex(RuntimeError, 'manual review'):
                self.run_install()
        self.assertEqual(self.target.read_bytes(), foreign)
        backup = list(self.ops.glob('apply-release.preinstall-*'))
        self.assertEqual(len(backup), 1)
        self.assertEqual(backup[0].read_bytes(), OLD)


suite = unittest.defaultTestLoader.loadTestsFromTestCase(GuardTests)
result = unittest.TextTestRunner(verbosity=2).run(suite)
summary = {'testsRun': result.testsRun, 'failures': len(result.failures), 'errors': len(result.errors),
           'successful': result.wasSuccessful(), 'installerSha256': sha(args.installer.read_bytes()),
           'productionChanged': False, 'temporaryFixturesOnly': True}
args.output.write_text(json.dumps(summary, indent=2) + '\n')
raise SystemExit(0 if result.wasSuccessful() else 1)
