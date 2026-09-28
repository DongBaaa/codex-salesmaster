"""Exercise the official Git-only release entry point in disposable Git repositories.

No real repository, remote, build, installer or service is changed. Fixtures and logs
are retained under --output-root for inspection. Run once per PowerShell host.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def git(root, *args):
    p = subprocess.run(['git', '-C', str(root), *args], capture_output=True,
                       text=True, encoding='utf-8', check=True)
    return p.stdout.strip()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--powershell', required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    args = parser.parse_args()
    output = args.output_root.resolve()
    output.mkdir(parents=True, exist_ok=False)
    runner = output/'run.ps1'
    runner.write_text('''param([string]$CasePath)
$ErrorActionPreference='Stop'
$data=Get-Content -LiteralPath $CasePath -Raw -Encoding UTF8|ConvertFrom-Json
$settings=@{ProjectRoot=$data.repo;ChecklistPath=$data.checklist;ChangedFilesPath=$data.changed;LogRoot=$data.logs;CommitMessage='isolated verification';SkipLinuxPc=$true;SkipPush=$true}
if($data.tracked.Count){$settings.IncludeTrackedPaths=[string[]]$data.tracked}
if($data.untracked.Count){$settings.IncludeUntrackedPaths=[string[]]$data.untracked}
if($data.dryRun){$settings.DryRun=$true}
try {
 if($data.changeAfterPlan){
  $t=$null;$e=$null
  $ast=[Management.Automation.Language.Parser]::ParseFile($data.source,[ref]$t,[ref]$e)
  foreach($f in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){. ([scriptblock]::Create($f.Extent.Text))}
  $plan=Get-ReleaseGitPlan -ProjectRoot $data.repo -IncludeTrackedPaths @('a.txt')
  [IO.File]::WriteAllText((Join-Path $data.repo 'a.txt'),'changed after planning')
  Add-ReleaseGitPlan -ProjectRoot $data.repo -Plan $plan -IncludeTrackedPaths @('a.txt')
 } else { & $data.source @settings }
} catch { Write-Output $_.Exception.Message; exit 1 }
''', encoding='utf-8-sig')
    results = []
    cases = ['unapproved_tracked', 'partial_selection', 'preexisting_staged',
             'partially_staged', 'unapproved_untracked', 'approved_tracked',
             'approved_new', 'literal_unicode_path', 'approved_delete',
             'approved_rename', 'dry_run', 'directory_not_file_approval', 'changed_after_plan']
    success_cases = {'approved_tracked', 'approved_new', 'literal_unicode_path',
                     'approved_delete', 'approved_rename', 'dry_run'}
    for case in cases:
        base = output/case
        repo = base/'repo'
        repo.mkdir(parents=True)
        git(repo, 'init', '-q', '-b', 'fixture')
        for key, value in [('user.name', 'Fixture'), ('user.email', 'fixture@example.invalid'),
                           ('commit.gpgsign', 'false'), ('core.autocrlf', 'false'),
                           ('core.quotepath', 'false'), ('core.hooksPath', str(base/'no-hooks'))]:
            git(repo, 'config', key, value)
        for name in ['a.txt', 'b.txt', '문서 [1].txt', '문서 1.txt']:
            (repo/name).write_text('original\n', encoding='utf-8')
        git(repo, 'add', '.')
        git(repo, 'commit', '-qm', 'fixture baseline')
        (repo/'a.txt').write_text('changed\n')
        tracked, untracked, expected_paths = [], [], ['a.txt']
        if case in success_cases:
            tracked = ['a.txt']
        if case == 'partial_selection':
            tracked = ['a.txt']
            (repo/'b.txt').write_text('unapproved\n')
        if case in ('preexisting_staged', 'partially_staged'):
            tracked = ['a.txt']
            git(repo, 'add', 'a.txt')
            if case == 'partially_staged':
                (repo/'a.txt').write_text('later unstaged work\n')
        if case in ('unapproved_untracked', 'approved_new'):
            tracked = ['a.txt']
            (repo/'new [2].txt').write_text('new file\n')
            if case == 'approved_new':
                untracked = ['new [2].txt']
                expected_paths.append('new [2].txt')
        if case == 'literal_unicode_path':
            (repo/'a.txt').write_text('original\n')
            (repo/'문서 [1].txt').write_text('literal bracket path\n')
            tracked = expected_paths = ['문서 [1].txt']
        if case == 'approved_delete':
            (repo/'a.txt').unlink()
        if case == 'approved_rename':
            (repo/'a.txt').rename(repo/'renamed.txt')
            untracked = ['renamed.txt']
            expected_paths.append('renamed.txt')
        if case == 'directory_not_file_approval':
            tracked = ['.']
        checklist = base/'checklist.md'
        checklist.write_text('- [x] 문제 없음 → Git 반영 가능\n', encoding='utf-8-sig')
        changed = base/'changed.md'
        changed.write_text('fixture review only\n', encoding='utf-8-sig')
        config = {'repo': str(repo), 'checklist': str(checklist), 'changed': str(changed),
                  'logs': str(base/'logs'), 'source': str(args.source.resolve()),
                  'tracked': tracked, 'untracked': untracked, 'dryRun': case == 'dry_run',
                  'changeAfterPlan': case == 'changed_after_plan'}
        casefile = base/'case.json'
        casefile.write_text(json.dumps(config), encoding='utf-8')
        before_head = git(repo, 'rev-parse', 'HEAD')
        before_index = digest(repo/'.git/index')
        before_work = {str(p.relative_to(repo)): digest(p) for p in repo.glob('*') if p.is_file()}
        p = subprocess.run([args.powershell, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                            '-File', str(runner), '-CasePath', str(casefile)], capture_output=True, timeout=40)
        (base/'stdout.txt').write_bytes(p.stdout)
        (base/'stderr.txt').write_bytes(p.stderr)
        after_head = git(repo, 'rev-parse', 'HEAD')
        after_index = digest(repo/'.git/index')
        after_work = {str(p.relative_to(repo)): digest(p) for p in repo.glob('*') if p.is_file()}
        checks = {'exit': (p.returncode == 0) == (case in success_cases),
                  'worktreePreserved': before_work == after_work}
        if case == 'changed_after_plan':
            checks['worktreePreserved'] = (repo/'a.txt').read_text() == 'changed after planning'
        if case not in success_cases or case == 'dry_run':
            checks.update(headPreserved=before_head == after_head, indexPreserved=before_index == after_index)
        else:
            actual = git(repo, 'diff-tree', '--no-commit-id', '--name-only', '--no-renames', '-r', 'HEAD').splitlines()
            checks.update(committedExactly=set(actual) == set(expected_paths),
                          clean=not git(repo, 'status', '--porcelain'),
                          headChanged=before_head != after_head)
        results.append({'case': case, 'exit': p.returncode, 'checks': checks, 'passed': all(checks.values())})
    report = {'sourceSha256': digest(args.source), 'powershell': args.powershell,
              'passed': sum(x['passed'] for x in results), 'total': len(results), 'cases': results,
              'realRepositoryWrites': 0, 'liveOperations': 0}
    (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))
    if report['passed'] != report['total']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
