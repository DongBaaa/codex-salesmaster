from pathlib import Path
import collections
import datetime
import hashlib
import json
import sqlite3

out = Path.home() / 'Documents/Codex/tradeplan-rental-selected-wire-20260926'
capture = out / 'capture-01'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
proof = read(capture / 'verification.json')
assert proof['captureSucceeded'] and proof['Pushes'] == 3 and proof['uniqueBodies'] == 1
assert proof['BlockedOtherRequests'] == 0 and not proof['completed']
assert proof['actualNetworkRequests'] == 0 and proof['sourceSnapshotUnchanged'] and proof['runtimeUnchanged']
full = json.loads(read(out.parent / 'tradeplan-rental-wire-20260926/capture-01/request-001.private.json')['body'])
selected = read(out / 'selected-request.private.json')
assert selected['invoices'] == [] and len(full['invoices']) == 2
assert all(value == full[key] for key, value in selected.items() if key != 'invoices')
requests = [read(p) for p in sorted(capture.glob('request-*.private.json'))]
assert len(requests) == 3 and len({r['body'] for r in requests}) == 1
assert all(json.loads(r['body']) == selected for r in requests)


def tables(path):
    with sqlite3.connect(path.as_uri() + '?mode=ro', uri=True) as db:
        db.row_factory = sqlite3.Row
        assert db.execute('pragma quick_check').fetchone()[0] == 'ok'
        return {r[0]: [dict(row) for row in db.execute('SELECT * FROM "' + r[0] + '"')]
                for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}


before = tables(capture / 'after-login.db')
after = tables(capture / 'data/거래플랜.db')
assert set(before) == set(after) and len(before) == 39
bag = lambda rows: collections.Counter(tuple(sorted(row.items())) for row in rows)
assert all(bag(before[t]) == bag(after[t]) for t in before)
invoice_receipts = [r for r in after['SyncOutboxEntries'] if r['EntityName'] == 'LocalInvoice']
assert len(invoice_receipts) == 2 and all(r['Status'] == 'Failed' for r in invoice_receipts)
assert sum(r['IsDirty'] for r in after['Invoices']) == 2
with (Path.home() / 'AppData/Local/거래플랜/data/거래플랜.db').open('rb') as f:
    assert hashlib.file_digest(f, 'sha256').hexdigest() == '674e0022e2d7b3da5de18f499600e9f311d1da1406fa7dfd21c925574a7ead22'
result = dict(at=datetime.datetime.now().astimezone().isoformat(),
    status='passed-selected-offline-send-and-pending-preservation',
    actualCompiledSendBoundary='PushPreparedRequestAsync', sourceSnapshotUnchanged=True,
    requestRows=38, durableRentalRows=19, referenceOnlyRows=19, excludedInvoiceRows=2,
    exactRequestMatches=True, retries=3, uniqueBodies=1, actualNetworkRequests=0,
    afterLoginTablesUnchanged=39, unrelatedInvoiceRowsAndReceiptsPreserved=True,
    originalPcDbUnchanged=True, runtimeFilesUnchanged=209, syntheticSameOwnerSession=True,
    successfulAcknowledgementVerified=False, authenticatedApiAcceptanceVerified=False,
    serverReplayRollbackVerified=False, userFacingSelectiveSyncImplemented=False,
    limits='DTO selection is a diagnostic procedure, not a production repair. Only synthetic failed sends were observed.')
(out / 'analysis.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
state_path = Path('D:/DevCaches/goal-desktop-full-regression-20260921/state.json')
state = read(state_path)
state['rentalSelectedWire20260926'] = result | {'evidence': str(out), 'goalComplete': False, 'consecutiveBlockedAuditTurns': 0}
state['updatedAt'] = result['at']
state['previousGoalTurnClassification'] = 'progress'
state_path.write_text(json.dumps(state, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(result, ensure_ascii=False))
