"""Per-hit current-potency coverage; separate buff sources without invented crit/DH weights."""
import collections,itertools,json,sys
from pathlib import Path

BURST={'Reawaken':750,'Ouroboros':1150,'Uncoiled Fury':680,'Uncoiled Twinfang':170,'Uncoiled Twinblood':170}
BURST.update({n+' Generation':680 for n in ['First','Second','Third','Fourth']})
BURST.update({n+' Legacy':320 for n in ['First','Second','Third','Fourth']})
NORMAL={"Hunter's Sting":300,"Swiftskin's Sting":300,'Death Rattle':280,'Vicewinder':540,'Dreadwinder':540,"Hunter's Coil":680,"Swiftskin's Coil":680,'Twinfang Bite':170,'Twinblood Bite':170,'Writhing Snap':200}
FINISH={'Flanksting Strike','Flanksbane Fang','Hindsting Strike','Hindsbane Fang'}
ORDINARY={'Divination':.06,'Brotherhood':.05,'Embolden':.05,'Searing Light':.05,'Starry Muse':.05,'Arcane Circle':.03,'Mug':.05,'Dokumori':.05}
CHANCE={'Battle Litany':('crit',.10),'Chain Stratagem':('crit',.10),'Battle Voice':('dh',.20),'Devilment':('crit+dh',.20)}

def potency(row,first_fang=False,first_finisher=False):
 n=row['name']
 if n in BURST:return BURST[n]
 if n in NORMAL:return NORMAL[n]
 if n in FINISH:return 400 if first_finisher else 500
 if n in {'Steel Fangs','Reaving Fangs'}:return 200 if first_fang else 300
 return 0

def score(rows,fixture):
 sources=collections.defaultdict(lambda:{'potency':0,'burstPotency':0,'hits':0});total=burst=0;fang=finisher=False;hits=[]
 for row in rows:
  p=potency(row,not fang,not finisher)
  uncertain='fang' if row['name'] in {'Steel Fangs','Reaving Fangs'} and fang else 'finisher' if row['name'] in FINISH and finisher else ''
  if row['name'] in FINISH:finisher=True
  if row['name'] in {'Steel Fangs','Reaving Fangs'}:fang=True
  if not p:continue
  bp=p if row['name'] in BURST else 0;total+=p;burst+=bp
  active=sorted({w['name'] for w in fixture['damageWindows'] if w['start']<=row['t']<w['end'] and (w['target']==0 or w['target']==row.get('target',0)) and w['name']!="Hunter's Instinct"})
  for source in active:
   sources[source]['potency']+=p;sources[source]['burstPotency']+=bp;sources[source]['hits']+=1
  direct=1.0
  for source in active:direct*=1+ORDINARY.get(source,0)
  hunter=row.get('hunter')
  if hunter is None:hunter=any(w['name']=="Hunter's Instinct" and w['start']<=row['t']<w['end'] for w in fixture['damageWindows'])
  # Variable-strength Technical/Finale, potion, partner, crit and DH stay separate coverage axes.
  hits.append({'t':row['t'],'name':row['name'],'target':row.get('target',0),'potency':p,'normalPotencyAssumption':uncertain,'exactBurstFamily':bool(bp),'sources':active,'knownFixedOrdinaryMultiplier':round(direct,8),'ownHunterMultiplier':1.1 if hunter else 1.0,'recordedReferenceMultiplier':row.get('multiplier')})
 return {'potencyApprox':total,'exactBurstFamilyPotency':burst,'perSource':dict(sorted(sources.items())),'knownFixedOrdinaryWeightedPotencyApprox':round(sum(h['potency']*h['knownFixedOrdinaryMultiplier'] for h in hits),3),'hits':hits}

