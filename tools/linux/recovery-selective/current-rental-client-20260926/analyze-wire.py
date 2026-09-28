"""Check exact offline retry bodies and identify unrelated pending records."""
from pathlib import Path
import datetime
import hashlib
import json
import sqlite3

out = Path.home() / 'Documents/Codex/tradeplan-rental-wire-20260926'
capture = out / 'capture-01'
saved = Path.home() / 'Documents/Codex/tradeplan-current-rental-client-20260926-v3/service-fixed'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
proof = read(capture / 'verification.json')
assert proof['captureSucceeded'] and proof['Pushes'] == 9 and proof['uniqueBodies'] == 1
assert not proof['completed'] and proof['actualNetworkRequests'] == 0
assert proof['sourceSnapshotUnchanged'] and proof['runtimeUnchanged']
requests = [read(p) for p in sorted(capture.glob('request-*.private.json'))]
assert len(requests) == 9 and len({r['body'] for r in requests}) == 1
assert all(r['method'] == 'POST' and r['path'] == '/sync/push' for r in requests)
body = json.loads(requests[0]['body'])
before = read(saved / 'before-service-rows.private.json')
after = read(saved / 'after-service-rows.private.json')
before_assets = {r['Id'].lower(): r for r in before['RentalAssets']}
after_assets = {r['Id'].lower(): r for r in after['RentalAssets']}
dirty = {key: r for key, r in after_assets.items() if r['IsDirty']}
wire_assets = {r['id'].lower(): r for r in body['rentalAssets']}
assert set(dirty) == set(wire_assets) and len(wire_assets) == 17
target = [key for key, r in dirty.items() if r['ManagementNumber'] == '2412-009']
assert len(target) == 1
existing = set(wire_assets) - set(target)
assert len(existing) == 16
for key in wire_assets:
    assert wire_assets[key]['installSiteName'] == after_assets[key]['InstallSiteName']
for key in existing:
    assert wire_assets[key]['installSiteName'] == before_assets[key]['InstallSiteName']

before_hashes = read(saved / 'before-service-hashes.json')
after_hashes = read(saved / 'after-service-hashes.json')
assert before_hashes['Invoices'] == after_hashes['Invoices']
source = saved / 'data/거래플랜.db'
db = sqlite3.connect(source.as_uri() + '?mode=ro', uri=True)
db.row_factory = sqlite3.Row
invoice_rows = {r['Id'].lower(): dict(r) for r in db.execute('SELECT * FROM Invoices WHERE IsDirty=1')}
db.close()
assert {r['id'].lower() for r in body['invoices']} == set(invoice_rows)
assert len(invoice_rows) == 2
original = Path.home() / 'AppData/Local/거래플랜/data/거래플랜.db'
with original.open('rb') as f:
    assert hashlib.file_digest(f, 'sha256').hexdigest() == '674e0022e2d7b3da5de18f499600e9f311d1da1406fa7dfd21c925574a7ead22'
result = dict(
    at=datetime.datetime.now().astimezone().isoformat(), status='passed-offline-capture-with-unrelated-pending-identified',
    appSha256=proof['appSha256'], listCounts={k: len(v) for k, v in body.items() if isinstance(v, list)},
    retries=9, uniqueRequestBodies=1, preservedExistingAssetSites=16, wireSitesMatchSavedAssets=17,
    unrelatedInvoicesAlreadyPendingBeforeRentalSave=2, invoiceTableUnchangedByRentalSave=True,
    sourceSnapshotUnchanged=True, runtimeFilesUnchanged=209, originalPcDbUnchanged=True,
    actualNetworkRequests=0, syntheticSession=True, syntheticCapability=True,
    authenticatedApiAcceptanceVerified=False, serverReplayRollbackVerified=False,
    fullSyncSuitableForSelectedRentalRecovery=False,
    nextGate='Use a reviewed selective recovery request; do not send this full request with two unresolved invoices',
    limits='Local transport attempts only. Nine identical bodies do not prove server idempotency. Other three routes were blocked.',
)
(out / 'analysis.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
state_path = Path('D:/DevCaches/goal-desktop-full-regression-20260921/state.json')
state = read(state_path)
state['rentalWireCapture20260926'] = result | {'evidence': str(out), 'goalComplete': False, 'consecutiveBlockedAuditTurns': 0}
state['updatedAt'] = result['at']
state['previousGoalTurnClassification'] = 'progress'
state_path.write_text(json.dumps(state, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(result, ensure_ascii=False))
