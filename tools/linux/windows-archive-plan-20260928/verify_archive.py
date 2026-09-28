"""Read-only streamed verification of one approved Windows archive.

Never extracts or deletes files. The optional CLI report must be a new file.
Archives contain relative regular files only; source credentials stay opaque.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import tarfile


def safe_name(value):
    if not isinstance(value, str) or not value or '\\' in value or '\x00' in value:
        raise ValueError('Invalid archive path')
    while value.startswith('./'):
        value = value[2:]
    parts = value.split('/')
    if any(part in ('', '.', '..') or ':' in part for part in parts) or PurePosixPath(value).is_absolute():
        raise ValueError('Non-relative archive path')
    return value


def hash_file(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def verify(archive, plan, root_index):
    roots = [root for root in plan['roots'] if root['index'] == root_index]
    if len(roots) != 1:
        raise ValueError('Unknown or repeated root index')
    expected = {}
    case_names = set()
    for row in plan['files']:
        if row['rootIndex'] != root_index:
            continue
        name = safe_name(row['relative'])
        if name in expected or name.casefold() in case_names:
            raise ValueError('Duplicate manifest path')
        if type(row['bytes']) is not int or row['bytes'] < 0:
            raise ValueError('Invalid expected file size')
        expected[name] = row
        case_names.add(name.casefold())
    if len(expected) != roots[0]['files'] or not expected:
        raise ValueError('Manifest population mismatch')
    archive = Path(archive)
    before = archive.stat()
    if before.st_size > plan['remoteArchiveCapBytes']:
        raise ValueError('Archive size cap exceeded')
    seen = set()
    total = 0
    with tarfile.open(archive, mode='r:gz') as bundle:
        for member in bundle:
            name = safe_name(member.name)
            if not member.isfile() or member.issym() or member.islnk():
                raise ValueError('Only regular file members are allowed')
            if name not in expected or name in seen:
                raise ValueError('Unexpected or repeated archive member')
            row = expected[name]
            if member.size != row['bytes']:
                raise ValueError('Archive file size differs')
            digest = hashlib.sha256()
            length = 0
            stream = bundle.extractfile(member)
            if stream is None:
                raise ValueError('File content missing')
            with stream:
                while chunk := stream.read(1024 * 1024):
                    length += len(chunk)
                    if length > row['bytes']:
                        raise ValueError('Archive file exceeds expected size')
                    digest.update(chunk)
            if length != row['bytes'] or digest.hexdigest() != row['sha256']:
                raise ValueError('Archive file hash differs')
            total += length
            seen.add(name)
        # Read through gzip's footer so truncation/CRC failure cannot be ignored.
        while chunk := bundle.fileobj.read(1024 * 1024):
            if chunk.strip(b'\x00'):
                raise ValueError('Unexpected data after tar archive')
    if seen != set(expected):
        raise ValueError('Archive files missing')
    after = archive.stat()
    if (before.st_size, before.st_mtime_ns, before.st_ino) != (after.st_size, after.st_mtime_ns, after.st_ino):
        raise ValueError('Archive changed during verification')
    archive_hash = hash_file(archive)
    final = archive.stat()
    if (after.st_size, after.st_mtime_ns, after.st_ino) != (final.st_size, final.st_mtime_ns, final.st_ino):
        raise ValueError('Archive changed during hashing')
    return {'verified': True, 'rootIndex': root_index, 'files': len(seen),
            'logicalBytes': total, 'archiveBytes': after.st_size, 'archiveSha256': archive_hash,
            'filesExtracted': 0, 'filesDeleted': 0}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--plan-sha256', required=True)
    parser.add_argument('--root-index', type=int, required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    raw = args.plan.read_bytes()
    if hashlib.sha256(raw).hexdigest() != args.plan_sha256:
        raise ValueError('Approval plan hash differs')
    result = verify(args.archive, json.loads(raw), args.root_index)
    result['planSha256'] = args.plan_sha256
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)
        stream.write('\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
