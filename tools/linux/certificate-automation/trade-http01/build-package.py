from pathlib import Path
import hashlib,json
root=Path(__file__).parent
names=['renew-and-deploy.sh','native_deploy.py','native_api.py','transaction.py','install_support.py','install_trade.py','payload-hashes.json']
hashes={n:hashlib.sha256((root/n).read_bytes()).hexdigest() for n in names}
declared=json.loads((root/'payload-hashes.json').read_text())
assert declared=={n:hashes[n] for n in names[:4]}
lines=['#!/bin/sh','set -eu','umask 077','PATH=/usr/syno/bin:/usr/syno/sbin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin','export PATH','test "$(id -u)" = 0','source_dir=/volume1/homes/boss/trade-http01-runtime-20260911','launch_base=/volume1/workplan-native-pilot-launch','test -d "$launch_base" && test ! -L "$launch_base"','test "$(readlink -f "$launch_base")" = "$launch_base"','test "$(stat -c %u "$launch_base")" = 0','test "$(stat -c %a "$launch_base")" = 700','run=$(mktemp -d "$launch_base/install-trade-XXXXXX")']
for n,h in hashes.items():
    lines+=['test -f "$source_dir/'+n+'" && test ! -L "$source_dir/'+n+'"','cp "$source_dir/'+n+'" "$run/'+n+'"','chmod 600 "$run/'+n+'"','test "$(sha256sum "$run/'+n+'" | awk \'{print $1}\')" = '+h]
lines+=['exec env -i PATH="$PATH" LC_ALL=C python3 -I -B "$run/install_trade.py" --install']
(root/'dsm-command.sh').write_text('\n'.join(lines)+'\n',encoding='utf-8',newline='\n')
hashes['dsm-command.sh']=hashlib.sha256((root/'dsm-command.sh').read_bytes()).hexdigest()
(root/'package-manifest.json').write_text(json.dumps(hashes,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'files':len(hashes),'nasUploaded':False}))
