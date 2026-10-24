"""Counterfactual HP/targetability matrix; production rotations run from pull start without resource resets.
Usage: python mch_transition_matrix.py --dll PATH --source timeline.json --output DIR
shortcut: fixed recorded mechanics and party damage; use live traces to validate latency and party responses.
"""
import argparse, copy, concurrent.futures, json, pathlib, subprocess

def scenario(original, kill, speed, mode, lifetime="last-hit"):
    t = copy.deepcopy(original)
    p1 = next(a for a in t['actors'] if a['baseId'] == 0x459B)
    old = p1['spans'][-1][1]
    delta = kill - old
    tail = p1['spans'][-1][0]
    def shift(x): return x + delta if x >= old else x
    def keep(x): return not (kill <= x < old) if delta < 0 else True
    for a in t['actors']:
        if a is p1:
            a['spans'][-1][1] = kill
            a['hp'] = [[tail + (x[0]-tail)*(kill-tail)/(old-tail) if x[0]>=tail else x[0], x[1]] for x in a['hp']]
            a['death'] = kill
        else:
            for field in ['spans','hp','pos']:
                a[field] = [[shift(v) for v in row] if field=='spans' else [shift(row[0]),*row[1:]] for row in a[field]]
            for field in ['death','presentFrom']:
                if a.get(field) is not None: a[field]=shift(a[field])
    for field in ['playerPos','playerTarget','loggedCasts','loggedHits']:
        t[field] = sorted([[shift(x[0]),*x[1:]] for x in t[field] if keep(x[0])], key=lambda x:x[0])
    for field in ['partyBuffs','enemyDebuffs','playerDebuffs','planWindows','enemyCasts']:
        t[field] = [x for x in t.get(field,[]) if keep(x['start'])]
        for x in t[field]:
            x['start']=shift(x['start'])
            if 'end' in x:x['end']=shift(x['end'])
    # Party cooldowns cannot become ready earlier merely because P1 died earlier.
    for field in ['partyBuffs','enemyDebuffs']:
        last={}; previous={}
        for x in sorted(t[field],key=lambda x:x['start']):
            key=(x['name'],x.get('job')); original_start=x['start']
            if key in previous and abs(original_start-previous[key][0])<.1:
                wait=previous[key][1]
            else:
                wait=max(0,last[key]+119.5-original_start) if key in last and original_start>=kill else 0
                last[key]=original_start+wait; previous[key]=(original_start,wait)
            x['start']+=wait;x['end']+=wait
    crystals=[a for a in t['actors'] if a['baseId']==0x45A3]
    crystal_start=min(a['spans'][0][0] for a in crystals)
    original_end=max(a['spans'][-1][1] for a in crystals)
    keys={a['key'] for a in crystals}
    selected_key=next(row[1] for row in t['playerTarget'] if row[0]>=crystal_start and row[1] in keys)
    selected=next(a for a in crystals if a['key']==selected_key)
    for a in crystals:
        if lifetime=='death' and a.get('death') is not None: a['spans'][-1][1]=a['death']
        if mode=='partner' and a is not selected:continue
        start=a['spans'][0][0]
        for field in ['spans','hp','pos']:
            a[field]=[[start+(v-start)*speed for v in row] if field=='spans' else [start+(row[0]-start)*speed,*row[1:]] for row in a[field]]
        if a.get('death') is not None:a['death']=start+(a['death']-start)*speed
    # Preserve focus on the paired crystal; native selection handles its disappearance.
    crystal_end=max(original_end,max(a['spans'][-1][1] for a in crystals))
    t['playerTarget']=[x for x in t['playerTarget'] if not crystal_start<=x[0]<=crystal_end]
    t['playerTarget'].append([crystal_start,selected['key']]);t['playerTarget'].sort(key=lambda x:x[0])
    t['duration']+=delta
    t['source']=f"COUNTERFACTUAL P1={kill} crystal={speed} mode={mode}; "+t['source']
    assert p1['spans'][-1][1] < next(a for a in t['actors'] if a['baseId']==0x459F)['spans'][0][0]
    assert all(lo<hi for a in t['actors'] for lo,hi in a['spans'])
    return t

def main():
    p=argparse.ArgumentParser();p.add_argument('--dll',required=True);p.add_argument('--source',required=True);p.add_argument('--output',required=True);p.add_argument('--config');p.add_argument('--lifetime',choices=['last-hit','death'],default='last-hit');p.add_argument('--kills',default='140,145,150,155,158,160');p.add_argument('--speeds',default='0.6,0.8,1,1.2,1.4');p.add_argument('--modes',default='partner,all');a=p.parse_args()
    root=pathlib.Path(a.output);root.mkdir(parents=True,exist_ok=True)
    original=json.loads(pathlib.Path(a.source).read_text(encoding='utf-8-sig'))
    jobs=[]
    for kill in map(float,a.kills.split(',')):
      for speed in map(float,a.speeds.split(',')):
       for mode in a.modes.split(','):
        name=f'p1-{kill:g}_crystal-{speed:g}_{mode}';path=root/(name+'.json')
        path.write_text(json.dumps(scenario(original,kill,speed,mode,a.lifetime)))
        jobs.append((name,path,kill,speed,mode))
    def run(job):
        name,path,kill,speed,mode=job
        command=['dotnet',a.dll,str(path),'--seeds','1','--seed-report','--resource-report']
        if a.config:command+=['--config',a.config]
        with (root/(name+'.txt')).open('w') as f:subprocess.run(command,stdout=f,stderr=subprocess.STDOUT,check=True)
        lines=(root/(name+'.txt')).read_text().splitlines()
        seed=json.loads(next(x[5:] for x in lines if x.startswith('SEED ')))
        resources=[json.loads(x[9:]) for x in lines if x.startswith('RESOURCE ')]
        return dict(name=name,kill=kill,speed=speed,mode=mode,total=seed['total'],phase=seed['phase'],resources=resources,casts=seed['casts'],applications=seed['applications'])
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:results=list(pool.map(run,jobs))
    (root/'results.json').write_text(json.dumps(results))
    print(f'{len(results)} complete production replays written to {root}')
if __name__=='__main__':main()
