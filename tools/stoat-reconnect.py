"""Reconnect regression after the native walk, using loopback-proxy.py's control folder."""
import argparse
import importlib.util
import json
import re
import time
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('run', type=Path)
parser.add_argument('--hud-only', action='store_true')
args = parser.parse_args()
spec = importlib.util.spec_from_file_location('walk', root / 'tools' / 'stoat-native-walk.py')
walk = importlib.util.module_from_spec(spec)
spec.loader.exec_module(walk)
run = args.run.resolve()
evidence = run / 'reconnect-checks'
evidence.mkdir(exist_ok=True)
host, guest = [walk.Client(run, side, evidence) for side in ('host', 'guest')]
host_log = max(host.folder.glob('client-*.log'), key=lambda p:p.stat().st_mtime)
guest_log = max(guest.folder.glob('client-*.log'), key=lambda p:p.stat().st_mtime)

def until(test, seconds=15):
    end = time.monotonic() + seconds
    while not test():
        if time.monotonic() > end:
            raise TimeoutError('condition')
        time.sleep(.1)

def widgets(client):
    return client.one('ui-tree')

def click(client, name):
    matches = [w for w in widgets(client) if w['id'].endswith('/' + name)]
    assert len(matches) == 1, (name, matches)
    x,y,w,h = matches[0]['rect']
    client.click([x+w//2,y+h//2])
    time.sleep(.15)

def has(client, name):
    return any(w['id'].endswith('/'+name) for w in widgets(client))

def status(client, text):
    return any(w['id'].endswith('/MultiplayerButton') and text in w['label'] for w in widgets(client))

def fault(action):
    (run/'proxy'/'command').write_text(action)
    until(lambda: not (run/'proxy'/'command').exists())

def grants():
    return re.findall(r'Issued Scout grant ([0-9a-f-]+)', host_log.read_text())

def hud_check():
    # Pose polling belongs to the runtime, not to the visibility of its overlay.
    guest.one('hud hide')
    try:
        tick = int(re.findall(r'Pose pair received;.*host tick (\d+)',guest_log.read_text())[-1])
        until(lambda: int(re.findall(r'Pose pair received;.*host tick (\d+)',guest_log.read_text())[-1]) > tick, seconds=12)
    finally:
        guest.one('hud show')
    print('PASS pose polling continues with HUD hidden', flush=True)

if args.hud_only:
    hud_check()
    raise SystemExit(0)

before = grants()[-1]
offset = host_log.stat().st_size
old = host.actors()[walk.SCOUT]['position']
point = next(p for p in ([550,260],[480,310],[600,300],[400,260])
             if (hit := guest.one(f'world pick {p[0]} {p[1]}'))['kind'] == 'terrain'
             and walk.distance(old,hit['terrain']) > 30)
fault('hold')
time.sleep(.3)
guest.click(point, 'right')
fault('drop')
until(lambda: status(guest, 'disconnected'))
time.sleep(.3)
assert b'Guest move submitted' not in host_log.read_bytes()[offset:]
assert walk.distance(old,host.actors()[walk.SCOUT]['position']) < 1
guest.one(f'screenshot "{evidence / "disconnected.png"}"')
print('PASS outstanding request and pending click invalidated', flush=True)

click(guest,'MultiplayerButton')
click(guest,'Rejoin')
until(lambda: has(guest,'Ready'))
assert grants()[-1] == before
click(guest,'CancelButton')
guest.click(point, 'right')
time.sleep(.3)
assert b'Guest move submitted' not in host_log.read_bytes()[offset:]
assert 'Waiting for both players to be ready.' in guest_log.read_text()
print('PASS rejoin before Ready rejects movement', flush=True)

click(guest,'MultiplayerButton')
click(guest,'Ready')
until(lambda: len(grants()) >= 2 and grants()[-1] != before)
until(lambda: status(guest,'Playing'))
click(guest,'CancelButton')
time.sleep(.4)
guest.select(guest.body(walk.SCOUT),[walk.SCOUT])
guest.ground(point)
guest.click(point, 'right')
until(lambda: walk.distance(old,host.actors()[walk.SCOUT]['position']) > 5)
guest.one(f'screenshot "{evidence / "rejoined.png"}"')
print('PASS fresh Ready issues a new grant and moves Scout', flush=True)

hud_check()
(evidence/'result.json').write_text(json.dumps({'result':'pass','oldGrant':before,'newGrant':grants()[-1]},indent=2))
