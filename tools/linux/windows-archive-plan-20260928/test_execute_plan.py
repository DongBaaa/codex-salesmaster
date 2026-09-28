"""Synthetic local safety checks. Never invokes SSH or the real deletion plan."""
import copy
import ctypes
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tarfile
import tempfile
import types
import unittest
from unittest.mock import patch

import execute_plan as local

# Receiver file/stream logic is portable; Linux mount/flock checks are not
# asserted by these Windows tests and remain mandatory in the actual receiver.
spec = importlib.util.spec_from_file_location('receiver_under_test', Path(__file__).with_name('receive_archive.py'))
remote = importlib.util.module_from_spec(spec)
with patch.dict(sys.modules, {'fcntl': types.SimpleNamespace()}):
    spec.loader.exec_module(remote)

TEST_ROOT = Path(r'C:\Users\beene\Documents\Codex\disk-capacity-followup-20260928\executor-tests')


class ExecutorSafetyTests(unittest.TestCase):
    def setUp(self):
        TEST_ROOT.mkdir(exist_ok=True)
        self.root = Path(tempfile.mkdtemp(prefix='fixture-', dir=TEST_ROOT))
        self.path = self.root / 'data.bin'
        self.path.write_bytes(b'known disposable test content')
        info = self.path.stat()
        self.row = {'path': str(self.path), 'relative': 'data.bin', 'rootIndex': 0,
                    'bytes': info.st_size, 'mtimeNs': info.st_mtime_ns, 'fileId': str(info.st_ino),
                    'linkCount': 1, 'sha256': hashlib.sha256(self.path.read_bytes()).hexdigest(),
                    'deleteAfterVerifiedArchive': True}

    def tearDown(self):
        # This recursively removes only the checked, newly created synthetic fixture.
        self.assertEqual(self.root.resolve().parent, TEST_ROOT.resolve())
        self.assertTrue(self.root.name.startswith('fixture-'))
        local.reparse_guard(self.root)
        shutil.rmtree(self.root)

    def test_exclusive_read_prevents_concurrent_write(self):
        with local.locked_file(self.path) as (stream, _):
            local.check_file(stream, self.row)
            with self.assertRaises(PermissionError):
                self.path.write_bytes(b'changed')
        self.assertEqual(self.path.read_bytes(), b'known disposable test content')

    def test_parent_cannot_move_while_file_checked(self):
        with local.locked_file(self.path):
            with self.assertRaises(PermissionError):
                self.root.rename(self.root.with_name(self.root.name + '-moved'))

    def test_content_and_metadata_drift_rejected(self):
        wrong = {**self.row, 'sha256': '0' * 64}
        with local.locked_file(self.path) as (stream, _):
            with self.assertRaises(ValueError):
                local.check_file(stream, wrong)
            with self.assertRaises(ValueError):
                local.check_file(stream, {**self.row, 'fileId': '1'})

    def test_hardlink_is_rejected(self):
        os.link(self.path, self.root / 'alias.bin')
        with local.locked_file(self.path) as (stream, _):
            with self.assertRaises(ValueError):
                local.check_file(stream, self.row)

    def test_disposition_deletes_only_the_checked_handle(self):
        keep = self.root / 'keep.db'
        keep.write_bytes(b'preserve')
        with local.locked_file(self.path, delete=True) as (stream, handle):
            local.check_file(stream, self.row)
            disposition = local.wintypes.BOOL(True)
            self.assertTrue(local.KERNEL.SetFileInformationByHandle(handle, 4, ctypes.byref(disposition), ctypes.sizeof(disposition)))
        self.assertFalse(self.path.exists())
        self.assertEqual(keep.read_bytes(), b'preserve')

    def test_remote_verification_failure_never_reaches_delete(self):
        evidence = self.root / 'evidence'
        evidence.mkdir()
        (evidence / 'all-verified.json').write_text('{}')
        with patch.object(local, 'EVIDENCE', evidence), patch.object(local, 'verify_remote', side_effect=ValueError('bad archive')), \
                patch.object(local, 'locked_file') as locked:
            with self.assertRaises(ValueError):
                local.delete({'files': [self.row]})
            locked.assert_not_called()
        self.assertTrue(self.path.exists())

    def test_missing_approval_blocks_upload_before_network(self):
        with patch.object(sys, 'argv', ['execute_plan.py', 'upload']), patch.object(local, 'upload') as upload:
            with self.assertRaises(ValueError):
                local.main()
            upload.assert_not_called()

    def test_traversal_plan_rejected(self):
        plan = local.load_plan()
        plan['files'][0]['relative'] = '../outside.bin'
        with self.assertRaises(ValueError):
            local.validate_plan(plan)

    def test_extra_inventory_file_rejected(self):
        plan = {'files': [self.row]}
        (self.root / 'extra.bin').write_bytes(b'unexpected')
        with patch.object(local, 'validate_plan', return_value={0: self.root}):
            with self.assertRaises(ValueError):
                local.check_inventory(plan)

    def receiver_plan(self):
        return {'roots': [{'index': 0, 'files': 1}], 'files': [self.row],
                'remoteArchiveCapBytes': 1024 * 1024, 'minimumSsdFreeAfterBytes': 0}

    def archive(self):
        out = io.BytesIO()
        with tarfile.open(fileobj=out, mode='w:gz') as bundle:
            member = tarfile.TarInfo('data.bin')
            member.size = self.row['bytes']
            bundle.addfile(member, io.BytesIO(self.path.read_bytes()))
        return out.getvalue()

    def test_receiver_cap_failure_preserves_input(self):
        plan = self.receiver_plan()
        plan['remoteArchiveCapBytes'] = 1
        with self.assertRaises(ValueError):
            remote.receive(self.root, plan, 0, io.BytesIO(self.archive()))
        self.assertEqual(self.path.read_bytes(), b'known disposable test content')

    def test_receiver_never_overwrites_existing_archive(self):
        dest = self.root / 'root-0.tar.gz'
        dest.write_bytes(b'previous attempt')
        with self.assertRaises(ValueError):
            remote.receive(self.root, self.receiver_plan(), 0, io.BytesIO(self.archive()))
        self.assertEqual(dest.read_bytes(), b'previous attempt')

    def test_full_archive_hashes_and_real_extraction(self):
        remote.receive(self.root, self.receiver_plan(), 0, io.BytesIO(self.archive()))
        result = remote.verify_all(self.root, self.receiver_plan())
        self.assertTrue(result['verified'])
        self.assertEqual(result['fileCount'], 1)
        self.assertEqual((self.root / 'samples/root-0.sample').read_bytes(), self.path.read_bytes())
        self.assertEqual(result, remote.verify_all(self.root, self.receiver_plan()) | {'ssdFreeBytes': result['ssdFreeBytes']})

    def test_partial_set_or_modified_member_never_verifies(self):
        plan = self.receiver_plan()
        with self.assertRaises(ValueError):
            remote.verify_all(self.root, plan)
        remote.receive(self.root, plan, 0, io.BytesIO(self.archive()))
        plan['files'][0]['sha256'] = '0' * 64
        with self.assertRaises(ValueError):
            remote.verify_all(self.root, plan)

    def test_ssd_reserve_blocks_receive(self):
        plan = self.receiver_plan()
        plan['minimumSsdFreeAfterBytes'] = 100
        with patch.object(remote.shutil, 'disk_usage', return_value=types.SimpleNamespace(free=99)):
            with self.assertRaises(ValueError):
                remote.receive(self.root, plan, 0, io.BytesIO(self.archive()))
        self.assertFalse((self.root / 'root-0.tar.gz').exists())

    def test_complete_delete_flow_keeps_unselected_data_and_journal(self):
        source = self.root / 'source'
        source.mkdir()
        self.path.rename(source / 'data.bin')
        self.path = source / 'data.bin'
        self.row['path'] = str(self.path)
        keep = source / 'keep.db'
        keep.write_bytes(b'preserved synthetic database')
        info = keep.stat()
        keep_row = {'path': str(keep), 'relative': 'keep.db', 'rootIndex': 0, 'bytes': info.st_size,
                    'mtimeNs': info.st_mtime_ns, 'fileId': str(info.st_ino), 'linkCount': 1,
                    'sha256': hashlib.sha256(keep.read_bytes()).hexdigest(), 'deleteAfterVerifiedArchive': False}
        plan = {'roots': [{'index': 0, 'files': 2}], 'files': [self.row, keep_row],
                'remoteArchiveCapBytes': 1024 * 1024, 'minimumSsdFreeAfterBytes': 0}
        archived = self.root / 'archived'
        archived.mkdir()
        with tarfile.open(archived / 'root-0.tar.gz', 'w:gz') as bundle:
            for path in (self.path, keep):
                member = tarfile.TarInfo(path.name)
                member.size = path.stat().st_size
                bundle.addfile(member, io.BytesIO(path.read_bytes()))
        evidence = self.root / 'evidence'
        evidence.mkdir()
        report = remote.verify_all(archived, plan)
        (evidence / 'all-verified.json').write_text(json.dumps(report))
        protected = self.root / 'original.db'
        protected.write_bytes(b'original unaffected')
        (evidence / 'protected-before.json').write_text(json.dumps({str(protected): hashlib.sha256(protected.read_bytes()).hexdigest()}))
        with patch.object(local, 'EVIDENCE', evidence), patch.object(local, 'PROTECTED', [protected]), \
                patch.object(local, 'validate_plan', return_value={0: source}), patch.object(local, 'check_processes'), \
                patch.object(local, 'verify_remote', side_effect=lambda _: remote.verify_all(archived, plan)):
            local.delete(plan)
        self.assertFalse(self.path.exists())
        self.assertEqual(keep.read_bytes(), b'preserved synthetic database')
        self.assertEqual(protected.read_bytes(), b'original unaffected')
        journal = [json.loads(line) for line in (evidence / 'deletion.jsonl').read_text().splitlines()]
        self.assertEqual([r['phase'] for r in journal], ['intent', 'deleted'])
        self.assertEqual(json.loads((evidence / 'completed.json').read_text())['deleted'], 1)
        self.assertTrue(remote.verify_all(archived, plan)['verified'])

    def test_bootstrap_is_fixed_destination_and_compiles(self):
        import shlex
        command = shlex.split(local.bootstrap_command())
        self.assertEqual(command[:2], ['python3', '-c'])
        compile(command[2], '<receiver-bootstrap>', 'exec')
        self.assertIn("if root.exists() or root.is_symlink():", command[2])
        self.assertIn("'/dev/sdb1'", command[2])


if __name__ == '__main__':
    unittest.main()
