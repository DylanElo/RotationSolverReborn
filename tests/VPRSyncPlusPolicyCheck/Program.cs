using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using System.Runtime.Loader;

// Regression cover for VPR_SyncPlus.
if (args.Length != 1)
{
	Console.Error.WriteLine("Usage: VPRSyncPlusPolicyCheck <RotationSolver.dll>");
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
var scheduler = assembly.GetType($"{ns}VPRSyncPlusScheduler", true)!;
var policy = assembly.GetType($"{ns}VPRSyncPlusPolicy", true)!;
var snapshotType = assembly.GetType($"{ns}VPRSyncPlusSnapshot", true)!;
var decisionType = assembly.GetType($"{ns}VPRSyncPlusDecision", true)!;
var gateType = assembly.GetType($"{ns}VPRSyncPlusBurstGate", true)!;
var planType = assembly.GetType($"{ns}VPRSyncPlusResourcePlan", true)!;

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
	bool burstReady = true,
	bool burstReadySoon = true,
	float burstHeldFor = 0f,
	bool timelineActive = false,
	float downtimeIn = float.MaxValue,
	float vulnerableIn = float.MaxValue,
	float vulnerableEndIn = float.MaxValue,
	bool partyBurstActive = false,
	bool fullPartyBurstActive = false,
	float secondsInPartyBurst = 0f,
	float? predictedPartyBurstIn = null,
	float payloadWindow = 9.5f,
	float maximumHold = 30f,
	float overlapWait = 6f,
	float lookAhead = 8f,
	bool freeReawaken = false)
	=> snapshotCtor.Invoke([
		autoBurst,
		reserved,
		synchronizeParty,
		requireLiveParty,
		burstReady,
		burstReadySoon,
		burstHeldFor,
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
		freeReawaken,
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

CheckDecision("Live party burst commits Reawaken",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: true), commit, spend, "raid buffs are up");

CheckDecision("Partial party burst waits for overlap",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: false, secondsInPartyBurst: 1f, overlapWait: 6f),
	hold, bank, "waiting for the rest");

CheckDecision("Partial party burst commits once overlap wait expires",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: false, secondsInPartyBurst: 7f, overlapWait: 6f),
	commit, spend, "raid buffs are up");

CheckDecision("Imminent downtime dumps and withholds Reawaken",
	Snapshot(timelineActive: true, downtimeIn: 5f, payloadWindow: 9.5f),
	hold, spend, "downtime too close");

CheckDecision("Downtime hold ceiling commits when ceiling reached",
	Snapshot(timelineActive: true, downtimeIn: 5f, payloadWindow: 9.5f, burstHeldFor: 35f, maximumHold: 30f),
	commit, spend, "hold ceiling reached");

CheckDecision("Predicted party burst holds and banks gauge",
	Snapshot(predictedPartyBurstIn: 5f, lookAhead: 8f),
	hold, bank, "raid buffs predicted");

// 2. Pure Policy Checks
var shouldUseReawaken = Method(policy, "ShouldUseReawaken");
Check("Reawaken allowed when committed, buffs valid, target alive", true,
	shouldUseReawaken.Invoke(null, [true, true, true, true, false, true, false, false, false, false]));
Check("Reawaken blocked when Swiftscaled drops mid-burst", false,
	shouldUseReawaken.Invoke(null, [true, true, false, true, false, true, false, false, false, false]));
Check("Reawaken blocked when Hunter's Instinct drops mid-burst", false,
	shouldUseReawaken.Invoke(null, [true, true, true, false, false, true, false, false, false, false]));
Check("Reawaken blocked when downtime is within execution window (< 9.5s)", false,
	shouldUseReawaken.Invoke(null, [true, true, true, true, false, true, true, true, false, false]));
Check("Reawaken forces spend when Serpent Offering is capped at 100", true,
	shouldUseReawaken.Invoke(null, [false, true, true, true, false, true, false, false, true, false]));

var shouldUseSerpentsIre = Method(policy, "ShouldUseSerpentsIre");
Check("Serpent's Ire allowed with room for its coil", true,
	shouldUseSerpentsIre.Invoke(null, [true, true, true]));
Check("Serpent's Ire blocked when coils are full", false,
	shouldUseSerpentsIre.Invoke(null, [true, false, true]));


