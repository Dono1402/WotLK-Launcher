#!/usr/bin/env python3
"""Populate empty synthetic client-build and RBAC references from pinned public SQL."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time
from prepare_databases import check_owned, mysql
from prepare_candidate import inactive, ROOT

if __name__=='__main__':
    inactive()
    fixture,info=check_owned('fixture')
    if info['State']['Running']:
        raise RuntimeError('Stop the synthetic realm before preparing its auth reference data.')
    tables=['build_info','rbac_permissions','rbac_linked_permissions','rbac_default_permissions']
    inserts={}
    hashes={}
    for table in tables:
        source=ROOT/'core/data/sql/base/db_auth'/(table+'.sql')
        matches=re.findall(r'INSERT INTO `'+table+r'` VALUES\s*\n(.*?);',source.read_text(),re.S)
        if len(matches)!=1 or (table=='build_info' and '(12340,3,3,5,' not in matches[0]):
            raise RuntimeError('Unexpected pinned auth reference data: '+table)
        inserts[table]=matches[0]
        hashes[table]=hashlib.sha256(source.read_bytes()).hexdigest()
    subprocess.run(['docker','start',info['Id']],check=True,stdout=subprocess.DEVNULL)
    try:
        for _ in range(60):
            try:
                mysql(fixture,'SELECT 1')
                break
            except subprocess.CalledProcessError:
                time.sleep(1)
        else:raise RuntimeError('Synthetic database readiness failed.')
        before={table:int(mysql(fixture,'SELECT COUNT(*) FROM shop_test_auth.'+table).strip()) for table in tables}
        if any(before.values()):
            raise RuntimeError('Only the known-empty synthetic reference tables may be populated.')
        statements=['INSERT INTO shop_test_auth.'+table+' VALUES\n'+inserts[table]+';' for table in tables]
        mysql(fixture,'START TRANSACTION;\n'+'\n'.join(statements)+'\nCOMMIT;')
        after={table:int(mysql(fixture,'SELECT COUNT(*) FROM shop_test_auth.'+table).strip()) for table in tables}
        accepted=int(mysql(fixture,'SELECT COUNT(*) FROM shop_test_auth.build_info WHERE build=12340').strip())
        if after['build_info']!=11 or accepted!=1 or any(value<=0 for value in after.values()):
            raise RuntimeError('Unexpected auth reference row count.')
        (ROOT/'evidence/synthetic-auth-reference.json').write_text(json.dumps({
            'fixture':str(fixture),'before':before,'after':after,'sourceSha256':hashes,
            'productionChanged':False},indent=2))
        print('PASS synthetic auth references:',after,flush=True)
    finally:
        subprocess.run(['docker','stop','--time','30',info['Id']],check=True,stdout=subprocess.DEVNULL)
