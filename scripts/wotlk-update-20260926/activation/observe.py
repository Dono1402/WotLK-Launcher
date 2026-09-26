#!/usr/bin/env python3
"""Read-only production observation; write only deployment evidence, no game actions."""
import importlib.util
import json
from pathlib import Path
import re
import subprocess

path = Path('/opt/arthas-next/candidates/atlas-all-update-20260926/inputs/deploy-20260926.py')
spec = importlib.util.spec_from_file_location('deploy', path)
d = importlib.util.module_from_spec(spec)
spec.loader.exec_module(d)

GUILD_JOIN = '''FROM arthas_chars.guild_member gm
JOIN arthas_chars.characters c ON c.guid=gm.guid
JOIN arthas_playerbots.playerbots_account_type b ON b.account_id=c.account AND b.account_type=1
JOIN arthas_chars.guild g ON g.guildid=gm.guildid
JOIN arthas_chars.characters leader ON leader.guid=g.leaderguid
LEFT JOIN arthas_playerbots.playerbots_account_type lp ON lp.account_id=leader.account
WHERE COALESCE(lp.account_type,0)=0'''


def observe():
    d.prepared()
    since = d.read(d.DEPLOY / 'activation-started.json')['at']
    result = {'at': d.now(), 'services': {}, 'errors': {}, 'coreReady': d.core_ready()}
    for unit in d.UNITS:
        state = d.show(unit)
        details = dict(line.split('=', 1) for line in d.run(['systemctl', 'show', unit,
            '-p', 'MemoryCurrent,CPUUsageNSec']).splitlines())
        result['services'][unit] = {k: state[k] for k in ['ActiveState', 'MainPID', 'NRestarts']}
        result['services'][unit].update(details)
        journal = d.run(['journalctl', '-u', unit, '--since', since, '--no-pager', '-o', 'cat'])
        (d.PRIVATE / (unit + '-startup.log')).write_text(journal)
        result['errors'][unit] = {label: len(re.findall(pattern, journal, re.I)) for label, pattern in {
            'fatal': r'Fatal startup|Unhandled exception|segmentation fault|assertion failed|out of memory',
            'sql1062': r'(?m)^.*(?:\[1062\]|Duplicate entry).*$',
            'sql1213': r'(?m)^.*(?:\[1213\]|Deadlock found).*$',
            'permissionDenied': r'permission denied|UnauthorizedAccessException',
        }.items()}
    result['onlineByType'] = [line.split('\t') for line in d.mysql('SELECT COALESCE(p.account_type,0),COUNT(*) '
        'FROM arthas_chars.characters c LEFT JOIN arthas_playerbots.playerbots_account_type p ON p.account_id=c.account '
        'WHERE c.online=1 GROUP BY p.account_type').splitlines()]
    result['realGuildBots'] = int(d.mysql('SELECT COUNT(*) ' + GUILD_JOIN))
    result['realGuildBotsOnline'] = int(d.mysql('SELECT COUNT(*) ' + GUILD_JOIN + ' AND c.online=1'))
    result['realGuildBotsPinned'] = int(d.mysql('SELECT COUNT(*) ' + GUILD_JOIN + '''
        AND EXISTS (SELECT 1 FROM arthas_playerbots.playerbots_random_bots e
                    WHERE e.owner=0 AND e.bot=c.guid AND e.event='add' AND e.value=1 AND e.validIn=0)
        AND NOT EXISTS (SELECT 1 FROM arthas_playerbots.playerbots_random_bots e
                    WHERE e.owner=0 AND e.bot=c.guid AND e.event='logout' AND e.value<>0)'''))
    result['botCountTarget'] = d.mysql("SELECT value FROM arthas_playerbots.playerbots_random_bots WHERE bot=0 AND event='bot_count'")
    result['worldLogErrors'] = {}
    for name in ['Server.log', 'Errors.log', 'Playerbots.log']:
        text = (Path('/opt/arthas-next/server/logs') / name).read_text(errors='replace')
        result['worldLogErrors'][name] = {label: len(re.findall(pattern, text, re.I)) for label, pattern in {
            'sql1062': r'(?m)^.*(?:\[1062\]|Duplicate entry).*$',
            'sql1213': r'(?m)^.*(?:\[1213\]|Deadlock found).*$',
            'crash': r'Assertion failed|segmentation fault|Unhandled exception|out of memory',
        }.items()}
    target = d.DEPLOY / 'observations'
    target.mkdir(exist_ok=True)
    name = result['at'].replace(':', '-') + '.json'
    d.save('observations/' + name, result)
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    observe()
