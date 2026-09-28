import copy
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

with patch.dict(sys.modules, {'fcntl': types.SimpleNamespace()}):
    import continue_receive as subject


class ContinuationTests(unittest.TestCase):
    def setUp(self):
        self.base = Path(r'C:\Users\beene\Documents\Codex\disk-capacity-followup-20260928\executor-tests')
        self.base.mkdir(exist_ok=True)
        self.root = Path(tempfile.mkdtemp(prefix='continuation-', dir=self.base))
        reports = []
        for i in range(9):
            p = self.root / f'root-{i}.tar.gz'
            p.write_bytes(bytes([i]) * 100)
            if i < 8:
                reports.append({'verified': True, 'rootIndex': i, 'archiveBytes': 100, 'archiveSha256': subject.original.hash_file(p)})
        p = self.root / 'root-8.tar.gz'
        self.data = {'originalPlanSha256': subject.original.PLAN_SHA, 'totalArchiveCapBytes': subject.CAP,
                     'minimumSsdFreeAfterBytes': subject.RESERVE, 'rootIndex': 8, 'completed': reports,
                     'root8CompressedBytes': 200, 'partial': {'bytes': 100, 'inode': p.stat().st_ino, 'sha256': subject.original.hash_file(p)}}
        self.plan = {'minimumSsdFreeAfterBytes': subject.RESERVE, 'remoteArchiveCapBytes': 12 * 1024**3}
        self.space = patch.object(subject.original.shutil, 'disk_usage', return_value=types.SimpleNamespace(free=40 * 1024**3))
        self.space.start()
        self.dir_sync = patch.object(subject, 'sync_directory')
        self.dir_sync.start()

    def tearDown(self):
        self.space.stop()
        self.dir_sync.stop()
        assert self.root.resolve().parent == self.base.resolve() and self.root.name.startswith('continuation-')
        shutil.rmtree(self.root)

    def test_preserves_partial_and_completed_archives(self):
        before = {p.name: p.read_bytes() for p in self.root.glob('*.gz')}
        subject.prepare(self.root, self.plan, self.data)
        self.assertEqual((self.root / subject.PARTIAL).read_bytes(), before['root-8.tar.gz'])
        for i in range(8):
            self.assertEqual((self.root / f'root-{i}.tar.gz').read_bytes(), before[f'root-{i}.tar.gz'])
        self.assertFalse((self.root / 'root-8.tar.gz').exists())

    def test_partial_counts_towards_cap_without_mutating_original(self):
        before = copy.deepcopy(self.plan)
        effective = subject.adjusted_plan(self.plan, self.data)
        self.assertEqual(effective['remoteArchiveCapBytes'], subject.CAP - 100)
        self.assertEqual(self.plan, before)

    def test_rejects_capacity_overflow_before_rename(self):
        self.data['root8CompressedBytes'] = subject.CAP
        with self.assertRaises(ValueError): subject.prepare(self.root, self.plan, self.data)
        self.assertTrue((self.root / 'root-8.tar.gz').exists())

    def test_rejects_reserve_shortfall_before_rename(self):
        with patch.object(subject.original.shutil, 'disk_usage', return_value=types.SimpleNamespace(free=subject.RESERVE + 199)):
            with self.assertRaises(ValueError): subject.prepare(self.root, self.plan, self.data)
        self.assertTrue((self.root / 'root-8.tar.gz').exists())

    def test_rejects_changed_completed_archive(self):
        (self.root / 'root-0.tar.gz').write_bytes(b'x' * 100)
        with self.assertRaises(ValueError): subject.prepare(self.root, self.plan, self.data)
        self.assertTrue((self.root / 'root-8.tar.gz').exists())

    def test_rejects_changed_partial(self):
        (self.root / 'root-8.tar.gz').write_bytes(b'x' * 100)
        with self.assertRaises(ValueError): subject.prepare(self.root, self.plan, self.data)

    def test_refuses_automatic_repeat(self):
        subject.prepare(self.root, self.plan, self.data)
        with self.assertRaises(ValueError): subject.prepare(self.root, self.plan, self.data)

    def test_amendment_hash_and_scope(self):
        p = self.root / 'continuation.json'
        raw = json.dumps(self.data).encode()
        p.write_bytes(raw)
        digest = hashlib.sha256(raw).hexdigest()
        self.assertEqual(subject.amendment(self.root, digest), self.data)
        with self.assertRaises(ValueError): subject.amendment(self.root, '0' * 64)
        self.data['rootIndex'] = 0
        raw = json.dumps(self.data).encode(); p.write_bytes(raw)
        with self.assertRaises(ValueError): subject.amendment(self.root, hashlib.sha256(raw).hexdigest())

    def test_verification_still_checks_preserved_partial(self):
        subject.prepare(self.root, self.plan, self.data)
        (self.root / subject.PARTIAL).write_bytes(b'x' * 100)
        with patch.object(subject.original, 'verify_all') as verify:
            with self.assertRaises(ValueError): subject.run('verify-all', self.root, self.plan, self.data, 'digest')
            verify.assert_not_called()


if __name__ == '__main__':
    unittest.main(verbosity=2)
