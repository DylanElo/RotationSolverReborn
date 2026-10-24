"""Assert P2 carryover/payload invariants and report every crystal Wildfire outcome.
Usage: python verify_mch_transitions.py RESULTS_DIR [--strict-crystals]
"""
import json, pathlib, sys
root=pathlib.Path(sys.argv[1]); rows=json.loads((root/'results.json').read_text())
weaponskills={'FullMetalField','BlazingShot','HeatedSplitShot','HeatedSlugShot','HeatedCleanShot','Drill','AirAnchor','ChainSaw','Excavator'}
crystal_casts=0; lost=[]; checks=0; entries={}
for row in rows:
 t=json.loads((root/(row['name']+'.json')).read_text())
 p2=next(a for a in t['actors'] if a['baseId']==0x459F);start=p2['spans'][0][0];end=p2['spans'][-1][1]
 entry=next(a for a in row['resources'] if a['Name'].startswith('459F:') and a['Name'].endswith(':entry'))
 assert entry['heat']>=50 or entry['statuses'].get('Hypercharged',0)>0, (row['name'],'P2 has no funded Hypercharge')
 assert entry['battery']>=40 or entry['robot'], (row['name'],'P2 has neither Battery nor committed Queen')
 if entry['statuses'].get('Overheated',0)>0:
  hc=max(c['time'] for c in row['casts'] if c['action']=='HyperchargePvE' and c['time']<=start)
  assert sum(a['Applied'] and a['Action']=='BlazingShot' and hc<a['Time']<=hc+10 for a in row['applications'])>=5, (row['name'],'Hypercharge expires across transition')
 checks+=3;entries[row['kill']]={k:entry[k] for k in ['heat','battery','robot','cooldowns']}
 casts=[c for c in row['casts'] if c['action']=='WildfirePvE' and start<c['time']<end]
 assert len(casts)==1,(row['name'],'P2 Wildfire count',len(casts));checks+=1
 cast=casts[0]; hits=[a for a in row['applications'] if a['Action']=='Wildfire' and cast['time']<a['Time']<cast['time']+10.1]
 assert len(hits)==1 and hits[0]['Applied'] and hits[0]['Damage']>0,(row['name'],'P2 Wildfire lost');checks+=1
 hit=hits[0]
 stack_hits=[a for a in row['applications'] if a['Applied'] and a['Action'] in weaponskills and a['Target']==hit['Target'] and cast['time']<a['Time']<hit['Time']]
 assert len(stack_hits)>=6,(row['name'],'P2 incomplete payload',len(stack_hits));checks+=1
 buff=next(b for b in t['partyBuffs'] if start<b['start']<end)
 assert any(a['Action']=='FullMetalField' and a['Applied'] and buff['start']<=a['Time']<buff['end'] for a in row['applications']), (row['name'],'P2 FMF misses buffs');checks+=1
 assert any(a['Action'] in weaponskills and a['Applied'] and start<=a['Time']<=start+3 for a in row['applications']), (row['name'],'P2 opening stalls');checks+=1
 crystals=[a for a in t['actors'] if a['baseId']==0x45A3]
 lo=min(a['spans'][0][0] for a in crystals);hi=max(a['spans'][-1][1] for a in crystals)
 for c in row['casts']:
  if c['action']!='WildfirePvE' or not lo<=c['time']<hi:continue
  crystal_casts+=1
  outcomes=[a for a in row['applications'] if a['Action']=='Wildfire' and c['time']<a['Time']<c['time']+10.1]
  if not any(a['Applied'] and a['Damage']>0 for a in outcomes):lost.append({'scenario':row['name'],'cast':c['time'],'outcomes':outcomes})
report={'scenarios':len(rows),'p2Assertions':checks,'p2Entries':entries,'crystalWildfires':crystal_casts,'lostCrystalWildfires':lost}
(root/'verification.json').write_text(json.dumps(report,indent=2))
print(f"{len(rows)} scenarios; {checks} P2 assertions passed; crystal Wildfire {crystal_casts-len(lost)}/{crystal_casts} applied")
if '--strict-crystals' in sys.argv: assert not lost, f'{len(lost)} crystal Wildfires lost; see verification.json'