// Real reset method and inherited hook wiring, not only standalone boolean policies.
var resetPolicy = Method(policy, "ShouldResetEncounter");
Check("new fight detected even without an out-of-combat frame", true, resetPolicy.Invoke(null, [300f, 1f, true]));
Check("combat exit resets learned windows", true, resetPolicy.Invoke(null, [300f, 300f, false]));
Check("normal combat preserves learned windows", false, resetPolicy.Invoke(null, [10f, 11f, true]));
Check("idle frames do not repeatedly reset", false, resetPolicy.Invoke(null, [0f, 0f, false]));
var rotation = assembly.GetType($"{ns}VPR_SyncPlus", true)!;
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

Check("base Reawaken consults candidate hook", true, gcdCalls.Contains("TryUseReawaken"));
Check("base Fury consults candidate hook on every candidate path", true, gcdCalls.Count(n => n == "TryUseUncoiledFury") >= 6);
foreach (var actionHook in new[] { "TryUseReawaken", "TryUseUncoiledFury" })
{
    var hook = rotation.GetMethod(actionHook, BindingFlags.Instance | BindingFlags.NonPublic)!;
    Check("Sync owns " + actionHook, rotation, hook.DeclaringType);
    var hookCalls = Calls(hook);
    Check(actionHook + " retains native eligibility and candidate policy", true,
        hookCalls.Contains("CanUse") && hookCalls.Contains("CandidateAllowsBurst"));
}
Check("base Ire consults the override", true,
    Calls(baseRotation.GetMethod("AttackAbility", BindingFlags.Instance | BindingFlags.NonPublic)!).Contains("get_AllowSerpentsIre"));
