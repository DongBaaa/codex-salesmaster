"""Exercise the actual preparation helper against pipe-sized stderr/stdout."""
from pathlib import Path
import json, shutil, subprocess, sys

assert sys.argv[1:] in ([], ['--expect-deadlock'])
expect_deadlock=bool(sys.argv[1:])
root=Path(r'D:\DevCaches\goal-invoice-recovery-20260921\preparation-output-regression')/('before' if expect_deadlock else 'after')
assert not root.exists()
root.mkdir(parents=True)
fixture=root/'fixture.ps1'
fixture.write_text("[Console]::Error.Write(('e' * 262144) + 'STDERR_END')\n[Console]::Out.Write(('o' * 131072) + 'STDOUT_END')\nexit 7\n",encoding='utf-8-sig')
pwsh=shutil.which('pwsh'); assert pwsh
quote=lambda x:"'"+str(x).replace("'","''")+"'"
driver=root/'driver.ps1'
driver.write_text(r'''
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile('D:\거래플랜\테스트 시행\테스트-환경-준비.ps1',[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Preparation parse failed'}
$nodes=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-DotnetWithOutput'},$true))
if($nodes.Count -ne 1){throw 'Expected unique original helper'}
Invoke-Expression $nodes[0].Extent.Text
'''+'\n$r=Invoke-DotnetWithOutput -DotnetExe '+quote(pwsh)+' -Arguments @(\'-NoProfile\',\'-File\','+quote(fixture)+')\n'+r'''
[pscustomobject]@{ExitCode=$r.ExitCode;OutputLines=$r.Output.Count;StdoutExact=$r.Output[0] -ceq (('o'*131072)+'STDOUT_END');StderrExact=$r.Output[1] -ceq (('e'*262144)+'STDERR_END')} | ConvertTo-Json -Compress
''',encoding='utf-8-sig')
p=subprocess.Popen([pwsh,'-NoProfile','-File',str(driver)],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
timed_out=False
try: out,err=p.communicate(timeout=10)
except subprocess.TimeoutExpired:
    timed_out=True
    # Only this newly-created fixture process tree, never a discovered process.
    stopped=subprocess.run(['taskkill','/PID',str(p.pid),'/T','/F'],capture_output=True,timeout=10)
    out,err=p.communicate(timeout=10)
    assert stopped.returncode==0
result={'expectedDeadlock':expect_deadlock,'timedOut':timed_out,'fixtureProcessEnded':p.poll() is not None}
if not timed_out:
    assert p.returncode==0,err.decode(errors='replace')[-500:]
    result.update(json.loads(out.decode('utf-8-sig')))
    assert result['ExitCode']==7 and result['OutputLines']==2 and result['StdoutExact'] and result['StderrExact']
(root/'result.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
assert timed_out==expect_deadlock
print(json.dumps(result))
