#!/usr/bin/env python3
"""Check startup reservation against the real, restarted disposable realm."""
import json
from pathlib import Path
import sys

ROOT = Path('/opt/atlas-shop-tests/rename-20260911')
CANDIDATE = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
sys.path.insert(0, str(ROOT / 'mod-atlas-shop/tests'))
from test_account_services_realm import Fixture


def main():
    f = Fixture(ROOT, 'guild-reservation-result.json')
    try:
        expected = json.loads((CANDIDATE / 'evidence/guild-reservation-input.json').read_text())
        if expected['fixture'] != str(ROOT) or len(expected['bots']) != 2:
            raise RuntimeError('The synthetic guild must be prepared first.')
        ids = sorted(int(bot['guid']) for bot in expected['bots'])
        guid_list = ','.join(map(str, ids))
        f.wait(lambda: f.sql('SELECT COUNT(*) FROM shop_test_chars.guild_member WHERE guildid='
            + str(int(expected['guildId'])) + ' AND guid IN (' + guid_list + ')') == '2')
        f.check(True, 'Both synthetic guild memberships survive the fixture restart.')
        f.wait(lambda: f.sql('SELECT COUNT(*) FROM shop_test_playerbots.playerbots_random_bots '
            "WHERE owner=0 AND event='add' AND value=1 AND validIn=0 AND bot IN (" + guid_list + ')') == '2', 180)
        f.check(True, 'Both guild bots receive indefinite native reservation events at startup.')

        def online():
            data = f.sql('SELECT c.guid FROM shop_test_chars.characters c '
                'JOIN shop_test_auth.account a ON a.id=c.account '
                'JOIN shop_test_playerbots.playerbots_account_type p ON p.account_id=a.id '
                "WHERE a.username LIKE 'ATLASFIXTUREBOT%' AND p.account_type=1 AND c.online=1 ORDER BY c.guid")
            return [] if not data else [int(value) for value in data.splitlines()]

        f.wait(lambda: online() == ids, 240)
        f.check(True, 'The two available random-bot population slots are occupied by the two reserved guild bots.')
        text = (ROOT / 'logs/world-console.log').read_text(errors='replace')
        f.check('Reserved 2 randombots from guilds led by real players for priority login' in text,
                'The real startup log confirms the Atlas priority-reservation path.')
        f.save(True, 'complete')
    except Exception as error:
        f.save(False, type(error).__name__ + ': ' + str(error))
        raise


if __name__ == '__main__':
    main()
