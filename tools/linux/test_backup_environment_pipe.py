"""Exercise the real backup environment parser with synthetic producers only."""
import argparse
import pathlib
import shutil
import subprocess
import unittest

ROOT = pathlib.Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--script', type=pathlib.Path, default=ROOT / 'assets/georaeplan-backup/georaeplan-backup.sh')
git_bash = pathlib.Path(r'C:\Program Files\Git\bin\bash.exe')
parser.add_argument('--bash', default=str(git_bash) if git_bash.is_file() else (shutil.which('bash') or '/bin/bash'))
args, remaining = parser.parse_known_args()
script = args.script.read_text(encoding='utf-8-sig')
start = script.index('business_database="$(\n')
end_marker = 'business_database="${business_database:-georaeplan_itworld}"'
end = script.index(end_marker, start) + len(end_marker)
assignment = script[start:end]


class BackupEnvironmentPipeTests(unittest.TestCase):
    def run_parser(self, before, after='', producer_exit=0):
        # More than a pipe buffer after the matching line makes early-reader exit
        # observable without Docker, operational .env files, or timing sleeps.
        producer = f'''
fake_compose() {{
  {before}
  for ((i=0; i<4096; i++)); do
    printf 'SYNTHETIC_PRIVATE_VALUE_%s=must-not-escape-parser-012345678901234567890123456789\\n' "$i"
  done
  {after or ':'}
  printf '__PRODUCER_FINISHED__\\n' >&2
  return {producer_exit}
}}
compose=(fake_compose)
'''
        code = 'set -Eeuo pipefail\n' + producer + assignment + '\nprintf "database=%s\\n" "$business_database"\n'
        return subprocess.run([args.bash, '--noprofile', '--norc', '-c', code], text=True, capture_output=True, timeout=30)

    def assert_consumed(self, result, expected):
        self.assertEqual(0, result.returncode, 'producer was stopped before completing its output')
        self.assertEqual('database='+expected+'\n', result.stdout)
        self.assertEqual('__PRODUCER_FINISHED__\n', result.stderr)
        self.assertNotIn('SYNTHETIC_PRIVATE_VALUE', result.stdout+result.stderr)

    def test_large_output_is_fully_consumed(self):
        self.assert_consumed(self.run_parser("printf 'ITWORLD_POSTGRES_DB=georaeplan_itworld\\n'"), 'georaeplan_itworld')

    def test_first_match_is_preserved(self):
        self.assert_consumed(self.run_parser("printf 'ITWORLD_POSTGRES_DB=first_database\\n'", "printf 'ITWORLD_POSTGRES_DB=second_database\\n'"), 'first_database')

    def test_empty_first_match_keeps_default(self):
        self.assert_consumed(self.run_parser("printf 'ITWORLD_POSTGRES_DB=\\n'", "printf 'ITWORLD_POSTGRES_DB=second_database\\n'"), 'georaeplan_itworld')

    def test_missing_key_keeps_default(self):
        self.assert_consumed(self.run_parser(':'), 'georaeplan_itworld')

    def test_crlf_value_is_trimmed(self):
        self.assert_consumed(self.run_parser("printf 'ITWORLD_POSTGRES_DB=georaeplan_itworld\\r\\n'"), 'georaeplan_itworld')

    def test_late_producer_failure_still_blocks_backup(self):
        result = self.run_parser("printf 'ITWORLD_POSTGRES_DB=georaeplan_itworld\\n'", producer_exit=23)
        self.assertEqual(23, result.returncode)
        self.assertEqual('', result.stdout, 'failed configuration producer must not become a successful default')
        self.assertEqual('__PRODUCER_FINISHED__\n', result.stderr)


if __name__ == '__main__':
    unittest.main(argv=[__file__]+remaining, verbosity=2)
