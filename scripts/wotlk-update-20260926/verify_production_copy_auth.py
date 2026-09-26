#!/usr/bin/env python3
"""Read auth reference counts only on the networkless cold production copy."""
import json
import subprocess
import time
from prepare_candidate import ROOT, verify
from prepare_databases import check_owned, mysql

if __name__=='__main__':
    verify()
    target,info=check_owned('production-copy')
    output=ROOT/'evidence/production-copy-auth-reference.json'
    if info['State']['Running'] or output.exists():
        raise RuntimeError('Expected a stopped copy and no previous reference-check result.')
    subprocess.run(['docker','start',info['Id']],check=True,stdout=subprocess.DEVNULL)
    try:
        for _ in range(60):
            try:
                mysql(target,'SELECT 1')
                break
            except subprocess.CalledProcessError:
                time.sleep(1)
        else:raise RuntimeError('Production-copy readiness failed.')
        tables=['build_info','rbac_permissions','rbac_linked_permissions','rbac_default_permissions']
        counts={table:int(mysql(target,'SELECT COUNT(*) FROM arthas_auth.'+table).strip()) for table in tables}
        accepted=int(mysql(target,'SELECT COUNT(*) FROM arthas_auth.build_info WHERE build=12340').strip())
        passed=accepted==1 and all(count>0 for count in counts.values())
        output.write_text(json.dumps({'passed':passed,'readOnly':True,'counts':counts,
            'build12340Rows':accepted,'productionStarted':False,'productionChanged':False},indent=2))
        print('Production-copy auth references:',counts,'build12340Rows',accepted,'passed',passed)
        if not passed:raise RuntimeError('Missing auth reference data in the production copy.')
    finally:
        subprocess.run(['docker','stop','--time','30',info['Id']],check=True,stdout=subprocess.DEVNULL)
    verify()