foreach (var gateName in new[] { "AllowReawaken", "AllowSerpentsIre", "AllowUncoiledFury" })
    Check("Sync owns " + gateName, rotation, rotation.GetProperty(gateName, BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
Check("capped gauge does not skip upkeep safety", false,
    shouldUseReawaken.Invoke(null, [false, true, false, true, false, true, false, false, true, false]));
Check("capped gauge does not skip downtime safety", false,
    shouldUseReawaken.Invoke(null, [false, true, true, true, false, true, true, true, true, false]));
var pool = Method(policy, "ShouldPoolGauge");
Check("save paid Reawaken for approaching Ire", true, pool.Invoke(null, [true, false, false, 50, true, null, false]));
Check("free Reawaken is not banked by paid-gauge rule", false, pool.Invoke(null, [true, false, true, 50, true, null, false]));
Check("live buffs release banked gauge", false, pool.Invoke(null, [true, true, false, 50, true, null, false]));
Check("100 gauge can be spent", false, pool.Invoke(null, [true, false, false, 100, true, null, false]));
Check("personal mode does not pool for party", false, pool.Invoke(null, [false, false, false, 50, true, null, false]));
Check("distant burst allows filler Reawaken", false, pool.Invoke(null, [true, false, false, 50, false, 70f, false]));
var fury = Method(policy, "ShouldSpendUncoiledFury");
Check("Fury is held in melee before buffs", false, fury.Invoke(null, [true, false, 2, 3, true, false]));
Check("full coils cannot be held", true, fury.Invoke(null, [true, false, 3, 3, true, false]));
Check("Fury preserves ranged uptime", true, fury.Invoke(null, [true, false, 2, 3, false, false]));
Check("Fury spends inside live buffs", true, fury.Invoke(null, [true, true, 2, 3, true, false]));

// Sugar Riot: 80 gauge + free Reawaken, then staggered Embolden/Searing Light.
var burstWindow = Method(policy, "PlannedBurstWindow");
Check("third affordable sequence does not force the full pool into the current combo window", 21.5f,
    burstWindow.Invoke(null, [true, 100, 9.5f, 2.5f]));
var releasePotion = Method(policy, "ShouldReleasePotion");
Check("Light paid burst fits nonoverlapping potion window", true,
    releasePotion.Invoke(null, [false,50,44f,16.75f,1000f,1000f,9.5f]));
Check("no potion keeps raid pooling", false,
    releasePotion.Invoke(null, [false,50,44f,0f,1000f,1000f,9.5f]));
Check("short potion cannot justify incomplete payload", false,
    releasePotion.Invoke(null, [false,50,44f,4f,1000f,1000f,9.5f]));
Check("overlapping potion and upcoming raid window retains pooling", false,
    releasePotion.Invoke(null, [false,50,8f,20f,1000f,1000f,9.5f]));
Check("free token cannot claim potion release of paid gauge", false,
    releasePotion.Invoke(null, [true,100,44f,20f,1000f,1000f,9.5f]));
Check("unknown future raid window preserves existing decision", false,
    releasePotion.Invoke(null, [false,50,null,30f,1000f,1000f,9.5f]));
Check("imminent downtime vetoes potion release", false,
    releasePotion.Invoke(null, [false,50,44f,30f,1000f,5f,9.5f]));
Check("short target lifetime vetoes potion release", false,
    releasePotion.Invoke(null, [false,50,44f,30f,5f,1000f,9.5f]));
var finishCombo = Method(policy, "ShouldFinishNormalCombo");
var delayVice = Method(policy, "ShouldDelayViceCombo");
var overlap = Method(policy, "EffectiveOverlapWait");
foreach (var gcd in new[] { 2.08f, 2.14f, 2.5f })
{
	var pair = (float)burstWindow.Invoke(null, [true, 80, 9.5f, gcd])!;
	Check($"{gcd}: Ire plus 80 gauge budgets both payloads", 19f + gcd, pair);
	Check($"{gcd}: paid double at cap also budgets both", pair,
		burstWindow.Invoke(null, [false, 100, 9.5f, gcd]));
	Check($"{gcd}: opener free use with no gauge is single", 9.5f,
		burstWindow.Invoke(null, [true, 0, 9.5f, gcd]));
	Check($"{gcd}: after first free use, 80 gauge budgets one", 9.5f,
		burstWindow.Invoke(null, [false, 80, 9.5f, gcd]));
	var wait = (float)overlap.Invoke(null, [19.8f, pair, 6f])!;
	Check($"{gcd}: do not spend six seconds of a double-burst buff", 0f, wait);
	CheckDecision($"{gcd}: staggered raid buff releases the pair immediately",
		Snapshot(partyBurstActive: true, overlapWait: wait), commit, spend, "raid buffs are up");
	Check($"{gcd}: combo at 16s must finish before the pair", true,
		finishCombo.Invoke(null, [true, 16f, pair, gcd]));
	Check($"{gcd}: fresh 28s combo survives the pair", false,
		finishCombo.Invoke(null, [true, 28f, pair, gcd]));
	Check($"{gcd}: completed combo does not block burst", false,
		finishCombo.Invoke(null, [false, 8f, pair, gcd]));
	Check($"{gcd}: expired combo does not deadlock burst", false,
		finishCombo.Invoke(null, [true, 0f, pair, gcd]));
	Check($"{gcd}: short hold must not start Vicewinder", true,
		delayVice.Invoke(null, [true, true, false, 2f, 3f * gcd + 1f, true, true]));
	Check($"{gcd}: live partial buffs must not start Vicewinder", true,
		delayVice.Invoke(null, [true, true, true, null, 3f * gcd + 1f, true, true]));
}
Check("single burst still permits a useful partial-overlap wait", 6f,
	overlap.Invoke(null, [19.8f, 9.5f, 6f]));
Check("distant burst allows Vicewinder", false,
	delayVice.Invoke(null, [true, true, false, 30f, 8f, true, false]));
Check("unknown future buffs do not indefinitely hold Vicewinder", false,
	delayVice.Invoke(null, [true, true, false, null, 8f, true, true]));
Check("Vicewinder may restore short personal buffs", false,
	delayVice.Invoke(null, [true, true, true, null, 8f, false, true]));
Check("no Reawaken resource does not block Vicewinder", false,
	delayVice.Invoke(null, [true, false, true, null, 8f, true, true]));
Check("personal burst mode keeps normal Vicewinder behavior", false,
	delayVice.Invoke(null, [false, true, true, null, 8f, true, false]));
Check("slightly late predicted buff still prevents Vice lockout during hold", true,
	delayVice.Invoke(null, [true, true, false, -1f, 8f, true, true]));
Check("expired hold releases Vicewinder when predicted buffs never arrived", false,
	delayVice.Invoke(null, [true, true, false, -31f, 8f, true, false]));
Check("both ST and AOE Vice entry paths consult the Sync candidate hook", 4,
	gcdCalls.Count(n => n == "TryUseViceCombo"));
foreach (var gateName in new[] { "AllowReawaken", "AllowUncoiledFury", "AllowViceCombo" })
	Check("combo deadline reaches " + gateName, true,
		Calls(rotation.GetProperty(gateName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!)
			.Contains("get_FinishNormalCombo"));
Check("overlap calculation reads planned burst length", true,
	Calls(rotation.GetMethod("EvaluateBurstDecision", BindingFlags.Instance | BindingFlags.NonPublic)!)
		.Contains("get_PlannedBurstWindow"));
Check("actual burst-length property invokes tested policy", true,
	Calls(rotation.GetProperty("PlannedBurstWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!)
		.Contains("PlannedBurstWindow"));
Check("actual combo deadline invokes tested policy", true,
	Calls(rotation.GetProperty("FinishNormalCombo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!)
		.Contains("ShouldFinishNormalCombo"));
Check("actual Vice entry invokes tested policy", true,
	Calls(rotation.GetProperty("AllowViceCombo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!)
		.Contains("ShouldDelayViceCombo"));

// Normal combo has time, but the independent finisher venom does not.
var deadline = Method(policy, "ComboDeadline");
Check("5:00 Vice entry protects venom with only five seconds left", true,
	finishCombo.Invoke(null, [true, deadline.Invoke(null, [27.9f, 5.36f]), 9.5f, 2.14f]));
Check("venom deadline takes precedence over a fresh combo", 5.36f,
	deadline.Invoke(null, [27.9f, 5.36f]));
Check("normal timer still takes precedence when sooner", 4f,
	deadline.Invoke(null, [4f, 50f]));
Check("no venom preserves normal combo deadline", 28f,
	deadline.Invoke(null, [28f, 0f]));
Check("fresh venom and combo allow a single detour", false,
	finishCombo.Invoke(null, [true, deadline.Invoke(null, [28f, 55f]), 9.5f, 2.14f]));
Check("expired combo cannot deadlock on a remaining venom", false,
	finishCombo.Invoke(null, [true, deadline.Invoke(null, [0f, 5f]), 9.5f, 2.14f]));
Check("live combo gate shares the planner's combined deadline", true,
    Calls(rotation.GetProperty("FinishNormalCombo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!).Contains("get_NormalComboRemaining"));
var finishCalls = Calls(rotation.GetProperty("NormalComboRemaining", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!);
Check("live combo protection reads status duration", true, finishCalls.Contains("PlayerStatusTime"));
Check("live combo protection uses combined deadline", true, finishCalls.Contains("ComboDeadline"));

CheckDecision("6:02 free Ire with predicted Litany in 12 seconds waits",
	Snapshot(predictedPartyBurstIn: 12f, freeReawaken: true), hold, bank, "raid buffs predicted");
CheckDecision("no free Ire keeps the existing eight-second prediction horizon",
	Snapshot(predictedPartyBurstIn: 12f), commit, spend, "no reason to wait");
CheckDecision("free opener with no learned prediction is not stalled",
	Snapshot(freeReawaken: true), commit, spend, "no reason to wait");
CheckDecision("free Ire in personal mode ignores predictions",
	Snapshot(synchronizeParty: false, predictedPartyBurstIn: 12f, freeReawaken: true), commit, spend, "no party sync");
CheckDecision("free Ire does not wait for a distant future cycle",
	Snapshot(predictedPartyBurstIn: 60f, freeReawaken: true), commit, spend, "no reason to wait");
CheckDecision("late Litany still arrives within the bounded hold",
	Snapshot(predictedPartyBurstIn: -10f, burstHeldFor: 24f, freeReawaken: true), hold, bank, "raid buffs predicted");
CheckDecision("missing Litany cannot extend the hold ceiling",
	Snapshot(predictedPartyBurstIn: -18f, burstHeldFor: 30f, freeReawaken: true), commit, spend, "hold ceiling reached");
CheckDecision("actual Litany releases held Ire",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: true, freeReawaken: true), commit, spend, "raid buffs are up");
CheckDecision("last full window before downtime still wins over an Ire hold",
	Snapshot(timelineActive: true, downtimeIn: 12f, predictedPartyBurstIn: 12f, freeReawaken: true),
	commit, spend, "last full window before downtime");
Check("free Reawaken at 100 gauge cannot claim to spend that gauge", false,
	shouldUseReawaken.Invoke(null, [false, true, true, true, false, true, false, false, true, true]));
Check("expiring free token can still be released by the commit override", true,
	shouldUseReawaken.Invoke(null, [true, true, true, true, false, true, false, false, true, true]));
Check("free token still respects actual imminent downtime", false,
	shouldUseReawaken.Invoke(null, [true, true, true, true, false, true, true, true, true, true]));
Check("live Reawaken gate provides free-token state", true,
	Calls(rotation.GetProperty("ShouldUseReawaken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!)
		.Contains("get_HasReadyToReawaken"));
Check("live snapshot provides free-token state", true,
	Calls(rotation.GetMethod("BuildSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!)
		.Contains("get_HasReadyToReawaken"));
Check("actual scheduler uses the shared baseline snapshot", true,
    Calls(rotation.GetMethod("EvaluateBurstDecision", BindingFlags.Instance | BindingFlags.NonPublic)!).Contains("BuildSnapshot"));

// Compose the scheduler's release with the real paid-gauge veto.
var forceProp = decisionType.GetProperty("ForceRelease")!;
var finalWindow = evaluate.Invoke(null, [Snapshot(timelineActive: true, downtimeIn: 12f)])!;
var forced = (bool)forceProp.GetValue(finalWindow)!;
Check("last safe window carries an explicit release", true, forced);
Check("ordinary Ire pooling cannot veto last safe Reawaken", false,
    pool.Invoke(null, [true, false, false, 50, true, null, forced]));
Check("release still cannot bypass target or upkeep gates", false,
    shouldUseReawaken.Invoke(null, [true, true, false, true, false, true, true, false, false, false]));
var holdClock = Method(policy, "UpdateHoldStartedAt");
float heldAt = 100f;
object previousGate = hold;
foreach (var now in new[] { 129.9f, 130f, 130.1f, 132.2f }) {
    heldAt = (float)holdClock.Invoke(null, [heldAt, now, true, Equals(previousGate, hold)])!;
    var decision = evaluate.Invoke(null, [Snapshot(requireLiveParty: true, burstHeldFor: now-heldAt)])!;
    previousGate = gateProp.GetValue(decision)!;
    Check($"{now}: no cast preserves original hold clock", 100f, heldAt);
    Check($"{now}: release remains available after blocked frame", now < 130 ? hold : commit, previousGate);
    if (now >= 130) Check($"{now}: ceiling release bypasses ordinary pooling", false,
        pool.Invoke(null, [true, false, false, 50, true, null, forceProp.GetValue(decision)]));
}
Check("actual Reawaken entry clears the consumed hold", -1f, holdClock.Invoke(null,[heldAt,133f,false,false]));
Check("unheld filler readiness does not start a hold", -1f, holdClock.Invoke(null,[-1f,150f,true,false]));
Check("new hold starts with a fresh budget", 151f, holdClock.Invoke(null,[-1f,151f,true,true]));
Check("actual tracker uses tested hold transition", true, trackerCalls.Contains("UpdateHoldStartedAt"));
Check("actual tracker observes Reawaken entry without diagnostic logging", true, trackerCalls.Contains("get_HasReawakenedActive"));
Check("actual final Reawaken gate consumes forced release", true,
    Calls(rotation.GetProperty("AllowReawaken",BindingFlags.Instance|BindingFlags.NonPublic)!.GetMethod!).Contains("get_ForceRelease"));

var secondMedicine = Method(policy, "ShouldUseSecondMedicine");
Check("later potion allowed before selected executable Reawaken", true,
	secondMedicine.Invoke(null, [true, 360f, 360f, true, true, true]));
Check("later potion respects disabled setting", false,
	secondMedicine.Invoke(null, [false, 360f, 360f, true, true, true]));
Check("later potion respects earliest time", false,
	secondMedicine.Invoke(null, [true, 359f, 360f, true, true, true]));
Check("later potion respects burst reservation", false,
	secondMedicine.Invoke(null, [true, 360f, 360f, false, true, true]));
Check("later potion respects upkeep combo and gauge vetoes", false,
	secondMedicine.Invoke(null, [true, 360f, 360f, true, false, true]));
Check("ready gauge without selected Reawaken cannot trigger potion", false,
	secondMedicine.Invoke(null, [true, 360f, 360f, true, true, false]));
Check("no-target fallback cannot trigger later potion", false,
	secondMedicine.Invoke(null, [true, 360f, 360f, true, false, false]));
var emergencyCalls = Calls(rotation.GetMethod("EmergencyAbility", BindingFlags.Instance | BindingFlags.NonPublic)!);
foreach (var gate in new[] { "CandidateAllowsBurst", "ShouldUseSelectedBurstMedicine", "get_NoAbilityReady", "IsTheSameTo", "get_ReawakenPvE", "UseBurstMedicine" })
	Check($"live potion path retains {gate}", true, emergencyCalls.Contains(gate));
var selectedMedicineCalls = Calls(rotation.GetMethod("ShouldUseSelectedBurstMedicine", BindingFlags.Instance | BindingFlags.NonPublic)!);
foreach (var gate in new[] { "ShouldUseSecondMedicine", "get_AllowBurstMedicine", "get_AllowReawaken" })
    Check($"candidate potion policy retains {gate}", true, selectedMedicineCalls.Contains(gate));
Check("live potion action match does not use object equality", false, emergencyCalls.Contains("Equals"));
Check("live potion path no longer treats resource readiness as execution", false,
	emergencyCalls.Contains("get_ReawakenReady"));

Console.WriteLine();
if (failures > 0)
{
	Console.Error.WriteLine($"{failures} VPR Sync check(s) FAILED.");
	return 1;
}

Console.WriteLine("All VPR Sync checks passed.");
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