def sensitivity(before,after,burst_only=False):
 """Scenario bounds are assumptions, not the players' measured stats or potion effects."""
 axes={'baseCritChance':[.10,.25,.40],'baseDHChance':[0,.25,.50],'critDamageMultiplier':[1.4,1.6,1.8],'Technical Finish':[1.01,1.02,1.03,1.05],'Radiant Finale':[1.02,1.04,1.06],'Standard Finish':[1.02,1.05],'Medicated':[1.03,1.07,1.12]}
 if not burst_only:axes.update({'fangPotencyFactor':[2/3,1],'finisherPotencyFactor':[.8,1]})
 groups=collections.defaultdict(float)
 for sign,scored in [(-1,before),(1,after)]:
  for h in scored['hits']:
   if burst_only and not h['exactBurstFamily']:continue
   groups[(tuple(h['sources']),h['ownHunterMultiplier'],h['normalPotencyAssumption'])]+=sign*h['potency']*h['knownFixedOrdinaryMultiplier']
 low=(float('inf'),None);high=(-float('inf'),None)
 for values in itertools.product(*axes.values()):
  factors=dict(zip(axes,values));delta=0
  for (sources,hunter,normal),p in groups.items():
   multiplier=hunter
   crit=factors['baseCritChance'];dh=factors['baseDHChance'];crit_damage=factors['critDamageMultiplier']
   extra_crit=.10*sum(s in sources for s in ['Battle Litany','Chain Stratagem'])+.20*('Devilment' in sources)
   extra_dh=.20*sum(s in sources for s in ['Battle Voice','Devilment'])
   multiplier*=(1+min(1,crit+extra_crit)*(crit_damage-1))/(1+crit*(crit_damage-1))
   multiplier*=(1+min(1,dh+extra_dh)*.25)/(1+dh*.25)
   if normal:multiplier*=factors.get(normal+'PotencyFactor',1)
   for source in sources:
    if source in factors:multiplier*=factors[source]
   delta+=p*multiplier
  if delta<low[0]:low=(delta,factors)
  if delta>high[0]:high=(delta,factors)
 scenarios=1
 for v in axes.values():scenarios*=len(v)
 return {'minimumDelta':round(low[0],3),'maximumDelta':round(high[0],3),'minimumScenario':low[1],'maximumScenario':high[1],'scenarios':scenarios,'assumptions':axes}

def reference_resources(f):
 gauge=coils=0;token_end=-1;overflow=[];gauge_loss=0;free_count=paid_count=0
 for row in f['reference']:
  n=row['name'];gain=10 if n in FINISH else 5 if n in ["Hunter's Coil","Swiftskin's Coil"] else 0;gauge_loss+=max(0,gauge+gain-100);gauge=min(100,gauge+gain)
  if n in ['Vicewinder','Dreadwinder',"Serpent's Ire"]:
   if coils==3:overflow.append({'t':row['t'],'name':n})
   coils=min(3,coils+1)
  if n=="Serpent's Ire":token_end=row['t']+30
  if n=='Uncoiled Fury':coils-=1
  if n=='Reawaken':
   if row['t']<token_end:free_count+=1;token_end=-1
   else:paid_count+=1;gauge-=50
  assert min(gauge,coils)>=0,(f['label'],row)
 return {'inferredCoilOverflow':len(overflow),'inferredGaugeOverflow':gauge_loss,'coilOverflowActions':overflow,'endGauge':gauge,'endCoils':coils,'freeReawakens':free_count,'paidReawakens':paid_count,'assumption':'Current level-100 rules; pull gauge/coils zero; no unlogged gauge reset. This is cast reconstruction, not observed gauge telemetry.'}

