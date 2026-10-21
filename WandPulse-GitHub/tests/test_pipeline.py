import csv
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from datetime import datetime

ROOT=Path(__file__).resolve().parents[1]
def module(name):
    spec=importlib.util.spec_from_file_location(name,ROOT/'scripts'/f'{name}.py')
    result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result);return result
p=module('pipeline');d=module('dingtalk')

class PipelineTests(unittest.TestCase):
    def test_real_handoff_rejects_advertisement(self):
        snapshots=p.load_snapshots(ROOT/'samples/pulse_data.csv')
        self.assertEqual(snapshots[-1]['time'],'2026-10-11 09:01:03')
        self.assertEqual(len(snapshots[-1]['rows']),24)

    def test_equal_votes_are_distinct_games(self):
        with tempfile.TemporaryDirectory() as folder:
            file=Path(folder)/'sample.csv'
            with file.open('w',encoding='utf-8',newline='') as f:
                writer=csv.writer(f);writer.writerow(['序号','游戏名称','点数','获取时间','平台'])
                for rank in range(1,21):writer.writerow([rank,f'Game {rank}',100,'2026-10-21 09:30:00','Steam'])
            self.assertEqual(len(p.load_snapshots(file)[0]['rows']),20)

    def test_normalize(self):
        self.assertEqual(p.normalize('\u200bGame™ (Demo)'),p.normalize('Game'))

    def test_failed_scan_keeps_cache(self):
        old={'Game':{'status':'PUBLISHED','cachedAt':'2026-01-01T00:00:00+08:00'}}
        with patch.dict('os.environ',{'XMOD_LOGIN_CREDENTIAL':'test'}),patch.object(p.urllib.request,'urlopen',side_effect=OSError),patch.object(p.time,'sleep'):
            result,warning=p.scan_status(['Game'],{'xmodApi':'https://example.test','xmodMaxPages':5},old)
        self.assertEqual(result,old);self.assertIsNotNone(warning)

    def test_scan_all_pages_prefers_duplicates_and_allows_regression(self):
        class Response:
            def __init__(self,obj):self.body=json.dumps(obj).encode()
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self,*args):return self.body
        def entry(state):return {'game':{'title':{'name':'Game'},'development_status':state}}
        responses=[Response({'total_page':2,'games':[entry('QUEUED')]}),Response({'total_page':2,'games':[entry('PUBLISHED')]})]
        with patch.dict('os.environ',{'XMOD_LOGIN_CREDENTIAL':'test'}),patch.object(p.urllib.request,'urlopen',side_effect=responses) as request,patch.object(p.time,'sleep'):
            result,warning=p.scan_status(['Game'],{'xmodApi':'https://example.test'}, {})
        self.assertEqual(request.call_count,2);self.assertEqual(result['Game']['status'],'PUBLISHED');self.assertIsNone(warning)
        with patch.dict('os.environ',{'XMOD_LOGIN_CREDENTIAL':'test'}),patch.object(p.urllib.request,'urlopen',return_value=Response({'total_page':1,'games':[entry('QUEUED')]})),patch.object(p.time,'sleep'):
            result,warning=p.scan_status(['Game'],{'xmodApi':'https://example.test'},result)
        self.assertEqual(result['Game']['status'],'QUEUED')

    def test_stale_export_does_not_overwrite(self):
        with tempfile.TemporaryDirectory() as folder,patch.object(p,'ROOT',Path(folder)):
            with self.assertRaises(ValueError):p.export(ROOT/'samples/pulse_data.csv',{})
            self.assertFalse((Path(folder)/'site/data/dashboard.json').exists())

    def test_report_warns_stale_uses_previous_day(self):
        data={'snapshots':[{'time':'2026-10-19 10:00:00','rows':[{'rank':1,'name':'Game','points':100}]},{'time':'2026-10-20 09:30:00','rows':[{'rank':1,'name':'Game','points':110}]}],'statuses':{}}
        _,text=d.message(data,datetime(2026,10,21,tzinfo=d.TZ))
        self.assertIn('数据已过期',text);self.assertIn('+10.00%',text);self.assertIn('待查询',text)

if __name__=='__main__':unittest.main()
