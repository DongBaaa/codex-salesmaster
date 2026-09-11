"""Uninstalled TradePlan-only coordinator. Invoke with python3 -I -B.

No API request on import. Production paths cannot be overridden by environment.
"""
import argparse
import json
import os
import re
from pathlib import Path
import stat
import sys

BASE=Path('/volume1/workplan-certificate-automation/trade-http01')
CODE=BASE/'native'
# Dedicated Trade journal; Work transactions are never reused.
STAGE=Path('/volume1/workplan-certificate-automation/trade-native-staging')
SOURCE=BASE/'config-http01-trade.2884.kr/trade.2884.kr_ecc'
CERTROOT=Path('/usr/syno/etc/certificate')

def validate_acme_config(config_root,uid=0):
    # acme.sh can execute saved renewal/deploy/reload hooks by itself. Never
    # source config for inspection; accept only plain assignment data here.
    from transaction import read_file,secure_directory,Refused
    config_root=Path(config_root)
    secure_directory(config_root,config_root,uid)
    for relative in ('account.conf','trade.2884.kr_ecc/trade.2884.kr.conf'):
        path=config_root/relative
        if not path.exists() and not path.is_symlink():continue
        data=read_file(path,config_root,uid,True).decode('utf-8')
        seen=set()
        for line in data.splitlines():
            line=line.strip()
            if not line or line.startswith('#'):continue
            match=re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)=(?:'([^']*)'|\"([^\"$`\\]*)\"|([A-Za-z0-9_./:@,+-]*))",line)
            if not match:raise Refused('nonliteral_acme_config')
            key=match[1];value=next(v for v in match.groups()[1:] if v is not None)
            if key in seen:raise Refused('duplicate_acme_config')
            seen.add(key)
            unsafe=('hook' in key.lower() or 'reload' in key.lower() or key.startswith('Le_Real') or
                    key in ('ACCOUNT_CONF_PATH','LE_WORKING_DIR','LE_CONFIG_HOME','CERT_HOME','DOMAIN_CONF'))
            if unsafe and value:raise Refused('saved_acme_side_effect_refused')
            if key=='Le_Domain' and value!='trade.2884.kr':raise Refused('acme_domain_mismatch')

def dispatch(tx,mode,source=SOURCE,run=None,observed=None,ended=False):
    if mode=='preflight':return tx.preflight()
    if mode=='apply':return tx.apply(source,source_format='acme-ecc')
    if mode=='observe':
        with tx.lock():return {'state':'observed','observation':tx.snapshot()['observation']}
    if mode=='rollback':return tx.rollback(run,observed,confirmed_no_inflight=ended)
    raise ValueError('unsupported_mode')

def validate_code_directory():
    if os.geteuid()!=0:raise PermissionError('root_required')
    if Path(__file__).absolute()!=CODE/'native_deploy.py':raise PermissionError('fixed_install_path_required')
    for item in [CODE]+list(CODE.parents):
        s=item.lstat()
        if not stat.S_ISDIR(s.st_mode) or s.st_uid!=0 or s.st_mode&0o022:
            raise PermissionError('unsafe_code_directory')
    for name in ('native_deploy.py','transaction.py','native_api.py'):
        s=(CODE/name).lstat()
        if not stat.S_ISREG(s.st_mode) or s.st_uid!=0 or s.st_nlink!=1 or s.st_mode&0o022:
            raise PermissionError('unsafe_code_file')
    if CODE.resolve()!=CODE:raise PermissionError('noncanonical_code')

def main(argv=None):
    parser=argparse.ArgumentParser(description='TradePlan certificate transaction')
    group=parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--preflight',action='store_true')
    group.add_argument('--apply-http01',action='store_true')
    group.add_argument('--observe',action='store_true')
    group.add_argument('--rollback',metavar='RUN')
    parser.add_argument('--observed')
    parser.add_argument('--previous-request-ended',action='store_true')
    args=parser.parse_args(argv)
    if (args.observed or args.previous_request_ended) and not args.rollback:
        parser.error('rollback-only arguments')
    if args.rollback and (not args.observed or not args.previous_request_ended):
        parser.error('rollback requires observation and previous-request-ended confirmation')
    try:
        validate_code_directory()
        sys.path.insert(0,str(CODE))
        from transaction import Transaction,Validator,TlsProbe
        from native_api import request_import
        tx=Transaction(CERTROOT,STAGE,Validator(),request_import,TlsProbe(),uid=0)
        mode='rollback' if args.rollback else 'apply' if args.apply_http01 else 'observe' if args.observe else 'preflight'
        if mode in ('preflight','apply'):
            # Check unresolved state first, before loading any ACME configuration.
            tx.preflight()
            validate_acme_config(SOURCE.parent)
        result=dispatch(tx,mode,run=args.rollback,observed=args.observed,ended=args.previous_request_ended)
    except Exception as e:
        # Never print raw exception messages, paths, API output or private data.
        result={'state':'refused','errorType':type(e).__name__}
    print(json.dumps(result,sort_keys=True))
    return 0 if result['state'] in ('ready','observed','deployed','rolled-back') else 2

if __name__=='__main__':raise SystemExit(main())
