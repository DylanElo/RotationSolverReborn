using System.Reflection;
using System.Runtime.Loader;

// Regression cover for BLM_SyncPlus.
if (args.Length != 1)
{
	Console.Error.WriteLine("Usage: BLMSyncPlusPolicyCheck <RotationSolver.dll>");
	return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
	var dependency = Path.Combine(assemblyDirectory, $"{name.Name}.dll");
	return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
const string ns = "RotationSolver.RebornRotations.Magical.";
var scheduler = assembly.GetType($"{ns}BLMSyncPlusScheduler", true)!;
var policy = assembly.GetType($"{ns}BLMSyncPlusPolicy", true)!;
var snapshotType = assembly.GetType($"{ns}BLMSyncPlusSnapshot", true)!;
var decisionType = assembly.GetType($"{ns}BLMSyncPlusDecision", true)!;
var gateType = assembly.GetType($"{ns}BLMSyncPlusBurstGate", true)!;
var planType = assembly.GetType($"{ns}BLMSyncPlusResourcePlan", true)!;
var downtimeActionType = assembly.GetType($"{ns}BLMSyncPlusDowntimeAction", true)!;

var free = Enum.Parse(gateType, "Free");
var hold = Enum.Parse(gateType, "Hold");
var commit = Enum.Parse(gateType, "Commit");
var spend = Enum.Parse(planType, "Spend");
var bank = Enum.Parse(planType, "Bank");
var dumpAction = Enum.Parse(downtimeActionType, "Dump");
var noneAction = Enum.Parse(downtimeActionType, "None");

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
	float payloadWindow = 12f,
	float maximumHold = 30f,
	float overlapWait = 6f,
	float lookAhead = 8f)
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

CheckDecision("Live party burst commits Manafont",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: true), commit, spend, "raid buffs are up");

CheckDecision("Partial party burst waits for overlap",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: false, secondsInPartyBurst: 1f, overlapWait: 6f),
	hold, bank, "waiting for the rest of the raid buffs");

CheckDecision("Partial party burst commits once overlap wait expires",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: false, secondsInPartyBurst: 7f, overlapWait: 6f),
	commit, spend, "raid buffs are up");

CheckDecision("Imminent downtime dumps and withholds Manafont",
	Snapshot(timelineActive: true, downtimeIn: 5f, payloadWindow: 12f),
	hold, spend, "downtime too close for a full window; spending resources");

CheckDecision("Downtime hold ceiling commits when ceiling reached",
	Snapshot(timelineActive: true, downtimeIn: 5f, payloadWindow: 12f, burstHeldFor: 35f, maximumHold: 30f),
	commit, spend, "hold ceiling reached");

CheckDecision("Predicted party burst holds and banks resources",
	Snapshot(predictedPartyBurstIn: 5f, lookAhead: 8f),
	hold, bank, "raid buffs predicted in 5.0s");

// 2. Pure Policy Checks: Manafont
var shouldUseManafont = Method(policy, "ShouldUseManafont");
Check("Manafont allowed when committed, in Astral Fire, MP == 0, not reserved", true,
	shouldUseManafont.Invoke(null, [commit, true, 0u, false]));
Check("Manafont blocked when burst gate is Hold", false,
	shouldUseManafont.Invoke(null, [hold, true, 0u, false]));
Check("Manafont blocked when MP > 0", false,
	shouldUseManafont.Invoke(null, [commit, true, 2000u, false]));
Check("Manafont blocked outside Astral Fire", false,
	shouldUseManafont.Invoke(null, [commit, false, 0u, false]));
Check("Manafont blocked when reserved for next boss", false,
	shouldUseManafont.Invoke(null, [commit, true, 0u, true]));

// 3. Pure Policy Checks: Ley Lines
var shouldUseLeyLines = Method(policy, "ShouldUseLeyLines");
Check("Ley Lines allowed when committed, stationary, safe timeline", true,
	shouldUseLeyLines.Invoke(null, [commit, false, false, false, (ushort)1, false]));
Check("Ley Lines blocked while moving", false,
	shouldUseLeyLines.Invoke(null, [commit, true, false, false, (ushort)1, false]));
Check("Ley Lines blocked when downtime imminent within 15s", false,
	shouldUseLeyLines.Invoke(null, [commit, false, true, false, (ushort)1, false]));
Check("Ley Lines blocked when target dying", false,
	shouldUseLeyLines.Invoke(null, [commit, false, false, true, (ushort)1, false]));
Check("Ley Lines holds charge 1 during hold gate", false,
	shouldUseLeyLines.Invoke(null, [hold, false, false, false, (ushort)1, false]));
Check("Ley Lines spends charge 1 when at 2 charges to avoid overcap even during hold", true,
	shouldUseLeyLines.Invoke(null, [hold, false, false, false, (ushort)2, false]));

// 4. Pure Policy Checks: Polyglot
var shouldSpendPolyglot = Method(policy, "ShouldSpendPolyglotInBurst");
Check("Polyglot spend in burst when committed and stacks > 0", true,
	shouldSpendPolyglot.Invoke(null, [commit, (byte)2, true]));
Check("Polyglot spend blocked when burst is holding", false,
	shouldSpendPolyglot.Invoke(null, [hold, (byte)2, true]));
Check("Polyglot spend blocked when stacks == 0", false,
	shouldSpendPolyglot.Invoke(null, [commit, (byte)0, true]));
var idleDecision = evaluate.Invoke(null, [Snapshot(burstReady: false, burstReadySoon: false)])!;
Check("Generic no-reason-to-wait Commit does not drain Polyglot outside a spend window", false,
	shouldSpendPolyglot.Invoke(null, [gateProp.GetValue(idleDecision)!, (byte)1, false]));

