"""Reduce complete public FFLogs captures to external replay fixtures, never action inputs."""
import collections, json, statistics, sys
from pathlib import Path

root=Path(sys.argv[1]); output=Path(sys.argv[2]);output.mkdir(parents=True,exist_ok=True)
samples=[('light','raids/light',37,582),('cruiser','raids/cruiser',2,2),('heavy','raids/heavy',34,111),('dmu','dmu',8,2),('fru','fru',7,6)]
buff_jobs={'Divination':'AST','Battle Voice':'BRD','Radiant Finale':'BRD','Technical Finish':'DNC','Battle Litany':'DRG','Brotherhood':'MNK','Mug':'NIN','Dokumori':'NIN','Starry Muse':'PCT','Arcane Circle':'RPR','Embolden':'RDM','Chain Stratagem':'SCH','Searing Light':'SMN'}
gcd_names={'Reawaken','First Generation','Second Generation','Third Generation','Fourth Generation','Ouroboros','Uncoiled Fury','Vicewinder','Dreadwinder',"Hunter's Coil","Swiftskin's Coil",'Steel Fangs','Reaving Fangs',"Hunter's Sting","Swiftskin's Sting",'Hindsting Strike','Flanksbane Fang','Hindsbane Fang','Flanksting Strike','Writhing Snap'}
for label,directory,fid,actor in samples:
 p=root/directory;meta=json.loads((p/'meta.json').read_text(encoding='utf-8-sig'));events=json.loads((p/'events.json').read_text(encoding='utf-8-sig'));fight=next(x for x in meta['fights'] if x['id']==fid);start=fight['start_time'];duration=(fight['end_time']-start)/1000
 pages=[json.loads(f.read_text(encoding='utf-8-sig')) for f in sorted(p.glob('page-*.json'))];assert not pages[-1].get('nextPageTimestamp');assert len(events)==sum(len(x['events']) for x in pages)
 cs=[e for e in events if e['type']=='cast' and e.get('sourceID')==actor];gcd=[e for e in cs if e['ability']['name'] in gcd_names]
 recasts=collections.defaultdict(list)
 for a,b in zip(gcd,gcd[1:]):
  dt=(b['timestamp']-a['timestamp'])/1000
  if .9<dt<4.5:recasts[a['ability']['name']].append(dt)
 timing={name:round(statistics.median(times),3) for name,times in recasts.items()}
 target_ids={e['targetID'] for e in cs if e.get('targetID') is not None and e.get('targetIsFriendly')==False and e['ability']['name'] in gcd_names}
 # Target schedules encode observed switches and targetability, not future action choices.
 target_events=[];current=None;first={};last={};hp_states={};hp_transitions=[]
 for e in events:
  tid=e.get('targetID');resources=e.get('targetResources',{})
  if tid in target_ids and resources.get('maxHitPoints',0)>0:
   dying=resources['hitPoints']/resources['maxHitPoints']<.02
   if dying!=hp_states.get(tid,False):hp_transitions.append({'t':(e['timestamp']-start)/1000,'target':tid,'dying':dying})
   hp_states[tid]=dying
  if e.get('targetID') in target_ids and e.get('sourceIsFriendly') and e['type'] in ('cast','damage'):
   last[e['targetID']]=(e['timestamp']-start)/1000
  if e in gcd and e.get('targetID') in target_ids:
   tid=e['targetID'];first.setdefault(tid,(e['timestamp']-start)/1000)
   if tid!=current:target_events.append({'t':(e['timestamp']-start)/1000,'target':tid});current=tid
 targetability=[{'t':(e['timestamp']-start)/1000,'target':e['targetID'],'on':bool(e['targetable'])} for e in events if e['type']=='targetabilityupdate' and e['targetID'] in target_ids]
 windows=[];opened={};damage_windows=[];extra_names={'Standard Finish','Devilment',"Hunter's Instinct"};extra_open={}
 for e in events:
  name=e.get('ability',{}).get('name');typ=e['type'];tid=e.get('targetID')
  if name in extra_names and tid==actor and typ in ['applybuff','refreshbuff','removebuff']:
   t=(e['timestamp']-start)/1000
   if typ=='removebuff' and name in extra_open:damage_windows.append({'start':extra_open.pop(name),'end':t,'name':name,'target':0})
   elif typ!='removebuff':extra_open.setdefault(name,t)
  if name not in buff_jobs and name!='Medicated':continue
  if not ((tid==actor and typ.endswith('buff')) or (tid in target_ids and typ.endswith('debuff'))):continue
  key=(name,tid);t=(e['timestamp']-start)/1000
  if typ.startswith('apply') or typ.startswith('refresh'):
   if key not in opened:opened[key]=t
  elif typ.startswith('remove') and key in opened:
   windows.append({'start':opened.pop(key),'end':t,'name':name,'job':buff_jobs.get(name,''),'target':tid if tid!=actor else 0})
 for (name,tid),t in opened.items():windows.append({'start':t,'end':duration,'name':name,'job':buff_jobs.get(name,''),'target':tid if tid!=actor else 0})
 for name,t in extra_open.items():damage_windows.append({'start':t,'end':duration,'name':name,'target':0})
 damage_windows += windows
 casts_by_packet={e['packetID']:e for e in cs if 'packetID' in e}
 calculated=[e for e in events if e['type']=='calculateddamage' and e.get('sourceID')==actor]
 damage_by_packet={e['packetID']:e for e in events if e['type']=='damage' and e.get('sourceID')==actor and 'packetID' in e}
 hit_timing=collections.defaultdict(list)
 for e in calculated:
  impact=damage_by_packet.get(e.get('packetID'))
  if impact:hit_timing[e['ability']['name']].append((impact['timestamp']-e['timestamp'])/1000)
 info=next((e for e in events if e['type']=='combatantinfo' and e.get('sourceID')==actor),{})
 last_target=target_events[-1]['target'];enemy_meta={e['id']:e for e in meta['enemies']}
 fixture={'label':label,'territoryId':{'light':1232,'cruiser':1263,'heavy':1325,'fru':1238,'dmu':1363}[label],'duration':duration,'actor':actor,'events':len(events),'pages':len(pages),'timing':timing,'targets':[{'id':tid,'first':first.get(tid,0),'end':last[tid],'finalBoss':tid==last_target,'boss':enemy_meta[tid]['type']=='Boss','baseId':enemy_meta[tid]['guid'],'name':enemy_meta[tid]['name']} for tid in sorted(target_ids)],'switches':target_events,'targetability':targetability,'windows':windows,'damageWindows':damage_windows,'stats':{k:info.get(k) for k in ['level','dexterity','criticalHit','directHit','simulatedCrit','simulatedDirectHit']},'hitDelays':{n:round(statistics.median(v),3) for n,v in hit_timing.items()},'reference':[{'t':(e['timestamp']-start)/1000,'name':e['ability']['name'],'target':e.get('targetID',0)} for e in cs if e['ability']['name'] in gcd_names|{"Serpent's Ire"}],'referenceHits':[{'t':(e['timestamp']-start)/1000,'name':e['ability']['name'],'target':e.get('targetID',0),'multiplier':e.get('multiplier'),'hitType':e.get('hitType'),'directHit':e.get('directHit',False)} for e in calculated if e.get('targetID') == casts_by_packet.get(e.get('packetID'),{}).get('targetID', e.get('targetID'))]}
 fixture['hpDying']=hp_transitions
 (output/(label+'.json')).write_text(json.dumps(fixture,indent=2),encoding='utf-8');print(label,timing)

