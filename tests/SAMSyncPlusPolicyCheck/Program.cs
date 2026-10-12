using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using System.Runtime.Loader;

// Regression cover for SAM_SyncPlus.
if (args.Length != 1)
{
	Console.Error.WriteLine("Usage: SAMSyncPlusPolicyCheck <RotationSolver.dll>");
	return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
	var dependency = Path.Combine(assemblyDirectory, $"{name.Name}.dll");
	if (File.Exists(dependency)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency);
    var hooks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev", $"{name.Name}.dll");
    return File.Exists(hooks) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(hooks) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
const string ns = "RotationSolver.RebornRotations.Melee.";
var scheduler = assembly.GetType($"{ns}SAMSyncPlusScheduler", true)!;
var policy = assembly.GetType($"{ns}SAMSyncPlusPolicy", true)!;
var snapshotType = assembly.GetType($"{ns}SAMSyncPlusSnapshot", true)!;
var decisionType = assembly.GetType($"{ns}SAMSyncPlusDecision", true)!;
var gateType = assembly.GetType($"{ns}SAMSyncPlusBurstGate", true)!;
var planType = assembly.GetType($"{ns}SAMSyncPlusResourcePlan", true)!;

var free = Enum.Parse(gateType, "Free");
var hold = Enum.Parse(gateType, "Hold");
var commit = Enum.Parse(gateType, "Commit");
var spend = Enum.Parse(planType, "Spend");
var bank = Enum.Parse(planType, "Bank");

static MethodInfo Method(Type owner, string name)
	=> owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(owner.FullName, name);

var evaluate = Method(scheduler, "Evaluate");
var snapshotCtor = snapshotType.GetConstructors().Single();
var gateProp = decisionType.GetProperty("Burst")!;
var planProp = decisionType.GetProperty("Resources")!;
var reasonProp = decisionType.GetProperty("Reason")!;

var failures = 0;

void Check(string name, object? expected, object? actual)
{
	if (Equals(expected, actual))
	{
		Console.WriteLine($"PASS {name}: {actual}");
		return;
	}

	failures++;
	Console.Error.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
}

object Snapshot(
	bool autoBurst = true,
	bool reserved = false,
	bool synchronizeParty = true,
	bool requireLiveParty = false,
	bool ikishotenReady = true,
	bool ikishotenReadySoon = true,
	float ikishotenHeldFor = 0f,
	bool timelineActive = false,
	float downtimeIn = float.MaxValue,
	float vulnerableIn = float.MaxValue,
	float vulnerableEndIn = float.MaxValue,
	bool partyBurstActive = false,
	bool fullPartyBurstActive = false,
	float secondsInPartyBurst = 0f,
	float? predictedPartyBurstIn = null,
	float payloadWindow = 12f,
	float maximumHold = 30f,
	float overlapWait = 6f,
	float lookAhead = 8f)
	=> snapshotCtor.Invoke([
		autoBurst,
		reserved,
		synchronizeParty,
		requireLiveParty,
		ikishotenReady,
		ikishotenReadySoon,
		ikishotenHeldFor,
		timelineActive,
		downtimeIn,
		vulnerableIn,
		vulnerableEndIn,
		partyBurstActive,
		fullPartyBurstActive,
		secondsInPartyBurst,
		predictedPartyBurstIn,
		payloadWindow,
		maximumHold,
		overlapWait,
		lookAhead,
	]);

void CheckDecision(
	string name,
	object snapshot,
	object expectedGate,
	object expectedPlan,
	string expectedReasonSubstring)
{
	var decision = evaluate.Invoke(null, [snapshot])!;
	var actualGate = gateProp.GetValue(decision);
	var actualPlan = planProp.GetValue(decision);
	var actualReason = (string)reasonProp.GetValue(decision)!;

	Check($"{name} [gate]", expectedGate, actualGate);
	Check($"{name} [plan]", expectedPlan, actualPlan);
	if (!actualReason.Contains(expectedReasonSubstring, StringComparison.OrdinalIgnoreCase))
	{
		failures++;
		Console.Error.WriteLine($"FAIL {name} [reason]: expected substring '{expectedReasonSubstring}', got '{actualReason}'");
	}
	else
	{
		Console.WriteLine($"PASS {name} [reason]: {actualReason}");
	}
}

// 1. Core Scheduler Decisions
CheckDecision("AutoBurst disabled results in Free/Spend",
	Snapshot(autoBurst: false), free, spend, "burst is off");

CheckDecision("Reserved state holds for dungeon boss",
	Snapshot(reserved: true), hold, bank, "reserved for the next boss");

CheckDecision("Live party burst commits Ikishoten",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: true), commit, spend, "raid buffs are up");

