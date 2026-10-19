using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using RotationSolver.RebornRotations.Melee;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var path = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var p = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
    if (!File.Exists(p)) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher/addon/Hooks/dev", name.Name + ".dll");
    return File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var stateType = assembly.GetType(typeof(SAMCycleState).FullName!, true)!;
var policy = assembly.GetType(typeof(SAMSyncCycle).FullName!, true)!;
var rotation = assembly.GetType("RotationSolver.RebornRotations.Melee.SAM_SyncPlus", true)!;
foreach(var (caller,callee) in new[]{("AttackAbility","UseMeikyo"),("GeneralGCD","Select")}) {
    Check(Calls(rotation.GetMethod(caller,BindingFlags.Instance|BindingFlags.NonPublic)!)
        .Any(m=>m.DeclaringType==policy&&m.Name==callee),$"live {caller} is not wired to {callee}");
}
object State(SAMCycleState s) => Activator.CreateInstance(stateType, s.Sen,s.Meikyo,s.Tendo,s.BuffsReady,s.Dot,s.Gcd,s.KaeshiRemaining,s.LiveBuffs,s.Bank,s.ChargeCapping,s.ComboStarted,s.Opener,s.Ending,s.OgiRemaining)!;
bool Meikyo(SAMCycleState s) {
    var result = SAMSyncCycle.UseMeikyo(s);
    Check(result == (bool)policy.GetMethod("UseMeikyo",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[State(s)])!, "deployed Meikyo policy differs");
    return result;
}
SAMCycleGcd Select(SAMCycleState s, bool h, bool o, bool n, bool t, bool m, bool k, bool tk) {
    var result=SAMSyncCycle.Select(s,h,o,n,t,m,k,tk);
    var loaded=policy.GetMethod("Select",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[State(s),h,o,n,t,m,k,tk]);
    Check(result.ToString()==loaded!.ToString(), "deployed GCD policy differs"); return result;
}
var basic = new SAMCycleState(1,false,false,true,60,2.14f,20,false,false,false,false,false,false);
var terminal = basic with { Ending=true, Dot=0, OgiRemaining=20 };
Check(Select(terminal,true,true,false,false,false,false,false)==SAMCycleGcd.Ogi,"ending target spends Ogi instead of fresh Higanbana");
Check(Select(terminal with { Tendo=true },true,true,false,false,false,false,false)==SAMCycleGcd.Ogi,"ending Ogi does not wait for another Tendo setup");
Check(Select(terminal,true,false,false,false,false,false,false)==SAMCycleGcd.Combo,"no terminal DoT even when Ogi unavailable");
Check(Select(terminal with { KaeshiRemaining=2 },true,true,false,false,false,true,false)==SAMCycleGcd.Kaeshi,"earlier Kaeshi deadline survives terminal Ogi priority");
Check(Calls(rotation.GetMethod("GeneralGCD",BindingFlags.Instance|BindingFlags.NonPublic)!).Any(m=>m.DeclaringType==rotation&&m.Name=="TryUseHiganbana")
    && Calls(rotation.GetMethod("TryUseHiganbana",BindingFlags.Instance|BindingFlags.NonPublic)!).Any(m=>m.DeclaringType==rotation&&m.Name=="CandidateAllowsBurst")
    && rotation.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Where(m=>m.Name.StartsWith("<TryUseHiganbana>b__"))
        .Any(m=>Calls(m).Any(c=>c.Name=="get_AllowHiganbana")),"cycle path enforces candidate-scoped terminal DoT gate");
