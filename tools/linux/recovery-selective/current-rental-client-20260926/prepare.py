"""Prepare current six-table rental input for the real desktop save service.

Never sync this lab fixture or replace the original user DB with it. The full
PostgreSQL restore is a prerequisite; this local service test is a next stage,
not a substitute for the still-required isolated API recovery and replay.
"""
from pathlib import Path
import datetime, hashlib, json, shlex, sqlite3, subprocess, uuid

repo=Path(r'D:\거래플랜')
out=Path(r'C:\Users\beene\Documents\Codex\tradeplan-current-rental-client-20260926-v3')
rest=Path(r'C:\Users\beene\Documents\Codex\tradeplan-full-restore-20260926')
source=Path(r'C:\Users\beene\AppData\Local\거래플랜\data\거래플랜.db')
app=repo/'테스트 시행/실행환경/App/거래플랜.Desktop.App.dll'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
assert not out.exists()
assert sha(source)=='674e0022e2d7b3da5de18f499600e9f311d1da1406fa7dfd21c925574a7ead22'
assert sha(app)=='b2199132fb74b7330712aa6a3802782c1d445cf5594b2c9d947b04e20dd718f3'
s=read(rest/'state.json')
assert s['status']=='passed-full-db-restore' and s['productionBusinessHashesUnchanged'] and s['volumeRemoved'] and not s['cleanupErrors']
hashes=read(rest/'restored-before-hashes.json')['georaeplan_usenet']
tables=read(rest/'restored-rental-rows.private.json')['georaeplan_usenet']
q=["BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY; SET LOCAL statement_timeout='30s'; SET LOCAL lock_timeout='1s'; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL bytea_output='hex'; SET LOCAL extra_float_digits=1;"]
for t in ('Items','RentalManagementCompanies'):
    q.append("SELECT json_build_object('table','"+t+"','count',count(*),'hash',md5(coalesce(string_agg(h,'' ORDER BY h COLLATE \"C\"),''))) FROM (SELECT md5(row_to_json(r)::text) h FROM \""+t+'\" r) q;')
    q.append("SELECT jsonb_build_object('table','"+t+"','rows',coalesce(jsonb_agg(to_jsonb(r)),'[]')) FROM \""+t+'\" r;')
q.append('ROLLBACK;')
args=['docker','exec','-i','georaeplan-postgres-1','sh','-c','exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d georaeplan_usenet']
run=subprocess.run(['ssh','-o','BatchMode=yes','georaeplan-linux',shlex.join(args)],input='\n'.join(q).encode(),capture_output=True,timeout=45)
assert run.returncode==0,'Read-only reference capture failed'
for e in map(json.loads,run.stdout.decode().splitlines()):
    if 'hash' in e:assert e==hashes[e['table']], 'Current reference differs from restored snapshot'
    else:tables[e['table']]=e['rows']
assert len(tables)==6
out.mkdir()
(out/'reference-tables.private.json').write_bytes(run.stdout)
dst=out/'fixture.db'
src=sqlite3.connect(source.as_uri()+'?mode=ro',uri=True)
db=sqlite3.connect(dst);src.backup(db);src.close()
def convert(name,value):
    if isinstance(value,bool):return int(value)
    if isinstance(value,(list,dict)):return json.dumps(value,ensure_ascii=False,separators=(',',':'))
    if isinstance(value,str):
        try:return str(uuid.UUID(value)).upper()
        except ValueError:pass
        if name.endswith('AtUtc'):
            return datetime.datetime.fromisoformat(value).astimezone(datetime.timezone.utc).replace(tzinfo=None).isoformat(' ')
    return value
with db:
    for table,rows in tables.items():
        cols=list(db.execute('PRAGMA table_info("'+table+'")'))
        col_names={x[1] for x in cols}
        db.execute('DELETE FROM "'+table+'"')
        if table=='RentalManagementCompanies':
            # Canonicalization must run through the real compiled client, not
            # via Python filtering or by dropping the local unique index.
            continue
        for row in rows:
            values={k:convert(k,v) for k,v in row.items() if k in col_names}
            values['IsDirty']=0
            if 'CatalogExtensionSyncPending' in col_names:values['CatalogExtensionSyncPending']=0
            missing=[x[1] for x in cols if x[3] and x[4] is None and x[1] not in values]
            assert not missing,(table,missing)
            names=list(values)
            db.execute('INSERT INTO "'+table+'" ('+','.join('"'+n+'"' for n in names)+') VALUES ('+','.join('?' for _ in names)+')',[values[n] for n in names])
    # These transport receipts belong to the protected original PC, not to a
    # fresh server-derived lab profile. Never change them in the original DB.
    db.execute('DELETE FROM SyncOutboxEntries')