var shouldBankPolyglot = Method(policy, "ShouldBankPolyglot");
Check("Polyglot banked when holding and not overcapping", true,
	shouldBankPolyglot.Invoke(null, [hold, bank, (byte)1, false, false]));
Check("Polyglot forced to spend when about to overcap", false,
	shouldBankPolyglot.Invoke(null, [hold, bank, (byte)3, true, true]));

var shouldDumpPolyglot = Method(policy, "ShouldDumpPolyglotBeforeDowntime");
Check("Polyglot dumped before downtime when downtime <= 3s", true,
	shouldDumpPolyglot.Invoke(null, [noneAction, 2.5f, 3.0f, (byte)1]));
Check("Polyglot dumped on dump action", true,
	shouldDumpPolyglot.Invoke(null, [dumpAction, 5.0f, 3.0f, (byte)1]));
Check("Polyglot not dumped when downtime is distant", false,
	shouldDumpPolyglot.Invoke(null, [noneAction, 10.0f, 3.0f, (byte)1]));
Check("Polyglot not dumped when stacks == 0", false,
	shouldDumpPolyglot.Invoke(null, [dumpAction, 2.0f, 3.0f, (byte)0]));

// 5. Pure Policy Checks: Downtime Cast Interruption Protection
var shouldSuppressHardCast = Method(policy, "ShouldSuppressHardCastForDowntime");
Check("Hard cast suppressed when downtime within 3.0s", true,
	shouldSuppressHardCast.Invoke(null, [2.5f, 3.0f, false]));
Check("Hard cast allowed when downtime is further than cast time", false,
	shouldSuppressHardCast.Invoke(null, [5.0f, 3.0f, false]));
Check("Instant GCD allowed even when downtime is imminent", false,
	shouldSuppressHardCast.Invoke(null, [1.5f, 3.0f, true]));

// 6. Encounter lifecycle and inherited action routing.
var shouldObserve = Method(policy, "ShouldObservePartyBurst");
Check("Learn first opener anchor", true, shouldObserve.Invoke(null, [-1f, 7f]));
Check("Do not reanchor an ongoing buff window", false, shouldObserve.Invoke(null, [7f, 7f]));
Check("Learn next burst independent of absolute 120s boundaries", true, shouldObserve.Invoke(null, [125f, 235f]));
Check("Learn new opener after reset", true, shouldObserve.Invoke(null, [-1f, 4f]));
var shouldReset = Method(policy, "ShouldResetEncounter");
Check("Reset on combat exit", true, shouldReset.Invoke(null, [245f, 0f, false]));
Check("Reset when encounter clock goes backwards", true, shouldReset.Invoke(null, [245f, 1f, true]));
Check("Do not reset normal combat", false, shouldReset.Invoke(null, [20f, 21f, true]));
Check("Do not repeatedly reset while idle", false, shouldReset.Invoke(null, [-1f, 0f, false]));

var rotationType = assembly.GetType($"{ns}BLM_SyncPlus", true)!;
var rotation = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(rotationType);
rotationType.GetProperty("LeylineMadness")!.SetValue(rotation, true);
var inheritedLeyLines = rotationType.GetProperty("AllowAutomaticLeyLines", BindingFlags.NonPublic | BindingFlags.Instance)!;
Check("Inherited Ley Lines path remains disabled even when legacy setting is enabled", false,
	inheritedLeyLines.GetValue(rotation));
var instanceFlags = BindingFlags.NonPublic | BindingFlags.Instance;
var observedBuffsField = rotationType.GetField("_observedPartyBuffs", instanceFlags)!;
observedBuffsField.SetValue(rotation, Activator.CreateInstance(observedBuffsField.FieldType));
var anchorField = rotationType.GetField("_lastObservedPartyBurstAt", instanceFlags)!;
var decisionStampField = rotationType.GetField("_decisionComputedAt", instanceFlags)!;
anchorField.SetValue(rotation, 245f);
decisionStampField.SetValue(rotation, 1f);
rotationType.GetMethod("ResetEncounterTracking", instanceFlags)!.Invoke(rotation, null);
Check("Actual reset clears prior-fight anchor", -1f, anchorField.GetValue(rotation));
Check("In-decision reset preserves the recursion guard stamp", 1f, decisionStampField.GetValue(rotation));
Check("Territory changes use the Sync reset override", rotationType,
	rotationType.GetMethod("OnTerritoryChanged")!.DeclaringType);

// Restore the existing buff-driven opener without coupling it to second-potion settings.
var openerMedicine = Method(policy, "ShouldUseOpenerMedicine");
Check("Opener potion under buffs", true, openerMedicine.Invoke(null, [7f, true, false]));
Check("No opener potion without buffs", false, openerMedicine.Invoke(null, [7f, false, false]));
Check("Opener potion respects burst reservation", false, openerMedicine.Invoke(null, [7f, true, true]));
Check("Opener branch does not become an uncontrolled later potion", false, openerMedicine.Invoke(null, [125f, true, false]));
var secondMedicine = Method(policy, "ShouldUseSecondMedicine");
Check("Second-potion disable does not alter opener eligibility", false,
	secondMedicine.Invoke(null, [false, 360f, 7f, commit]));

Console.WriteLine();
if (failures > 0)
{
	Console.Error.WriteLine($"{failures} BLM Sync check(s) FAILED.");
	return 1;
}

Console.WriteLine("All BLM Sync checks passed.");
return 0;