def main():
 fixtures=Path(sys.argv[1]);before=Path(sys.argv[2]);after=Path(sys.argv[3]);out=Path(sys.argv[4]);out.mkdir(parents=True,exist_ok=True)
 summaries={label:{r['label']:r for r in json.loads((folder/'summary.json').read_text())} for label,folder in [('before',before),('after',after)]}
 provenance={label:json.loads((folder/'source-hashes.json').read_text()) if (folder/'source-hashes.json').exists() else None for label,folder in [('before',before),('after',after)]}
 lines=['# VPR timing replay: per-hit source coverage','','Current official level-100 potencies are summed at each selected action snapshot time. Sources remain separate: Litany/Chain alter crit chance and Voice alters DH chance; they are not ordinary damage multipliers. No hidden gear/main-stat, potion cap, base crit/DH or damage variance is fabricated. This is expected-potency coverage evidence, not DPS/rDPS.','','Normal-chain scoring is approximate (representative finisher variants, favorable positional/venom, first Fang 200 then Honed 300, first finisher 400 then venom 500). Reawaken/Fury-family potencies are exact current-guide constants. Historical hits are normalized to current potencies. Exact buff strengths for variable Finale/Technical and potion/partner effects remain separate coverage axes. Known fixed ordinary multipliers are reported only as a partial proxy, excluding those unknown factors.','','| Fight | Model exact burst-family potency | Model full-action potency approximation |','|---|---|---|']
 all_results={}
 for fp in sorted(fixtures.glob('*.json')):
  f=json.loads(fp.read_text());name=f['label'];before_rows=json.loads((before/(name+'-timeline.json')).read_text());after_rows=json.loads((after/(name+'-timeline.json')).read_text());b=score(before_rows,f);a=score(after_rows,f);reference=score(f['referenceHits'],f)
  all_results[name]={'before':{k:v for k,v in b.items() if k!='hits'},'after':{k:v for k,v in a.items() if k!='hits'},'reference':{k:v for k,v in reference.items() if k!='hits'},'referenceResources':reference_resources(f),'statsAvailable':f['stats'],'sensitivityFullApprox':sensitivity(b,a),'sensitivityBurstExact':sensitivity(b,a,True)}
  all_results[name]['runSummary']={label:summaries[label][name] for label in summaries}
  all_results[name]['timingCastTrace']=[{k:r.get(k) for k in ['t','name','reason','timing','finalKillIn','territoryId','baseId','candidateFinalKillIn']} for r in after_rows if r['name'] in ['Reawaken','Uncoiled Fury','Vicewinder',"Serpent's Ire"] and ('Advance = True' in r.get('timing','') or 'FuryBefore = True' in r.get('timing','') or 'ViceBefore = True' in r.get('timing','') or 'hit schedule' in r['reason'])]
  for label,value in [('before',b),('after',a),('reference',reference)]:
   (out/(name+'-'+label+'-hits.json')).write_text(json.dumps(value['hits'],indent=2))
  lines.append(f"| {name} | {b['exactBurstFamilyPotency']} -> {a['exactBurstFamilyPotency']} | {b['potencyApprox']} -> {a['potencyApprox']} |")
 lines+=['','## Per-source changes','','Each cell is old -> new approximate total covered potency, followed by exact burst-family covered potency. A decrease is retained visibly; this report does not declare every source trade a gain.','']
 for name,r in all_results.items():
  lines+=['### '+name,'','| Source | Total covered potency | Burst-family covered potency |','|---|---|---|']
  for source in sorted(set(r['before']['perSource'])|set(r['after']['perSource'])):
   b=r['before']['perSource'].get(source,{'potency':0,'burstPotency':0});a=r['after']['perSource'].get(source,{'potency':0,'burstPotency':0});lines.append(f"| {source} | {b['potency']} -> {a['potency']} | {b['burstPotency']} -> {a['burstPotency']} |")
  lines+=['']
 lines+=['## Assumed-factor sensitivity','','This complementary indicator uses official fixed ordinary buff factors and own Hunter 1.10. Chance buffs add their actual percentage points before expectation: Litany/Chain +10 points crit; Voice +20 points DH; Devilment +20 points each. Expected crit factor is (1 + capped chance * (crit multiplier - 1)); expected DH factor is (1 + capped chance * 0.25), both normalized against the assumed unbuffed expectation. Unknown base crit chance 0.10/0.25/0.40, base DH 0/0.25/0.50 and crit multiplier 1.4/1.6/1.8 are deliberately hypothetical, not player stats. Technical factors 1.01/1.02/1.03/1.05, Finale 1.02/1.04/1.06, Standard 1.02/1.05 and potion 1.03/1.07/1.12 are swept independently. The full approximation also sweeps repeated Fang 200/300 and finisher 400/500 to expose its simplified Honed/venom boundary. This gives 7,776 full-action scenarios and 1,944 exact burst-family scenarios. The candidate-minus-baseline range is normalized potency, not a confidence interval, actual damage or proof of improvement.','','| Fight | Full-action approximation delta range | Exact burst-family delta range |','|---|---|---|']
 for name,r in all_results.items():
  a=r['sensitivityFullApprox'];b=r['sensitivityBurstExact'];lines.append(f"| {name} | {a['minimumDelta']} to {a['maximumDelta']} | {b['minimumDelta']} to {b['maximumDelta']} |")
 lines+=['','Official potency source: [Viper job guide](https://na.finalfantasyxiv.com/jobguide/viper/). Ordinary versus chance-buff classifications were independently checked against each official job guide. Calculateddamage timestamps, rather than delayed damage receipt, identify observed snapshot times. The raw hit multiplier is retained for inspection, not treated as proof of any unknown stat or as a future-action input.']
 lines+=['','## Whole-fight checks and timing branches','','| Fight | Completed sequences | Coil loss | Gauge loss | Token expiry |','|---|---|---|---|---|']
 for name,r in all_results.items():
  b=r['runSummary']['before'];a=r['runSummary']['after'];lines.append(f"| {name} | {b['counts']['Ouroboros']} -> {a['counts']['Ouroboros']} | {b['overcapCoils']} -> {a['overcapCoils']} | {b['overcapGauge']} -> {a['overcapGauge']} | {b['TokenExpired']} -> {a['TokenExpired']} |")
 lines+=['','Actual selected timing branch actions and their chosen horizons are exported as timingCastTrace in coverage.json; prediction calls do not advance game resources. Each native replay rejects resource violations, unfinished/restarted Reawaken sequences and active-target GCD stalls above two seconds.','', 'Source snapshots:', '```json',json.dumps(provenance,indent=2),'```']
 lines+=['','## Remaining reference differences','','The reference resource reconstruction infers 12 lost coils in DMU and 8 in FRU, versus zero capacity loss in the original model. DMU begins losing generated coils at Vicewinder 58.556s/97.634s, so the difference is not limited to a final kill or cinematic transition. Extra Fury GCDs also add damage, so matching the reference coil waste is not itself an improvement target. Current native readiness, policy choices, representative combos, movement and historical rules all constrain parity; changing a timing policy alone does not establish full top-player equivalence. See coverage.json for each inferred overflow event and explicit zero-start/no-hidden-reset assumptions.']
 (out/'coverage.json').write_text(json.dumps(all_results,indent=2));(out/'COVERAGE.md').write_text('\n'.join(lines)+'\n');print('\n'.join(lines[:13]))

assert potency({'name':'Reawaken'})==750
assert potency({'name':'Hindsbane Fang'},first_finisher=True)==400
assert potency({'name':'Hindsbane Fang'})==500
assert 'Battle Litany' not in ORDINARY and CHANCE['Battle Voice']==('dh',.20)
_test=score([{'t':1,'name':'Reawaken','target':1},{'t':1,'name':'Ouroboros','target':2},{'t':2,'name':'Reawaken','target':2}],{'damageWindows':[{'name':'Chain Stratagem','start':0,'end':2,'target':2}]})
assert _test['perSource']['Chain Stratagem']['burstPotency']==1150
assert _test['exactBurstFamilyPotency']==2650
if __name__=='__main__':main()
