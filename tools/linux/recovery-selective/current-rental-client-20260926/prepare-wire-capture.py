"""Prepare an offline-only request capture of the verified rental save copy."""
from pathlib import Path
import hashlib
import json

repo = Path('D:/거래플랜')
out = Path.home() / 'Documents/Codex/tradeplan-rental-wire-20260926'
source = Path.home() / 'Documents/Codex/tradeplan-current-rental-client-20260926-v3/service-fixed/data/거래플랜.db'
app_hash = '0438dbd7cfe433d4b6a88e2b9685293ca1f497e7e66502739b6e15c28bb806e2'
proof = json.loads((source.parent.parent / 'verification.json').read_text())
assert proof['passed'] and proof['appSha256'].lower() == app_hash
assert not out.exists(), 'Keep previous capture evidence'
source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
code = (repo / 'tools/linux/recovery-selective/current-capture/Program.cs').read_text(encoding='utf-8-sig')
start = code.index('const string root = ')
end = code.index('var output = ', start)
code = code[:start] + f'''const string root = @"{out}";
var source = @"{source}";
var sourceHash = "{source_hash}";
const string runtime = @"D:\\거래플랜\\Tests\\GeoraePlan.Desktop.App.Tests\\bin\\Release\\net8.0-windows";
if (args.Length != 1 || Path.GetFileName(args[0]) != args[0] || !args[0].StartsWith("capture-"))
    throw new ArgumentException("Fresh capture-* directory name required");
''' + code[end:]
start = code.index('var actor = ')
end = code.index('Directory.CreateDirectory(', start)
code = code[:start] + f'''if (Hash(appPath) != "{app_hash}") throw new InvalidOperationException("Verified rental fix required");
''' + code[end:]
start = code.index('var user = session.')
end = code.index('if ((string)app.GetType', start)
code = code[:start] + '''// Synthetic test-admin session; no authenticated actor or server access.
''' + code[end:]
code = code.replace('Current compiled 1.1.741 TrySync request capture only.', 'Rental save copy through actual compiled TrySync request capture only.')
out.mkdir()
(out / 'Program.cs').write_text(code, encoding='utf-8')
(out / 'CurrentCapture.csproj').write_bytes((repo / 'tools/linux/recovery-selective/current-capture/CurrentCapture.csproj').read_bytes())
(out / 'input.json').write_text(json.dumps(dict(source=str(source), sourceSha256=source_hash, appSha256=app_hash,
    purpose='Offline capture only; synthetic admin and capability; no server acceptance proof'), indent=2), encoding='utf-8')
print(json.dumps({'prepared': str(out), 'sourceBytes': source.stat().st_size, 'sourceSha256': source_hash}))
