"""Run the real release script with real file copies and fake service commands on Linux.

No Docker daemon or HTTP server is used. All writes stay in an owned temporary tree.
Usage: python3 Test-GeoraePlanReleaseRollback.py --script apply-release.sh --output result.json
"""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import threading
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


MOCK = r'''#!/usr/bin/env python3
import os, pathlib, signal, subprocess, sys, time
r=pathlib.Path(os.environ['FIXTURE_ROOT'])
case=os.environ['FIXTURE_CASE']
cmd=pathlib.Path(sys.argv[0]).name
args=sys.argv[1:]
with (r/'trace.jsonl').open('a') as f:
 import json
 f.write(json.dumps({'cmd':cmd,'args':args})+'\n')
counter=r/(cmd+'.count')
n=int(counter.read_text())+1 if counter.exists() else 1
counter.write_text(str(n))
if cmd=='rsync':
 if case=='copy_failure' and n==1:
  (r/'app/live/marker').write_text('partial')
  (r/'app/live/partial-only').write_text('partial')
  sys.exit(41)
 if case=='rollback_copy_failure' and n==2: sys.exit(43)
 code=subprocess.call([os.environ['REAL_RSYNC']]+args)
 if case in ('terminate','interrupt') and n==1:
  os.kill(os.getppid(),signal.SIGTERM if case=='terminate' else signal.SIGINT)
 sys.exit(code)
if cmd=='cp':
 if case=='backup_failure': sys.exit(44)
 sys.exit(subprocess.call([os.environ['REAL_CP']]+args))
if cmd=='docker':
 if args==['info']: sys.exit(1 if case=='docker_group_fallback' else 0)
 # Every real mutation must remain restricted to this compose project and API.
 assert args==['compose','--env-file',str(r/'ops/.env'),'-p','georaeplan','-f',str(r/'ops/docker-compose.yml'),'up','-d','--no-deps','--force-recreate','api'], args
 c=r/'compose.count'; count=int(c.read_text())+1 if c.exists() else 1; c.write_text(str(count))
 if case=='api_failure' and count==1: sys.exit(42)
 if case=='rollback_api_failure' and count==2: sys.exit(42)
 sys.exit(0)
if cmd=='sg':
 assert args[0]=='docker' and args[1]=='-c'
 assert '/srv/georaeplan' not in args[2]
 assert args[2].endswith('up -d --no-deps --force-recreate api')
 sys.exit(0)
if cmd=='curl':
 assert args[-1] in ('http://127.0.0.1:18082/healthz','http://127.0.0.1:18082/readyz')
 current=(r/'app/live/marker').read_text()
 maximum=int(args[args.index('--max-time')+1]);connect=int(args[args.index('--connect-timeout')+1])
 assert 0 < connect <= maximum <= 10
 if current=='new' and case in ('slow_health_deadline','late_http_200'):
  time.sleep(maximum)
  if case=='late_http_200': print('200',end='');sys.exit(0)
  sys.exit(28)
 if current=='new' and case=='slow_ready_deadline':
  if args[-1].endswith('/healthz'): time.sleep(2.2)
  else: time.sleep(maximum);sys.exit(28)
 if case=='rollback_slow_deadline':
  if current=='old': time.sleep(maximum)
  sys.exit(28)
 if case=='rollback_health_failure': sys.exit(22)
 if current=='new' and case in ('health_failure','rollback_copy_failure','rollback_api_failure'): sys.exit(22)
 if current=='new' and case=='readiness_failure' and args[-1].endswith('/readyz'): sys.exit(22)
 if current=='new' and case=='readiness_redirect' and args[-1].endswith('/readyz'):
  print('302',end=''); sys.exit(0)
 print('200',end='')
 sys.exit(0)
if cmd=='sleep': sys.exit(0)
raise RuntimeError('unexpected mock command')
'''