CheckDecision("Partial party burst waits for overlap",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: false, secondsInPartyBurst: 1f, overlapWait: 6f),
	hold, bank, "waiting for the rest");

CheckDecision("Partial party burst commits once overlap wait expires",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: false, secondsInPartyBurst: 7f, overlapWait: 6f),
	commit, spend, "raid buffs are up");

CheckDecision("Imminent downtime dumps Kenki and withholds Ikishoten",
	Snapshot(timelineActive: true, downtimeIn: 5f, payloadWindow: 12f),
	hold, spend, "downtime too close");

CheckDecision("Downtime remains a hard veto after alignment ceiling",
	Snapshot(timelineActive: true, downtimeIn: 5f, payloadWindow: 12f, ikishotenHeldFor: 35f, maximumHold: 30f),
	hold, spend, "downtime too close");
CheckDecision("Zero alignment hold cannot release Ikishoten one second before downtime",
	Snapshot(timelineActive: true, downtimeIn: 1f, ikishotenHeldFor: 0f, maximumHold: 0f),
	hold, spend, "downtime too close");
CheckDecision("Exact payload window remains available",
	Snapshot(synchronizeParty: false, timelineActive: true, downtimeIn: 12f, payloadWindow: 12f),
	commit, spend, "last full window");
CheckDecision("Alignment ceiling still releases when payload fits",
	Snapshot(requireLiveParty: true, ikishotenHeldFor: 35f, maximumHold: 30f),
	commit, spend, "hold ceiling reached");

CheckDecision("Predicted party burst holds and banks Kenki",
	Snapshot(predictedPartyBurstIn: 5f, lookAhead: 8f),
	hold, bank, "raid buffs predicted");

// 2. Pure Policy Checks
var shouldUseIkishoten = Method(policy, "ShouldUseIkishoten");
Check("Ikishoten allowed when committed, Kenki <= 50, target alive", true,
	shouldUseIkishoten.Invoke(null, [true, true, false, true]));
Check("Ikishoten blocked when Kenki > 50 (prevents overcap)", false,
	shouldUseIkishoten.Invoke(null, [true, false, false, true]));
Check("Ikishoten blocked when target dies before payload", false,
	shouldUseIkishoten.Invoke(null, [true, true, true, true]));
Check("Ikishoten blocked when burst gate is Hold", false,
	shouldUseIkishoten.Invoke(null, [false, true, false, true]));

var shouldHoldMeikyo = Method(policy, "ShouldHoldMeikyo");
Check("Meikyo held for burst when 1 charge banked and burst predicted in 5s", true,
	shouldHoldMeikyo.Invoke(null, [true, false, (ushort)1, (ushort)2, false, 5f, 8f]));
Check("Meikyo spent when at max charges (never overcaps charges)", false,
	shouldHoldMeikyo.Invoke(null, [true, false, (ushort)2, (ushort)2, false, 5f, 8f]));
Check("Meikyo spent when party burst is active", false,
	shouldHoldMeikyo.Invoke(null, [true, true, (ushort)1, (ushort)2, false, 5f, 8f]));