Check(Calls(rotation.BaseType!.GetMethod("GeneralGCD",BindingFlags.Instance|BindingFlags.NonPublic)!).Any(m=>m.Name=="get_AllowHiganbana"),"inherited synced/AoE path enforces terminal DoT gate");
Check(Meikyo(basic with { Sen=0, Dot=0, Opener=true }), "second opener Meikyo before DoT");
Check(Select(basic with { Meikyo=true, Dot=0 },true,false,false,false,false,false,false)==SAMCycleGcd.Higanbana,"Higanbana during Meikyo");
Check(!Meikyo(basic with { ComboStarted=true, Dot=8 }),"finish combo before recovery Meikyo");
Check(Meikyo(basic with { Dot=8 }),"recover with Meikyo before DoT deadline");
Check(!Meikyo(basic with { Bank=true, Dot=12 }),"bank charge for delayed buffs");
Check(Meikyo(basic with { LiveBuffs=true }),"use charge in observed buffs");
Check(!Meikyo(basic with { Tendo=true, LiveBuffs=true }),"never overwrite Tendo");
Check(!Meikyo(basic with { Dot=0,LiveBuffs=true,ChargeCapping=true }),"DoT recovery precedes another Meikyo even in buffs");
Check(Select(basic with { Meikyo=true,Tendo=true,Dot=0 },true,true,false,false,false,false,false)==SAMCycleGcd.Combo,"late buff recovery builds Tendo before DoT/Ogi");
Check(Select(basic,false,false,false,false,false,true,false)==SAMCycleGcd.Combo,"carry normal Kaeshi through filler");
Check(Select(basic with { Sen=3 },false,false,false,false,true,true,false)==SAMCycleGcd.Kaeshi,"consume carried Kaeshi before next Iaijutsu");
Check(Select(basic with { KaeshiRemaining=2 },false,false,false,false,false,true,false)==SAMCycleGcd.Kaeshi,"save expiring Kaeshi");
Check(Select(basic with { Sen=3 },false,false,false,false,false,true,false)==SAMCycleGcd.Kaeshi,"instant recovery while casts unavailable");
Check(Select(basic with { Sen=3, BuffsReady=false },false,false,false,false,false,false,false)==SAMCycleGcd.Combo,"recover personal buffs after downtime");

bool Available(float remaining, float delay = 0f, float cast = 0f) {
    var value = SAMSyncCycle.RequiredStatusCovers(remaining,delay,cast);
    Check(value == (bool)policy.GetMethod("RequiredStatusCovers",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[remaining,delay,cast])!,"compiled required status horizon differs");
    return value;
}
Check(Available(2.86f),"instant Kaeshi still usable inside old two-GCD cutoff");
Check(!Available(0) && !Available(-1),"missing or expired proc cannot bypass status requirements");
Check(!Available(1.3f,0,1.3f),"proc must cover cast completion");
Check(!Available(2.8f,1.5f,1.3f),"queued cast includes remaining GCD");
Check(Available(3f,1.5f,1.3f),"queued cast with safe remaining proc is allowed");
Check(!Available(1.5f,0.4f,1.3f),"Fugetsu must cover queued GCD delay and cast completion");
Check(Available(1.9f,0.4f,1.3f),"Fugetsu with a safe queued cast horizon remains usable");
var damageBuffHook = rotation.GetMethod("DamageBuffCovers",BindingFlags.Static|BindingFlags.NonPublic)!;
var damageBuffCalls = Calls(damageBuffHook);
Check(damageBuffCalls.Any(m=>m.DeclaringType==policy&&m.Name=="RequiredStatusCovers"),"Fugetsu gate uses the tested execution horizon");
Check(damageBuffCalls.Any(m=>m.Name=="get_DefaultGCDRemain"),"Fugetsu gate includes the queued GCD delay");
var delayedKaeshi = basic with { KaeshiRemaining=5 };
Check(Select(delayedKaeshi,false,false,false,false,false,Available(5),false)==SAMCycleGcd.Combo,"safe carried Kaeshi can wait");
delayedKaeshi = delayedKaeshi with { KaeshiRemaining=2.86f };
Check(Select(delayedKaeshi,false,false,false,false,false,Available(2.86f),false)==SAMCycleGcd.Kaeshi,"next GCD actually releases usable Kaeshi");
var urgentOgi = basic with { Tendo=true,Meikyo=true,LiveBuffs=true,KaeshiRemaining=0,OgiRemaining=7 };
Check(Select(urgentOgi,false,Available(7,0,1.3f),false,false,false,false,false)==SAMCycleGcd.Ogi,"old Ogi precedes new Tendo obligation");
Check(Select(urgentOgi with { OgiRemaining=30 },false,true,false,false,false,false,false)==SAMCycleGcd.Combo,"fresh Ogi preserves ordinary Tendo order");
Check(Select(urgentOgi with { KaeshiRemaining=2 },false,true,false,false,false,true,false)==SAMCycleGcd.Kaeshi,"earlier Kaeshi deadline wins competing Ogi escape");
var readyHook = rotation.GetMethod("CanUseReadyAction",BindingFlags.Instance|BindingFlags.NonPublic)!;
Check(Calls(readyHook).Any(m=>m.DeclaringType==policy&&m.Name=="RequiredStatusCovers"),"real action hook must check proc execution horizon");
Check(Calls(rotation.GetMethod("GeneralGCD",BindingFlags.Instance|BindingFlags.NonPublic)!).Any(m=>m.Name=="CanUseReadyAction"),"GCD selector must use real availability hook");
Check(Calls(rotation.GetMethod("AttackAbility",BindingFlags.Instance|BindingFlags.NonPublic)!).Any(m=>m.Name=="CanUseReadyAction"),"Zanshin must use real availability hook");

