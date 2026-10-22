"""Local acquisition, validated export and XMod lookup. Python 3.11+, standard library only."""
import argparse
import csv
import json
import os
import re
import subprocess
import sys
import time
import unicodedata
import urllib.request
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TZ = timezone(timedelta(hours=8))
PRIORITY = dict(NOT_FOUND=0, NOT_DEVELOPED=1, QUEUED=2, IN_PROGRESS=3, REVIEW=4, COMPLETED=5, PUBLISHED=6)

def now():
    return datetime.now(TZ)

def atomic_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(path)

def normalize(name):
    name = ''.join(c for c in name if unicodedata.category(c) not in ('Cf', 'Cc') and c not in '™®©')
    name = name.replace('’', "'").replace('‘', "'")
    old = None
    while old != name:
        old = name
        name = re.sub(r'\s*(?:\((?:game preview|demo|playtest|(?:open |closed )?beta|early access)\)|\s+(?:game preview|demo|playtest|beta|early access))\s*$', '', name, flags=re.I).strip()
    return name.casefold()

def load_snapshots(csv_path, minimum=20):
    grouped = defaultdict(list)
    with open(csv_path, encoding='utf-8-sig', newline='') as f:
        for row in csv.DictReader(f):
            try:
                stamp = row['获取时间']
                datetime.strptime(stamp, '%Y-%m-%d %H:%M:%S')
                item = dict(rank=int(row['序号']), name=row['游戏名称'].strip(), points=int(row['点数']), platform=row.get('平台', ''))
                grouped[stamp].append(item)
            except (ValueError, KeyError, TypeError):
                continue
    valid = []
    for stamp, rows in sorted(grouped.items()):
        if (len(rows) < minimum or len({r['name'].casefold() for r in rows}) != len(rows)
                or sorted(r['rank'] for r in rows) != list(range(1, len(rows)+1))
                or any(not r['name'] or r['points'] < 0 or '邀请好友' in r['name'] or '获得奖励' in r['name'] for r in rows)):
            print(f'Rejected incomplete/invalid snapshot: {stamp} ({len(rows)} rows)')
            continue
        valid.append(dict(time=stamp, rows=sorted(rows, key=lambda r:r['rank'])))
    return valid

def scan_status(names, config, old):
    credential = os.getenv('XMOD_LOGIN_CREDENTIAL', '')
    if not credential:
        print('XMOD_LOGIN_CREDENTIAL missing: retaining last-known statuses, freshness remains unchanged.')
        return old, '未配置 XMod 凭据，开发状态未刷新'
    requested = defaultdict(list)
    for name in names:
        requested[normalize(name)].append(name)
    findings = {}
    pages = None
    max_pages = int(config.get('xmodMaxPages', 500))
    headers = {'accept':'application/json', 'login-credential':credential,
               'origin':'http://cms.qiyou.cn:3120', 'referer':'http://cms.qiyou.cn:3120/',
               'saas-app-id':'GAME_TOOL', 'saas-platform':'pc', 'saas-product-line':'XMOD'}
    try:
        page = 1
        while pages is None or page <= pages:
            url = config['xmodApi'] + f'?page_size=100&page={page}'
            req = urllib.request.Request(url, headers=headers)
            # A failed page invalidates the entire refresh, never overwrite good cache with NOT_FOUND.
            for attempt in range(3):
                try:
                    with urllib.request.urlopen(req, timeout=30) as resp:
                        payload = json.load(resp)
                    if not isinstance(payload.get('games'), list):
                        raise ValueError('Invalid XMod response')
                    break
                except Exception:
                    if attempt == 2:
                        raise
                    time.sleep(2 ** attempt)
            pages = int(payload['total_page'])
            if pages < 1 or pages > max_pages:
                raise ValueError('XMod pagination outside configured limit')
            for entry in payload['games']:
                game = entry['game']
                state = game.get('development_status')
                if state not in PRIORITY or state == 'NOT_FOUND':
                    continue
                title = game.get('title') or {}
                for key in {normalize(title.get('name') or ''), normalize(title.get('translate') or '')}:
                    for name in requested.get(key, []):
                        if name not in findings or PRIORITY[state] > PRIORITY[findings[name]['status']]:
                            findings[name] = dict(status=state, platform=entry.get('platforms') or '', cachedAt=now().isoformat())
            page += 1
            time.sleep(.05)
        # Compare duplicates within this scan only: authoritative state may move backwards.
        for name in names:
            findings.setdefault(name, dict(status='NOT_FOUND', platform='', cachedAt=now().isoformat()))
        return findings, None
    except Exception as exc:
        print(f'XMod refresh failed ({type(exc).__name__}); retaining previous cache.')
        return old, 'XMod 刷新失败，显示最后已知状态'

