"""Prepare a selected request through the compiled client's send boundary.

The source retains all pending rows/receipts. Only the outgoing DTO is selected.
This capture has no network transport and no successful response simulation.
"""
from pathlib import Path
import hashlib
import json
import sqlite3

base = Path.home() / 'Documents/Codex/tradeplan-rental-wire-20260926'
out = Path.home() / 'Documents/Codex/tradeplan-rental-selected-wire-20260926'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
proof = read(base / 'analysis.json')
assert proof['unrelatedInvoicesAlreadyPendingBeforeRentalSave'] == 2
assert proof['retries'] == 9 and proof['actualNetworkRequests'] == 0
assert not out.exists()
source = base / 'capture-01/data/거래플랜.db'
with sqlite3.connect(source.as_uri() + '?mode=ro', uri=True) as db:
    owners = [r[0] for r in db.execute('SELECT DISTINCT UserId FROM SyncOutboxEntries')]
    assert len(owners) == 1
    receipts = {r[0].lower() for r in db.execute('SELECT MutationId FROM SyncOutboxEntries')}
    for table in ('Customers', 'Items', 'RentalManagementCompanies'):
        assert db.execute('SELECT count(*) FROM ' + table + ' WHERE IsDirty=1').fetchone()[0] == 0
wire = read(base / 'capture-01/request-001.private.json')
full = json.loads(wire['body'])
assert len(full['invoices']) == 2
selected = json.loads(wire['body'])
selected['invoices'] = []
assert sum(len(v) for v in selected.values() if isinstance(v, list)) == 38
durable = ['rentalAssets', 'rentalBillingProfiles', 'rentalAssetAssignmentHistories']
dependencies = {'customers': 'Customer', 'items': 'Item', 'rentalManagementCompanies': 'RentalManagementCompany'}
assert all(r['mutationId'].lower() in receipts for g in durable for r in selected[g])
assert all(r['mutationId'].lower() not in receipts for g in dependencies for r in selected[g])
assert sum(len(selected[g]) for g in durable) == sum(len(selected[g]) for g in dependencies) == 19

code = (base / 'Program.cs').read_text(encoding='utf-8-sig')
code = 'using System.Data.Common;\n' + code
code = code.replace(str(base), str(out))
start = code.index('var source = ')
end = code.index('const string runtime', start)
code = code[:start] + f'var source = @"{source}";\nvar sourceHash = "{sha(source)}";\n' + code[end:]
anchor = '// Synthetic test-admin session; no authenticated actor or server access.'
code = code.replace(anchor, anchor + f'''
var user = session.GetType().GetProperty("User")!.GetValue(session)!;
user.GetType().GetProperty("UserId")!.SetValue(user, Guid.Parse("{owners[0]}"));
''')
start = code.index('    completed = await ((Task<bool>)')
end = code.index('\n}\nfinally', start)
replacement = '''    var sqliteType = Assembly.Load("Microsoft.Data.Sqlite").GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
    using (var connection = (DbConnection)Activator.CreateInstance(sqliteType, "Data Source="+dbPath)!) {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$path";
        parameter.Value = Path.Combine(output, "after-login.db"); command.Parameters.Add(parameter); command.ExecuteNonQuery();
    }
    var method = sync.GetType().GetMethod("PushPreparedRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var selectedText = File.ReadAllText(Path.Combine(root, "selected-request.private.json"));
    var dto = JsonSerializer.Deserialize(selectedText, method.GetParameters()[2].ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var keyType = sync.GetType().GetNestedType("SyncEntityKey", BindingFlags.NonPublic)!;
    var keySetType = typeof(HashSet<>).MakeGenericType(keyType);
    var keys = Activator.CreateInstance(keySetType)!;
    using var selectedDoc = JsonDocument.Parse(selectedText);
    foreach (var (group, entity) in new[] { ("customers", "Customer"), ("items", "Item"), ("rentalManagementCompanies", "RentalManagementCompany") })
        foreach (var row in selectedDoc.RootElement.GetProperty(group).EnumerateArray())
            keySetType.GetMethod("Add")!.Invoke(keys, [Activator.CreateInstance(keyType, entity, row.GetProperty("id").GetGuid())]);
    var api = sync.GetType().GetField("_api", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sync)!;
    try {
        await ((Task)method.Invoke(sync, [api, session, dto, null, keys, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(90));
        completed = true;
    } catch (HttpRequestException) when (network.Pushes > 0) {
        // Expected synthetic 503. A scope/receipt/preflight error is not success.
    }
'''
code = code[:start] + replacement + code[end:]
code = code.replace('simulatedCapabilityResponse = true', 'simulatedCapabilityResponse = false')
code = code.replace('Rental save copy through actual compiled TrySync request capture only. Simulated session fields and capability response. No HTTP success, recovery or full UI proof.', 'Selected DTO through compiled PushPreparedRequestAsync; synthetic same-owner session; local 503; no actual server acceptance or user-facing selective-sync feature is inferred.')
out.mkdir()
(out / 'Program.cs').write_text(code, encoding='utf-8')
(out / 'CurrentCapture.csproj').write_bytes((base / 'CurrentCapture.csproj').read_bytes())
(out / 'selected-request.private.json').write_text(json.dumps(selected, ensure_ascii=False), encoding='utf-8')
(out / 'input.json').write_text(json.dumps({'source': str(source), 'sourceSha256': sha(source), 'fullRequestSha256': sha(base / 'capture-01/request-001.private.json'), 'durableRows': 19, 'dependencyRows': 19, 'excludedInvoiceRows': 2, 'mutatedSource': False}, indent=2), encoding='utf-8')
print(json.dumps({'prepared': True, 'durableRows': 19, 'dependencyRows': 19, 'excludedInvoiceRows': 2}))