foreach(var gcd in new[]{2.08f,2.14f}) foreach(var delayed in new[]{false,true}) {
    var events=Simulate(gcd,delayed,false);
    var opener=events.Where(e=>e.time<34).Select(e=>e.action).ToList();
    var secondMeikyo=events.First(e=>e.action=="Meikyo");
    Check(secondMeikyo.time<15,$"{gcd}: second Meikyo not in opener");
    Check(opener.IndexOf("Higanbana")<opener.IndexOf("Ogi"),$"{gcd}: opener DoT/Ogi order");
    Check(events.Count(e=>e.action=="Tendo" && e.time<34)==2,$"{gcd}: two opener Tendos");
    Check(events.Count(e=>e.action=="Higanbana")>=6,$"{gcd}: missing DoT refreshes");
    var dots=events.Where(e=>e.action=="Higanbana").Select(e=>e.time).ToArray();
    Check(dots.Zip(dots.Skip(1),(a,b)=>b-a).Max()<72,$"{gcd}: excessive DoT drift");
    for(int i=1;i<4;i++) {
        var buff=delayed?new[]{3.168f,135.622f,263.099f,391.047f}[i]:5+120*i;
        Check(events.Any(e=>e.action=="Tendo" && e.time>=buff && e.time<buff+20),$"{gcd}: missed Tendo in buff {i}, delayed={delayed}");
        Check(events.Any(e=>e.action=="TendoKaeshi" && e.time>=buff && e.time<buff+20),$"{gcd}: missed Tendo follow-up in buff {i}, delayed={delayed}");
    }
    Console.WriteLine($"SIM gcd={gcd} delayed={delayed}: "+string.Join(", ",events.Where(e=>e.action is "Tendo" or "Higanbana" or "Meikyo").Select(e=>$"{e.time:F1} {e.action}")));
    var recovered=Simulate(gcd,delayed,true);
    Check(recovered.Any(e=>e.time>230 && e.action=="Higanbana"),"downtime recovery lost DoT");
}
Console.WriteLine($"PASS {checks} cycle/scenario and compiled-policy checks");