def export(csv_path, config, refresh=False, allow_stale=False):
    snapshots = load_snapshots(csv_path, int(config.get('minGames', 20)))
    if not snapshots:
        raise ValueError('No complete snapshots; existing published data was not changed')
    latest = datetime.strptime(snapshots[-1]['time'], '%Y-%m-%d %H:%M:%S').replace(tzinfo=TZ)
    if not allow_stale and (latest > now()+timedelta(minutes=5) or now()-latest > timedelta(hours=config.get('maxDataAgeHours',6))):
        raise ValueError('Latest complete snapshot is stale or in the future; refusing upload')
    destination = ROOT/'site/data/dashboard.json'
    old_data = json.loads(destination.read_text(encoding='utf-8')) if destination.exists() else {}
    statuses = old_data.get('statuses', {})
    warning = '历史样例数据，非实时采集' if allow_stale else None
    if refresh:
        names = sorted({r['name'] for s in snapshots for r in s['rows']})
        statuses, status_warning = scan_status(names, config, statuses)
        warning = status_warning or warning
    atomic_json(destination, dict(generatedAt=now().isoformat(), latestTime=snapshots[-1]['time'],
                                  snapshots=snapshots, statuses=statuses, warning=warning))
    print(f'Exported {len(snapshots)} complete snapshots; latest {snapshots[-1]["time"]}')

def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)

def collect(config):
    # Check CDP; launch only if Wand is not already running, never kill an active client.
    try:
        with urllib.request.urlopen('http://127.0.0.1:9222/json', timeout=3) as resp:
            json.load(resp)
    except Exception:
        check = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq Wand.exe', '/FO', 'CSV'], capture_output=True, text=True)
        if 'wand.exe' in check.stdout.lower():
            raise RuntimeError('Wand is running without CDP. Close it manually, then run this task again.')
        subprocess.Popen([config['wandExe'], '--remote-debugging-port=9222', '--remote-debugging-address=127.0.0.1'])
        for _ in range(30):
            time.sleep(2)
            try:
                with urllib.request.urlopen('http://127.0.0.1:9222/json', timeout=3):
                    break
            except Exception:
                continue
        else:
            raise RuntimeError('Wand CDP did not start; open Wand and sign in manually')
    dotnet = config.get('dotnetExe', 'dotnet')
    project = ROOT/'collector/WandPulseGrabber'
    run(dotnet, 'build', project, '-c', 'Release', '--nologo')
    csv_path = ROOT/'local/pulse_data.csv'
    csv_path.parent.mkdir(exist_ok=True)
    environment = dict(os.environ, WAND_MIN_GAMES=str(config.get('minGames',20)))
    subprocess.run([dotnet,str(project/'bin/Release/net8.0/WandPulseGrabber.dll'),str(csv_path)],
                   cwd=ROOT, env=environment, timeout=600, check=True)
    export(csv_path, config, refresh=True)

def upload(config):
    git = config.get('gitExe', 'git')
    branch = config.get('branch','main')
    # Stage precisely the public dataset, never local credentials or CSV sources.
    run(git, 'add', '--', 'site/data/dashboard.json')
    changed = subprocess.run([git,'diff','--cached','--quiet','--','site/data/dashboard.json'],cwd=ROOT)
    if changed.returncode == 1:
        run(git, 'commit', '-m', 'data: update Wand Pulse snapshots', '--', 'site/data/dashboard.json')
    elif changed.returncode != 0:
        raise RuntimeError('Cannot inspect staged data')
    # Divergence fails clearly rather than silently overwriting remote changes.
    run(git, 'push', 'origin', f'HEAD:refs/heads/{branch}')

def main():
    p = argparse.ArgumentParser()
    p.add_argument('command', choices=['collect','upload','export','run'])
    p.add_argument('--csv', type=Path)
    p.add_argument('--allow-stale', action='store_true', help='Offline historical import only')
    args = p.parse_args()
    config_file=ROOT/'local/config.json'
    config=json.loads((config_file if config_file.exists() else ROOT/'config.example.json').read_text(encoding='utf-8-sig'))
    if args.command in ('collect','run'):
        collect(config)
    if args.command in ('upload','run'):
        upload(config)
    if args.command == 'export':
        export(args.csv or ROOT/'local/pulse_data.csv', config, allow_stale=args.allow_stale)

if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(f'ERROR: {exc}', file=sys.stderr)
        sys.exit(1)
