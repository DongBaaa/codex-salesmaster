"""Local candidate only: no CLI and no import at module load time.

The coordinator must create/verify a private staging copy, back up the old
certificate, and verify service mappings and served TLS after this request.
This adapter deliberately does not decide deployment success or retry imports.
"""
from dataclasses import dataclass
import json
import re
import subprocess

EXECUTABLE = '/usr/syno/bin/synowebapi'

@dataclass(frozen=True)
class ImportOutcome:
    state: str
    code: str
    api_success: bool = False
    reconciliation_required: bool = True

def import_arguments(staging_directory, certificate_id, description):
    if description != 'trade.2884.kr':
        raise ValueError('unsupported_certificate_description')
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', certificate_id):
        raise ValueError('invalid_certificate_id')
    # POSIX arguments are assembled independently of the Windows test host.
    raw = str(staging_directory)
    if not raw.startswith('/') or any(x in raw for x in ['\x00', '\n', '\r', '\\']):
        raise ValueError('invalid_staging_directory')
    components = raw.split('/')
    if any(x in ('.', '..') for x in components) or raw.endswith('/') or '//' in raw:
        raise ValueError('noncanonical_staging_directory')
    # Only files under this purpose-built private staging tree can be imported.
    if not raw.startswith('/volume1/workplan-certificate-automation/trade-native-staging/'):
        raise ValueError('outside_native_staging')
    def parameter(name, value): return name + '=' + json.dumps(value, ensure_ascii=True)
    return [EXECUTABLE, '--exec-fastwebapi', 'api=SYNO.Core.Certificate',
            'method=import', 'version=1',
            parameter('key_tmp', raw+'/privkey.pem'),
            parameter('cert_tmp', raw+'/cert.pem'),
            parameter('inter_cert_tmp', raw+'/chain.pem'),
            parameter('id', certificate_id), parameter('desc', description)]

def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value: raise ValueError('duplicate_json_key')
        value[key] = item
    return value

def classify_response(returncode, stdout):
    # Every dispatched request may have changed the certificate, even on timeout,
    # malformed output, a nonzero exit or a rejected response. Never retry blindly.
    if returncode != 0:
        return ImportOutcome('uncertain', 'command_nonzero')
    if not isinstance(stdout, str) or not stdout.strip():
        return ImportOutcome('uncertain', 'empty_response')
    if len(stdout.encode('utf-8')) > 1024*1024:
        return ImportOutcome('uncertain', 'oversized_response')
    try:
        value = json.loads(stdout, object_pairs_hook=_unique_object)
    except (ValueError, RecursionError):
        return ImportOutcome('uncertain', 'invalid_json_response')
    if not isinstance(value, dict):
        return ImportOutcome('uncertain', 'invalid_response_shape')
    if 'error' in value:
        return ImportOutcome('rejected', 'api_error')
    if value.get('success') is not True:
        return ImportOutcome('rejected', 'explicit_success_missing')
    # This is only the API acknowledgement. Files, mapping and public TLS remain
    # independent requirements and are not inferred from success:true.
    return ImportOutcome('acknowledged', 'api_success', api_success=True)

def request_import(staging_directory, certificate_id, description, runner=subprocess.run):
    args = import_arguments(staging_directory, certificate_id, description)
    try:
        response = runner(args, capture_output=True, text=True, encoding='utf-8',
                          errors='replace', timeout=90, check=False,
                          env={'PATH':'/usr/syno/bin:/usr/bin:/bin', 'LC_ALL':'C'})
    except subprocess.TimeoutExpired:
        return ImportOutcome('uncertain', 'command_timeout')
    except OSError:
        return ImportOutcome('uncertain', 'command_unavailable')
    # Never expose stdout/stderr: output can contain filesystem or secret data.
    return classify_response(response.returncode, response.stdout)