def tree_digest(path):
    return {str(p.relative_to(path)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in path.rglob('*') if p.is_file()}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--script', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--cutover', type=Path)
    args = parser.parse_args()
    source = args.script.read_text(encoding='utf-8-sig')
    required = {name: shutil.which(name) for name in ('bash', 'rsync', 'cp', 'flock', 'curl')}
    if not all(required.values()):
        raise RuntimeError('Linux bash/rsync/cp/flock are required')
    subprocess.run([required['bash'], '-n', str(args.script)], check=True)
    capabilities = subprocess.run([required['bash'], str(args.script), '--capabilities'], text=True, capture_output=True, check=True)
    assert capabilities.stdout.strip() == 'georaeplan-release-health-deadlines-v1'
    cases = {
        'success': (0, False), 'copy_failure': (30, True),
        'api_failure': (30, True), 'health_failure': (30, True),
        'readiness_failure': (30, True), 'readiness_redirect': (30, True), 'terminate': (30, True),
        'interrupt': (30, True), 'rollback_copy_failure': (31, False),
        'rollback_api_failure': (31, False), 'rollback_health_failure': (31, False),
        'backup_failure': (44, False), 'missing_release_file': (10, False),
        'invalid_release_id': (2, False), 'lock_contention': (75, False),
        'docker_group_fallback': (0, False), 'invalid_timeout': (4, False),
        'invalid_leading_zero_timeout': (4, False), 'invalid_large_timeout': (4, False),
        'invalid_negative_timeout': (4, False), 'invalid_rollback_timeout': (4, False),
        'slow_health_deadline': (30, True), 'slow_ready_deadline': (30, True),
        'late_http_200': (30, True), 'rollback_slow_deadline': (31, False),
        'real_http_deadline': (30, True),
        'invalid_legacy_retries': (4, False),
    }
    results = []
    with tempfile.TemporaryDirectory(prefix='georaeplan-release-verification-') as owned:
        for case, (expected_exit, recovered) in cases.items():
            root = Path(owned) / case
            for path in ('bin', 'ops', 'app/live', 'releases/candidate/updates/manifest'):
                (root/path).mkdir(parents=True)
            for name in ('.env', 'docker-compose.yml'):
                (root/'ops'/name).write_text('fixture only\n')
            for name in ('거래플랜.Server.Api.dll', 'appsettings.json', 'release-info.txt', 'updates/manifest/stable.json'):
                (root/'releases/candidate'/name).write_text('fixture only\n')
            (root/'releases/candidate/marker').write_text('new')
            (root/'releases/candidate/new-only').write_text('new')
            (root/'app/live/marker').write_text('old')
            (root/'app/live/old-only').write_text('old')
            # Real rsync quick-check would skip these different same-size files.
            for marker in (root/'app/live/marker', root/'releases/candidate/marker'):
                os.utime(marker, (1_700_000_000, 1_700_000_000))
            before = tree_digest(root/'app/live')
            if case == 'missing_release_file':
                (root/'releases/candidate/appsettings.json').unlink()
            script = root/'apply.sh'
            server = None
            http_requests = []
            current_source = source.replace('/srv/georaeplan', str(root))
            if case == 'real_http_deadline':
                class Handler(BaseHTTPRequestHandler):
                    def do_GET(self):
                        marker = (root/'app/live/marker').read_text()
                        http_requests.append({'marker':marker, 'path':self.path})
                        if marker == 'new': time.sleep(3)
                        try:
                            self.send_response(200)
                            self.end_headers()
                            self.wfile.write(b'ok')
                        except (BrokenPipeError, ConnectionResetError):
                            pass
                    def log_message(self, *args): pass
                server = ThreadingHTTPServer(('127.0.0.1',0), Handler)
                threading.Thread(target=server.serve_forever, daemon=True).start()
                current_source = current_source.replace('127.0.0.1:18082', '127.0.0.1:'+str(server.server_port))
            # No production-path override is provided by the deployable script itself.
            script.write_text(current_source)
            for name in ('rsync', 'cp', 'docker', 'sg', 'curl', 'sleep'):
                if case == 'real_http_deadline' and name == 'curl': continue
                p = root/'bin'/name
                p.write_text(MOCK)
                p.chmod(0o700)
            env = dict(os.environ, PATH=str(root/'bin')+os.pathsep+os.environ['PATH'],
                       FIXTURE_ROOT=str(root), FIXTURE_CASE=case,
                       REAL_RSYNC=required['rsync'], REAL_CP=required['cp'],
                       HEALTH_CHECK_TIMEOUT_SECONDS='3' if case=='slow_ready_deadline' else '1',
                       ROLLBACK_HEALTH_TIMEOUT_SECONDS='2')
            invalid_values = {'invalid_timeout':'0', 'invalid_leading_zero_timeout':'08',
                              'invalid_large_timeout':'3601', 'invalid_negative_timeout':'-1'}
            if case in invalid_values: env['HEALTH_CHECK_TIMEOUT_SECONDS']=invalid_values[case]
            if case=='invalid_rollback_timeout': env['ROLLBACK_HEALTH_TIMEOUT_SECONDS']='3601'
            env.pop('HEALTH_CHECK_RETRIES', None)
            if case=='invalid_legacy_retries': env['HEALTH_CHECK_RETRIES']='900'
            lock = None
            if case == 'lock_contention':
                lock = (root/'ops/.apply-release.lock').open('w')
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                started = time.monotonic()
                process = subprocess.run([required['bash'], str(script), '..' if case == 'invalid_release_id' else 'candidate'],
                                         env=env, text=True, capture_output=True, timeout=25)
                elapsed = time.monotonic() - started
            finally:
                if lock:
                    lock.close()
                if server:
                    server.shutdown()
                    server.server_close()
            tracepath = root/'trace.jsonl'
            trace = [json.loads(line) for line in tracepath.read_text().splitlines()] if tracepath.exists() else []
            backups = list((root/'app/backups').glob('live-*'))
            checks = {'exit': process.returncode == expected_exit,
                      'recoveredMarker': ('rollback_done' in process.stderr) == recovered,
                      'noFalseSuccess': ('apply_release_done' in process.stdout) == (expected_exit == 0)}
            if expected_exit in (0, 30, 31):
                checks['intactBackup'] = len(backups) == 1 and tree_digest(backups[0]) == before
            preflight_failure = case in ('backup_failure', 'missing_release_file', 'invalid_release_id', 'lock_contention') or case.startswith('invalid_')
            if recovered or preflight_failure:
                checks['liveEqualsBefore'] = tree_digest(root/'app/live') == before
            if expected_exit == 0:
                checks['liveEqualsCandidate'] = tree_digest(root/'app/live') == tree_digest(root/'releases/candidate')
                checks['readinessChecked'] = any(t['cmd']=='curl' and t['args'][-1].endswith('/readyz') for t in trace)
            if preflight_failure:
                checks['noDeployCommands'] = not any(t['cmd'] in ('rsync', 'docker', 'sg', 'curl') for t in trace)
            if case.startswith('rollback_'):
                checks['specificFailureReported'] = {
                    'rollback_copy_failure': 'rollback_copy_failed',
                    'rollback_api_failure': 'rollback_api_recreate_failed',
                    'rollback_health_failure': 'rollback_health_failed',
                    'rollback_slow_deadline': 'rollback_health_failed',
                }[case] in process.stderr
            if case in ('slow_health_deadline', 'slow_ready_deadline', 'late_http_200', 'rollback_slow_deadline', 'real_http_deadline'):
                checks['wallTimeBounded'] = elapsed < (6 if case in ('slow_ready_deadline','rollback_slow_deadline') else 4)
                checks['deadlineFailureReported'] = 'timeout_seconds=' in process.stderr
            if case=='slow_ready_deadline':
                calls=[t['args'] for t in trace if t['cmd']=='curl']
                checks['readinessUsesRemainingBudget']=len(calls)>=2 and int(calls[1][calls[1].index('--max-time')+1]) < int(calls[0][calls[0].index('--max-time')+1])
            if case=='rollback_slow_deadline':
                checks['rollbackHasOwnBudget']='timeout_seconds=2' in process.stderr
            if case=='real_http_deadline':
                checks['realCurlAndLoopbackOnly']=not any(t['cmd']=='curl' for t in trace)
                checks['realHealthTimeoutAndRollbackReady']=http_requests==[
                    {'marker':'new','path':'/healthz'}, {'marker':'old','path':'/healthz'}, {'marker':'old','path':'/readyz'}]
            results.append({'case': case, 'exit': process.returncode, 'checks': checks,
                            'elapsedSeconds': elapsed,
                            'realHttpRequests': http_requests,
                            'passed': all(checks.values()), 'stdout': process.stdout,
                            'stderr': process.stderr, 'trace': trace})
        owned_path = str(owned)
    cutover_results = []
    if args.cutover:
        text = args.cutover.read_text(encoding='utf-8-sig')
        match = re.search(r"\$remoteScript = @'\n(.*?)\n'@", text, re.S)
        assert match, 'Cutover embedded shell not found'
        cutover = match.group(1).replace('__MODE__','apply').replace('__RELEASE_ID__','candidate')
        assert 'HEALTH_CHECK_RETRIES=' not in cutover
        assert 'HEALTH_CHECK_TIMEOUT_SECONDS=900 ROLLBACK_HEALTH_TIMEOUT_SECONDS=900' in cutover
        for compatible in (False, True):
            with tempfile.TemporaryDirectory(prefix='georaeplan-cutover-deadline-') as scratch:
                root = Path(scratch)
                for name in ('ops','releases/candidate','bin'): (root/name).mkdir(parents=True)
                for name in ('.env','docker-compose.yml'): (root/'ops'/name).write_text('unchanged fixture\n')
                target = root/'ops/apply-release.sh'
                target.write_text('#!/bin/bash\necho '+('georaeplan-release-health-deadlines-v1' if compatible else 'legacy')+'\n')
                target.chmod(0o700)
                docker = root/'bin/docker'
                docker.write_text('#!/bin/bash\nprintf "%s\\n" "$*" >> "'+str(root/'docker.trace')+'"\nexit 77\n')
                docker.chmod(0o700)
                script = root/'cutover.sh'
                script.write_text(cutover.replace('/srv/georaeplan',str(root)))
                before = tree_digest(root/'ops')
                result = subprocess.run([required['bash'],str(script)],env=dict(os.environ,PATH=str(root/'bin')+os.pathsep+os.environ['PATH']),capture_output=True,text=True,timeout=10)
                traces=(root/'docker.trace').read_text().splitlines() if (root/'docker.trace').exists() else []
                checks={'expectedExit':result.returncode==(77 if compatible else 35),
                        'noMutation':before==tree_digest(root/'ops'),
                        'onlyExpectedRead':traces==(['inspect georaeplan-postgres-1'] if compatible else [])}
                cutover_results.append({'capabilitySupported':compatible,'checks':checks,'passed':all(checks.values())})
    report = {'scriptSha256': hashlib.sha256(args.script.read_bytes()).hexdigest(),
              'cases': results, 'passed': sum(x['passed'] for x in results),
              'total': len(results), 'realServiceCommands': 0,
              'capabilityProbePassed': True,
              'cutoverCallerChecks': cutover_results,
              'ownedFixtureRemoved': not Path(owned_path).exists()}
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'cases'}))
    if report['passed'] != report['total'] or not report['ownedFixtureRemoved'] or any(not row['passed'] for row in cutover_results):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
