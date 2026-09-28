"""Record bounded local proof without claiming UI, deployment, or API recovery."""
import datetime
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path('D:/거래플랜')
OUT = Path.home() / 'Documents/Codex/tradeplan-rental-site-preservation-20260926'
RUNTIME = ROOT / '테스트 시행/실행환경'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


now = datetime.datetime.now().astimezone().isoformat()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tests = {}
for name, total, passed, failed in [('red-final', 3, 1, 2), ('green-rental', 30, 30, 0), ('rental-regression', 1513, 1513, 0)]:
    counters = ET.parse(OUT / (name + '.trx')).find('.//t:Counters', ns).attrib
    actual = {key: int(counters[key]) for key in ['total', 'passed', 'failed']}
    assert actual == dict(total=total, passed=passed, failed=failed), (name, actual)
    tests[name] = actual

expected = '0438dbd7cfe433d4b6a88e2b9685293ca1f497e7e66502739b6e15c28bb806e2'
artifacts = {}
for label, path in {
    'tested': ROOT / 'Tests/GeoraePlan.Desktop.App.Tests/bin/Release/net8.0-windows/거래플랜.Desktop.App.dll',
    'official': RUNTIME / 'App/거래플랜.Desktop.App.dll',
}.items():
    artifacts[label] = sha(path)
    assert artifacts[label] == expected, label
assert (RUNTIME / '.georaeplan-runtime-ready').exists()
assert not (RUNTIME / '.georaeplan-runtime-invalid').exists()

db_hash = sha(Path.home() / 'AppData/Local/거래플랜/data/거래플랜.db')
head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip()
index_hash = hashlib.sha256(subprocess.check_output(['git', 'ls-files', '--stage', '-z'], cwd=ROOT)).hexdigest()
assert db_hash == '674e0022e2d7b3da5de18f499600e9f311d1da1406fa7dfd21c925574a7ead22'
assert head == 'b3d1457c9569348ef07cd36c57d687ca3c9d1803'
assert index_hash == 'f0ae7b636b2b06db832b0d430d308da4554261093aeb7782ddb5b8b2a1cd39df'
replay = read(OUT / 'real-data-replay.json')
assert replay['observed']['service-fixed']['otherAssetBusinessChanges'] == 0

result = dict(
    at=now, status='local regression and real-data service replay passed; official UI pending',
    tests=tests, focusedTestsIncludedInRentalTotal=True, artifacts=artifacts,
    originalPcDbSha256=db_hash, head=head, indexSemanticSha256=index_hash,
    originalPcDbAndGitIndexPreserved=True, replay=replay,
    officialPreparationPassed=True, formalRunAllStarted=True,
    officialUi={'loginObserved': 'admin (Admin/USENET)', 'rightMonitorVerified': False,
                'rentalNavigationVerified': False, 'normalShutdownVerified': False,
                'pending': 'Move current test window to right monitor before input'},
    freeBytes={drive: shutil.disk_usage(drive + ':/').free for drive in ['C', 'D']},
    package745IncludesThisFix=False, releaseSelectionNeedsRefresh=True,
    operationalBusinessWrites=0, productionPublished=False, gitCommittedOrPushed=False,
    entireGoalComplete=False, consecutiveBlockedAuditTurns=0,
)
(OUT / 'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
state_path = Path('D:/DevCaches/goal-desktop-full-regression-20260921/state.json')
state = read(state_path)
state['rentalSitePreservation20260926'] = result | {'evidence': str(OUT)}
state['updatedAt'] = now
state['previousGoalTurnClassification'] = 'progress'
state_path.write_text(json.dumps(state, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'status': result['status'], 'tests': tests, 'preservation': True, 'freeBytes': result['freeBytes']}, ensure_ascii=False))