List<(float time,string action)> Simulate(float gcd,bool delayed,bool downtime) {
    var events=new List<(float,string)>();
    int sen=0,stacks=3,charges=1,combo=0,meditation=0;
    float moon=0,flower=0,dot=0,kaeshi=0,tendoKaeshi=0,ogi=0,nami=0,meikyo=20,nextCharge=41,nextIki=4;
    bool tendo=true;
    var windows=delayed?new[]{3.168f,135.622f,263.099f,391.047f}:new[]{5f,125f,245f,365f};
    for(float time=0;time<420;time+=gcd) {
        while(time>=nextCharge) { charges=Math.Min(2,charges+1); nextCharge+=55; }
        bool live=windows.Any(w=>time>=w&&time<w+20);
        bool bank=!live&&Enumerable.Range(1,3).Any(i=>time>=windows[i-1]+112 && time<windows[i-1]+150);
        bool skip=downtime&&time>=180&&time<215;
        if(meikyo<=0) stacks=0;
        int count=System.Numerics.BitOperations.PopCount((uint)sen);
        var s=new SAMCycleState(count,stacks>0,tendo,moon>0&&flower>0,dot,gcd,kaeshi,live,bank,charges==2,combo!=0,time<30&&tendoKaeshi<=0,false);
        if(!skip) {
            if(charges>0&&tendoKaeshi<=0&&Meikyo(s)) { charges--;stacks=3;tendo=true;meikyo=20;events.Add((time,"Meikyo"));s=s with{Meikyo=true,Tendo=true}; }
            if(time>=nextIki&&(live||time<10)) { ogi=30;nextIki=time+120; }
            if(meditation==3) meditation=0;
            s=s with { OgiRemaining=ogi };
            var action=Select(s,count==1&&moon>1.3f,SAMSyncCycle.RequiredStatusCovers(ogi,0,1.3f)&&meditation<3&&moon>1.3f,nami>0,count==3&&tendo&&kaeshi<=0&&moon>1.3f,count==3&&!tendo&&moon>1.3f,SAMSyncCycle.RequiredStatusCovers(kaeshi,0,0),SAMSyncCycle.RequiredStatusCovers(tendoKaeshi,0,0));
            if(action==SAMCycleGcd.Combo) {
                int bit=0;
                if(stacks>0) {
                    if(moon<=5&&(sen&2)!=0) { moon=40;stacks--;combo=0; goto tick; }
                    if(flower<=5&&(sen&4)!=0) { flower=40;stacks--;combo=0; goto tick; }
                    if(moon<=5)bit=2;else if(flower<=5)bit=4;
                    else if((sen&2)==0&&(moon<=flower||(sen&4)!=0))bit=2;
                    else if((sen&4)==0)bit=4;else if((sen&2)==0)bit=2;else if((sen&1)==0)bit=1;
                    Check(bit!=0,$"model stalled with Meikyo at {time}, sen={sen}"); stacks--;
                } else if(combo==2)bit=2;else if(combo==3)bit=4;
                else if(combo==1) {
                    if(flower<=5){flower=40;combo=3;}else if(moon<=5){moon=40;combo=2;}
                    else if((sen&1)==0)bit=1;
                    else if((sen&2)==0&&(moon<=flower||(sen&4)!=0)){moon=40;combo=2;}
                    else {flower=40;combo=3;}
                } else combo=1;
                if(bit!=0) { Check((sen&bit)==0,$"Sen overwritten at {time}: gcd={gcd}, delayed={delayed}, downtime={downtime}, sen={sen}, bit={bit}, stacks={stacks}, combo={combo}");sen|=bit;combo=0;if(bit==2)moon=40;if(bit==4)flower=40; }
            } else {
                events.Add((time,action.ToString()));
                switch(action) {
                    case SAMCycleGcd.Higanbana: sen=0;dot=60;meditation++;break;
                    case SAMCycleGcd.Midare: sen=0;kaeshi=30;meditation++;break;
                    case SAMCycleGcd.Tendo: sen=0;tendo=false;tendoKaeshi=30;meditation++;break;
                    case SAMCycleGcd.Kaeshi: kaeshi=0;break;
                    case SAMCycleGcd.TendoKaeshi: tendoKaeshi=0;break;
                    case SAMCycleGcd.Ogi: ogi=0;nami=30;meditation++;break;
                    case SAMCycleGcd.KaeshiNamikiri: nami=0;break;
                }
            }
        }
        tick:
        moon=Math.Max(0,moon-gcd);flower=Math.Max(0,flower-gcd);dot=Math.Max(0,dot-gcd);
        kaeshi=Math.Max(0,kaeshi-gcd);tendoKaeshi=Math.Max(0,tendoKaeshi-gcd);ogi=Math.Max(0,ogi-gcd);nami=Math.Max(0,nami-gcd);meikyo-=gcd;
    }
    return events;
}

static List<MethodBase> Calls(MethodInfo method) {
    var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
    var il=method.GetMethodBody()!.GetILAsByteArray()!; var calls=new List<MethodBase>();
    for(var i=0;i<il.Length;) {
        ushort code=il[i++];if(code==0xfe)code=(ushort)(0xfe00|il[i++]);var op=codes[code];
        if(op.OperandType==OperandType.InlineMethod)calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il,i))!);
        i+=op.OperandType switch {
            OperandType.InlineNone=>0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
            OperandType.InlineVar=>2, OperandType.InlineI8 or OperandType.InlineR=>8,
            OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4,
        };
    }
    return calls;
}
