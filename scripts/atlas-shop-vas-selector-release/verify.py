#!/usr/bin/env python3
"""Verify the installed Hermes fix without using a real player's account."""
import datetime
import json
from pathlib import Path
import subprocess
import sys
import urllib.request
import argparse
import deploy
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--release',default=deploy.RELEASE,choices=('vas-selector-20260912','vas-wire-type-20260912'))
deploy.configure_release(parser.parse_args().release)
from deploy import ROOT, HERMES, SERVICE, CONFIG, sha, state, verify_baseline, write
sys.path.insert(0, '/opt/atlas-shop-releases/gold-1.7.2-20260912/scripts')
from release_runtime import query

before=json.loads((ROOT/'before.json').read_text(encoding='utf-8-sig'))
activation=json.loads((ROOT/'activated.json').read_text())
proof=json.loads((ROOT/'tested.json').read_text())
verify_baseline(before,include_hermes=False)
current=state(SERVICE)
if current['ActiveState']!='active' or current['MainPID']!=activation['state']['MainPID'] or current['NRestarts']!='0':
    raise RuntimeError('Hermes is not stable after activation')
if sha('/proc/'+current['MainPID']+'/exe')!=proof['hermesSha256'] or sha(HERMES/'HermesProxy')!=proof['hermesSha256']:
    raise RuntimeError('Active Hermes differs from the tested candidate')
with urllib.request.urlopen('http://127.0.0.1:4323/health',timeout=5) as response:
    if response.status!=200 or json.load(response).get('status')!='ok': raise RuntimeError('API is not healthy')
heartbeat=query('SELECT realm_id,protocol,TIMESTAMPDIFF(SECOND,last_seen_at,UTC_TIMESTAMP()) FROM arthas_auth.atlas_shop_delivery_health WHERE realm_id=1')
if len(heartbeat)!=1 or heartbeat[0][:2]!=['1','2'] or not 0<=int(heartbeat[0][2])<30:
    raise RuntimeError('Native service heartbeat is stale')
config_hash=sha(CONFIG)
if config_hash!=activation['configurationSha256']: raise RuntimeError('Proxy configuration changed')
public_manifest=subprocess.check_output(['curl','-4','--fail','--silent','--show-error','--max-time','15',
    'https://animeclub.fr/wotlk/launcher/launcher-update.json'])
import hashlib
if hashlib.sha256(public_manifest).hexdigest()!='420988c2b3ba4e794215456b3b8ec9eab51ea72057df3dad89b63d1cba752d06':
    raise RuntimeError('Public launcher manifest changed')
report={**activation,'verifiedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'activeState':current,'nativeHeartbeat':heartbeat,'apiHealthy':True,'publicLauncherVersionUnchanged':'1.7.2',
    'isolatedNetworkChecks':len(proof.get('checks',[])),'fixturePassed':proof['passed'],
    'livePlayerOrdersCreated':0,'livePlayerConversionsCreated':0,'graphicalClientValidation':'Awaiting user retry after reconnecting'}
write(ROOT/'verified.json',report)
print(json.dumps(report,indent=2))
