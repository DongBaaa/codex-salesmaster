import copy
import gzip
import hashlib
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

from verify_archive import safe_name, verify


class ArchiveVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / 'fixture.tar.gz'
        self.data = {'source/keep.cs': b'fixture source', 'old/app.exe': b'fixture binary'}
        self.plan = {'roots': [{'index': 0, 'files': 2}], 'remoteArchiveCapBytes': 1024 * 1024,
                     'files': [{'rootIndex': 0, 'relative': name, 'bytes': len(value),
                                'sha256': hashlib.sha256(value).hexdigest()} for name, value in self.data.items()]}

    def write(self, entries=None):
        with tarfile.open(self.archive, 'w:gz') as bundle:
            for name, data in entries if entries is not None else self.data.items():
                item = tarfile.TarInfo(name)
                item.size = len(data)
                bundle.addfile(item, io.BytesIO(data))

    def test_complete_archive_preserves_bytes_without_extraction(self):
        self.write()
        result = verify(self.archive, self.plan, 0)
        self.assertTrue(result['verified'])
        self.assertEqual(2, result['files'])
        self.assertEqual([self.archive], list(self.root.iterdir()))

    def test_same_length_changed_bytes_rejected(self):
        self.write([(name, b'X' * len(value)) for name, value in self.data.items()])
        with self.assertRaises(ValueError):
            verify(self.archive, self.plan, 0)

    def test_missing_extra_and_duplicate_members_rejected(self):
        original = list(self.data.items())
        for entries in [original[:1], original + [('extra', b'x')], original + original[:1]]:
            with self.subTest(entries=len(entries)):
                self.write(entries)
                with self.assertRaises(ValueError):
                    verify(self.archive, self.plan, 0)

    def test_traversal_absolute_and_windows_paths_rejected(self):
        for name in ['../outside', '/outside', 'C:/outside', 'a/../b', 'a\\b', '', 'a//b']:
            with self.subTest(name=name), self.assertRaises(ValueError):
                safe_name(name)

    def test_symbolic_and_hard_links_rejected(self):
        for kind in [tarfile.SYMTYPE, tarfile.LNKTYPE]:
            with tarfile.open(self.archive, 'w:gz') as bundle:
                item = tarfile.TarInfo('source/keep.cs')
                item.type = kind
                item.linkname = '../outside'
                bundle.addfile(item)
            with self.assertRaises(ValueError):
                verify(self.archive, self.plan, 0)

    def test_size_cap_and_unknown_root_rejected(self):
        self.write()
        with self.assertRaises(ValueError):
            verify(self.archive, self.plan, 1)
        self.plan['remoteArchiveCapBytes'] = 1
        with self.assertRaises(ValueError):
            verify(self.archive, self.plan, 0)

    def test_manifest_population_and_case_duplicates_rejected(self):
        self.write()
        bad = copy.deepcopy(self.plan)
        bad['files'][1]['relative'] = 'SOURCE/KEEP.CS'
        with self.assertRaises(ValueError):
            verify(self.archive, bad, 0)
        self.plan['roots'][0]['files'] = 3
        with self.assertRaises(ValueError):
            verify(self.archive, self.plan, 0)

    def test_truncated_gzip_footer_rejected(self):
        self.write()
        self.archive.write_bytes(self.archive.read_bytes()[:-5])
        with self.assertRaises((ValueError, EOFError, OSError, tarfile.TarError)):
            verify(self.archive, self.plan, 0)

    def test_hidden_data_after_tar_end_rejected(self):
        self.write()
        raw = gzip.decompress(self.archive.read_bytes())
        self.archive.write_bytes(gzip.compress(raw + b'hidden-unapproved-data'))
        with self.assertRaises(ValueError):
            verify(self.archive, self.plan, 0)


if __name__ == '__main__':
    unittest.main()