assert db.execute('pragma integrity_check').fetchone()[0]=='ok'
db.close()
assert sha(source)=='674e0022e2d7b3da5de18f499600e9f311d1da1406fa7dfd21c925574a7ead22'
manifest={'snapshotPath':str(dst),'snapshotSha256':sha(dst),'sourceSha256':sha(source),'appSha256':sha(app),'fullRestoreStateSha256':sha(rest/'state.json'),'sixServerTableCounts':{t:len(rows) for t,rows in tables.items()},'operationalWrites':0,'originalPendingDataUnchanged':True,'limits':'All six rental tables match the fresh server snapshot; ancillary local tables remain from protected original copy. No network/sync configured. Lab-only clean flags and empty receipts are not a recovery of original pending work.'}
(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
(out/'companies.private.json').write_text(json.dumps(tables['RentalManagementCompanies'],ensure_ascii=False),encoding='utf-8')
code=(repo/'tools/linux/recovery-selective/rental-reconcile-probe/Program.cs').read_text(encoding='utf-8-sig')
code='using System.Data.Common;\n'+code
code=code.replace('C:/DevCaches/trade-confirmed-rentals-20260917/'.replace('/',chr(92)),str(out)+chr(92))
start=code.index('var tests = ')
end=code.index('var actorPath = ',start)
code=code[:start]+'''var sessionType = app.GetType("거래플랜.Desktop.App.Services.SessionState", true)!;
var session = Activator.CreateInstance(sessionType)!;
var offline = sessionType.GetMethod("SetOfflineSession")!;
var actor = Activator.CreateInstance(offline.GetParameters()[0].ParameterType)!;
foreach (var (field, value) in new[] { ("Username", "isolated-rental-recovery"), ("Role", "admin"), ("TenantCode", "USENET_GROUP"), ("OfficeCode", "USENET"), ("ScopeType", "Admin") })
    actor.GetType().GetProperty(field)!.SetValue(actor, value);
offline.Invoke(session, [actor]);
'''+code[end:]
code=code.replace('var actor = actorFile.RootElement;', 'var observedActor = actorFile.RootElement;').replace('actor.GetProperty(', 'observedActor.GetProperty(')
code=code.replace('var serverFixture = args.Length == 4;', 'if (args.Length != 4) throw new InvalidOperationException("Current server fixture required");\nvar serverFixture = true;')
code=code.replace('var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);','if (Hash(appPath).ToLowerInvariant() != "'+sha(app)+'") throw new InvalidOperationException("Official app changed");\nvar app = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);')
anchor='var outcomes = new List<object>();'
assert code.count(anchor)==1
code=code.replace(anchor,anchor+'''
void Capture(string stage)
{
    var sqlType = Assembly.Load("Microsoft.Data.Sqlite").GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
    using var connection = (DbConnection)Activator.CreateInstance(sqlType, "Data Source="+dbPath+";Mode=ReadOnly")!;
    connection.Open();
    var names = new List<string>();
    using (var cmd = connection.CreateCommand()) {
        cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader=cmd.ExecuteReader(); while(reader.Read())names.Add(reader.GetString(0));
    }
    var hashes=new Dictionary<string,object>();
    var selected=new Dictionary<string,object>();
    foreach(var name in names) {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[A-Za-z0-9_]+$"))throw new Exception("Unexpected table name");
        using var cmd=connection.CreateCommand();cmd.CommandText="SELECT * FROM "+name;
        using var reader=cmd.ExecuteReader();var rows=new List<Dictionary<string,object?>>();
        while(reader.Read()) {
            var row=new Dictionary<string,object?>();
            for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        var encoded=rows.Select(r=>JsonSerializer.Serialize(r)).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        hashes[name]=new { count=rows.Count, sha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\\n",encoded)))) };
        if(new[]{"RentalAssets","RentalBillingProfiles","RentalAssetAssignmentHistories","RentalManagementCompanies"}.Contains(name))selected[name]=rows;
    }
    File.WriteAllText(Path.Combine(output,stage+"-hashes.json"),JsonSerializer.Serialize(hashes));
    File.WriteAllText(Path.Combine(output,stage+"-rows.private.json"),JsonSerializer.Serialize(selected));
}
Capture("before-initializer");
var initializer = app.GetType("거래플랜.Desktop.App.Data.LocalDbInitializer", true)!;
await ((Task)initializer.GetMethod("InitializeAsync")!.Invoke(null,[db])!).WaitAsync(TimeSpan.FromSeconds(120));
Clear();
// Invoke the real canonical company importer before saving rental data. No
// HTTP client, dispatcher, timer, Start or sync run is supplied or invoked.
var syncType = app.GetType("거래플랜.Desktop.App.Services.SyncService", true)!;
var syncConstructor = syncType.GetConstructors().Single();
var constructorArgs = new object?[syncConstructor.GetParameters().Length];
constructorArgs[0] = db; constructorArgs[2] = service; constructorArgs[4] = session;
var canonical = syncConstructor.Invoke(constructorArgs);
var import = syncType.GetMethod("UpsertPulledRentalManagementCompaniesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
var dtoType = import.GetParameters()[0].ParameterType.GetGenericArguments()[0];
var listType = typeof(List<>).MakeGenericType(dtoType);
var companies = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Path.GetDirectoryName(snapshot)!, "companies.private.json")), listType)!;
await ((Task)import.Invoke(canonical, [companies, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(30));
Clear();
outcomes.Add(new { stage = "compiled-company-canonical-import", success = true });
Capture("before-service");
''')
code=code.replace('    await ((IAsyncDisposable)db).DisposeAsync();','    Capture("after-service");\n    await ((IAsyncDisposable)db).DisposeAsync();')
(out/'Program.cs').write_text(code,encoding='utf-8')
(out/'RentalReconcileProbe.csproj').write_bytes((repo/'tools/linux/recovery-selective/rental-reconcile-probe/RentalReconcileProbe.csproj').read_bytes())
print(json.dumps({'prepared':True,'tables':manifest['sixServerTableCounts'],'originalPendingDataUnchanged':True,'operationalWrites':0}))
