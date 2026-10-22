"""GitHub-hosted daily push; no AI dependencies or credentials in source."""
import base64
import hashlib
import hmac
import json
import os
import time
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

LABELS={'PUBLISHED':'已发布','COMPLETED':'已完成','REVIEW':'审核中','IN_PROGRESS':'开发中','QUEUED':'排队中','NOT_DEVELOPED':'未开发','NOT_FOUND':'未收录'}
TZ=timezone(timedelta(hours=8))

def message(data, current=None):
    current=current or datetime.now(TZ)
    title='Wand Pulse 竞品投票游戏监控'
    snapshots=data.get('snapshots') or []
    if not snapshots:
        return title, f'# {title}\n\n⚠ 尚无有效采集数据，请检查本机定时任务。'
    latest=snapshots[-1]
    stamp=datetime.strptime(latest['time'],'%Y-%m-%d %H:%M:%S').replace(tzinfo=TZ)
    stale=current-stamp > timedelta(hours=6) or stamp > current+timedelta(minutes=5)
    previous=next((s for s in reversed(snapshots[:-1]) if s['time'][:10] < latest['time'][:10]), None)
    baseline={r['name']:r['points'] for r in previous['rows']} if previous else {}
    lines=[f'# {title}',f'数据时间：{latest["time"]}（UTC+8）',f'共 {len(latest["rows"])} 款']
    if stale:
        lines.append('⚠ 数据已过期：以下为历史快照，请检查本机采集。')
    if data.get('warning'):
        lines.append('⚠ '+data['warning'])
    for row in latest['rows']:
        prior=baseline.get(row['name'])
        growth=f'{(row["points"]-prior)/prior*100:+.2f}%' if prior else ('新上榜' if previous else '—')
        name=row['name'].replace('\n',' ').replace('*','').replace('[','').replace(']','')
        status=data.get('statuses',{}).get(row['name'],{})
        label=LABELS.get(status.get('status'),'待查询')
        try:
            if current-datetime.fromisoformat(status['cachedAt']) > timedelta(hours=24):
                label+='（缓存过期）'
        except (KeyError,ValueError):
            if status:
                label+='（时间未知）'
        lines.append(f'{row["rank"]}. **{name}**｜{row["points"]:,}｜{growth}｜{label}  ')
    return title, '\n\n'.join(lines)

def main():
    data=json.loads((Path(__file__).resolve().parents[1]/'site/data/dashboard.json').read_text(encoding='utf-8'))
    title,text=message(data)
    if os.getenv('DRY_RUN')=='1':
        print(text)
        return
    url=os.environ['DINGTALK_WEBHOOK']
    parsed=urllib.parse.urlparse(url)
    if parsed.scheme!='https' or parsed.hostname!='oapi.dingtalk.com':
        raise ValueError('Expected an HTTPS DingTalk robot webhook')
    secret=os.getenv('DINGTALK_SECRET')
    if secret:
        stamp=str(int(time.time()*1000))
        sign=base64.b64encode(hmac.new(secret.encode(), f'{stamp}\n{secret}'.encode(), hashlib.sha256).digest()).decode()
        url+='&'+urllib.parse.urlencode({'timestamp':stamp,'sign':sign})
    body=json.dumps({'msgtype':'markdown','markdown':{'title':title,'text':text}},ensure_ascii=False).encode()
    req=urllib.request.Request(url, data=body, headers={'Content-Type':'application/json; charset=utf-8'})
    # Do not automatically retry a potentially delivered message.
    with urllib.request.urlopen(req,timeout=30) as response:
        result=json.load(response)
    if result.get('errcode')!=0:
        raise RuntimeError(f'DingTalk rejected message: errcode={result.get("errcode")}')
    print('DingTalk delivered')

if __name__=='__main__':
    main()