var shouldBankKenki = Method(policy, "ShouldBankKenki");
Check("Kenki banked when holding resources and current Kenki < 50", true,
	shouldBankKenki.Invoke(null, [true, 35, 50]));
Check("Kenki not banked when already at 50+", false,
	shouldBankKenki.Invoke(null, [true, 55, 50]));

var shouldDumpKenkiBeforeDowntime = Method(policy, "ShouldDumpKenkiBeforeDowntime");
Check("Kenki dumped on Shinten before downtime <= 3s", true,
	shouldDumpKenkiBeforeDowntime.Invoke(null, [true, 2.5f, 40]));
Check("Kenki not dumped if downtime is still distant", false,
	shouldDumpKenkiBeforeDowntime.Invoke(null, [true, 10f, 40]));


// Real reset method and inherited hook wiring, not only standalone boolean policies.
var resetPolicy = Method(policy, "ShouldResetEncounter");
Check("new fight detected even without an out-of-combat frame", true, resetPolicy.Invoke(null, [300f, 1f, true]));
Check("combat exit resets learned windows", true, resetPolicy.Invoke(null, [300f, 300f, false]));
Check("normal combat preserves learned windows", false, resetPolicy.Invoke(null, [10f, 11f, true]));
Check("idle frames do not repeatedly reset", false, resetPolicy.Invoke(null, [0f, 0f, false]));
var rotation = assembly.GetType($"{ns}SAM_SyncPlus", true)!;
var instance = RuntimeHelpers.GetUninitializedObject(rotation);
var anchorField = rotation.GetField("_lastObservedPartyBurstAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
var cycleField = rotation.GetField("_observedPartyBurstCycle", BindingFlags.Instance | BindingFlags.NonPublic)!;
anchorField.SetValue(instance, 250f);
cycleField.SetValue(instance, 3);
rotation.GetMethod("ResetPartyBurstAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
Check("actual tracker clears previous fight anchor", -1f, anchorField.GetValue(instance));
Check("actual tracker clears previous fight cycle", 0, cycleField.GetValue(instance));
Check("next opener can become the new anchor", true,
    Method(policy, "ShouldObservePartyBurst").Invoke(null, [anchorField.GetValue(instance), 10f, 120f]));
var trackerCalls = Calls(rotation.GetMethod("UpdateBurstTracker", BindingFlags.Instance | BindingFlags.NonPublic)!);
Check("live tracker invokes the tested reset", true, trackerCalls.Contains("ResetPartyBurstAnchor"));
Check("live tracker invokes the clock-reset predicate", true, trackerCalls.Contains("ShouldResetEncounter"));
var baseRotation = rotation.BaseType!;
var gcdCalls = Calls(baseRotation.GetMethod("GeneralGCD", BindingFlags.Instance | BindingFlags.NonPublic)!);

Check("base Ogi paths consult the override", true, gcdCalls.Count(n => n == "get_AllowOgiNamikiri") == 2);
Check("base downtime paths consult the override", true, gcdCalls.Contains("get_AllowIaijutsu"));
Check("Sync owns the Ogi gate", rotation, rotation.GetProperty("AllowOgiNamikiri", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
var payload = Method(policy, "ShouldSpendPayload");
Check("healthy ready status waits for scheduled burst", false, payload.Invoke(null, [false, 20f, 5f, 50, false]));
Check("ready status cannot expire while waiting", true, payload.Invoke(null, [false, 3f, 5f, 50, false]));
Check("Kenki cap can release Zanshin", true, payload.Invoke(null, [false, 20f, 5f, 90, false]));
Check("ending target releases payload", true, payload.Invoke(null, [false, 20f, 5f, 50, true]));
Check("committed window spends payload", true, payload.Invoke(null, [true, 20f, 5f, 50, false]));
Check("banking really holds 60 Kenki", true, shouldBankKenki.Invoke(null, [true, 60, 85]));
Check("banking releases before cap", false, shouldBankKenki.Invoke(null, [true, 85, 85]));
var spendKenki = Method(policy, "ShouldSpendKenki");
Check("pooled 60 Kenki is retained", false, spendKenki.Invoke(null, [false, false, 60, false, true]));
Check("burst spends at 25 Kenki", true, spendKenki.Invoke(null, [true, true, 25, false, true]));
Check("unbuffed normal spender keeps reserve", false, spendKenki.Invoke(null, [true, false, 25, false, true]));
Check("banking cannot deadlock ready Ikishoten at 60 Kenki", true, spendKenki.Invoke(null, [false, false, 60, true, true]));
Check("ready Ikishoten keeps exactly 50 Kenki", false, spendKenki.Invoke(null, [false, false, 50, true, true]));
Check("level-sync below Ikishoten still spends", true, spendKenki.Invoke(null, [true, false, 25, false, false]));

var seneiGate = Method(policy, "ShouldUseSenei");
Check("ending target releases Senei without casting ready Ikishoten", true, seneiGate.Invoke(null,[false,true,false,true,50,false]));
Check("ordinary delayed burst still banks Senei", false, seneiGate.Invoke(null,[false,false,false,true,50,false]));
Check("downtime releases immediately usable Senei", true, seneiGate.Invoke(null,[false,false,true,true,50,false]));
Check("Senei never consumes a ready Zanshin's 50 Kenki", false, seneiGate.Invoke(null,[true,true,true,false,50,true]));
Check("surplus Kenki allows Senei beside Zanshin", true, seneiGate.Invoke(null,[true,true,true,false,75,true]));
var medicineGate = Method(policy,"AllowMedicine");
Check("opener medicine remains enabled", true, medicineGate.Invoke(null,[true,10f,false,360f]));
Check("inherited medicine cannot bypass disabled second potion", false, medicineGate.Invoke(null,[true,400f,false,360f]));
Check("inherited medicine cannot fire at four minutes", false, medicineGate.Invoke(null,[true,240f,true,360f]));
Check("inherited medicine waits for commit", false, medicineGate.Invoke(null,[false,370f,true,360f]));
Check("inherited medicine honors later configured window", true, medicineGate.Invoke(null,[true,425f,true,420f]));
var attackCalls = Calls(rotation.GetMethod("AttackAbility",BindingFlags.Instance|BindingFlags.NonPublic)!);
Check("Zanshin availability checked before cheap downtime dump", true,
    attackCalls.IndexOf("CanUseReadyAction") >= 0 && attackCalls.IndexOf("CanUseReadyAction") < attackCalls.IndexOf("get_HissatsuShintenPvE"));
Check("actual Senei branch uses terminal release policy", true, attackCalls.Contains("ShouldUseSenei"));
Check("inherited medicine calls the virtual timing guard", true,
    Calls(rotation.BaseType!.GetMethod("GeneralAbility",BindingFlags.Instance|BindingFlags.NonPublic)!).Contains("get_AllowBurstMedicine"));
Check("Sync medicine guard invokes tested policy", true,
    Calls(rotation.GetProperty("AllowBurstMedicine",BindingFlags.Instance|BindingFlags.NonPublic)!.GetMethod!).Contains("AllowMedicine"));

Console.WriteLine();
if (failures > 0)
{
	Console.Error.WriteLine($"{failures} SAM Sync check(s) FAILED.");
	return 1;
}

Console.WriteLine("All SAM Sync checks passed.");
return 0;

// Decode operands so a coincidental byte in an IL constant cannot look like a method call.
static List<string> Calls(MethodInfo method)
{
    var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => unchecked((ushort)o.Value));
    var il = method.GetMethodBody()!.GetILAsByteArray()!;
    var calls = new List<string>();
    for (var i = 0; i < il.Length;)
    {
        ushort code = il[i++];
        if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
        var op = opcodes[code];
        if (op.OperandType == OperandType.InlineMethod)
            calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, i))!.Name);
        i += op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
            _ => 4,
        };
    }
    return calls;
}
