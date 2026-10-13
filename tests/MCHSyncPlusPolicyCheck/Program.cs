using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

// Regression cover for MCH_SyncPlus. The scheduler is a pure function over a snapshot, so the
// whole decision table is testable without the game attached.

if (args.Length != 1)
{
	Console.Error.WriteLine("Usage: MCHSyncPlusPolicyCheck <RotationSolver.dll>");
	return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
	var dependency = Path.Combine(assemblyDirectory, $"{name.Name}.dll");
	// Game-data types (Lumina) only ship with Dalamud; needed to reflect over rotation properties.
	var dalamud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev", $"{name.Name}.dll");
	return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency)
		: File.Exists(dalamud) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dalamud) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
const string ns = "RotationSolver.RebornRotations.Ranged.";
var scheduler = assembly.GetType($"{ns}MCHSyncPlusScheduler", true)!;
var policy = assembly.GetType($"{ns}MCHSyncPlusPolicy", true)!;
var burstActionPolicy = assembly.GetType($"{ns}MCHBurstActionPolicy", true)!;
var snapshotType = assembly.GetType($"{ns}MCHSyncPlusSnapshot", true)!;
var decisionType = assembly.GetType($"{ns}MCHSyncPlusDecision", true)!;
var gateType = assembly.GetType($"{ns}MCHSyncPlusBurstGate", true)!;
var planType = assembly.GetType($"{ns}MCHSyncPlusResourcePlan", true)!;
var wildfireWeaveType = assembly.GetType($"{ns}MCHSyncPlusWildfireWeave", true)!;
var rotationType = assembly.GetType($"{ns}MCH_SyncPlus", true)!;
var planRotationType = assembly.GetType($"{ns}MCH_Plan", true)!;
var rotationUpdaterType = assembly.GetType("RotationSolver.Updaters.RotationUpdater", true)!;

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
var gateProp = decisionType.GetProperty("Wildfire")!;
var planProp = decisionType.GetProperty("Resources")!;
var reasonProp = decisionType.GetProperty("Reason")!;
var downtimeProp = decisionType.GetProperty("Downtime")!;

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

var resolveRetiredRotationChoice = Method(rotationUpdaterType, "ResolveRetiredRotationChoice");
const string retiredRotationName = "RotationSolver.RebornRotations.Ranged.MCH_Sync";
const string canonicalRotationName = "RotationSolver.RebornRotations.Ranged.MCH_SyncPlus";
Check("Classic rotation type is retired", null, assembly.GetType(retiredRotationName, false));
Check("saved Classic selection migrates to canonical MCH Sync", canonicalRotationName,
	resolveRetiredRotationChoice.Invoke(null, [retiredRotationName]));
Check("canonical selection remains stable", canonicalRotationName,
	resolveRetiredRotationChoice.Invoke(null, [canonicalRotationName]));
Check("unrelated rotation selection remains stable", "Example.OtherRotation",
	resolveRetiredRotationChoice.Invoke(null, ["Example.OtherRotation"]));

var seedCanonicalConfiguration = Method(rotationUpdaterType, "SeedCanonicalMchSyncConfiguration");
var retiredConfigurations = new ConcurrentDictionary<string, string>(new Dictionary<string, string>
{
	["BurstPlan"] = "FRU / trust party",
	["UseSecondBurstMedicine"] = "False",
	["SecondMedicineTime"] = "420",
	["OpenerBurstMeds"] = "False",
	["RetiredOnlyValue"] = "keep in rollback bucket",
});
var canonicalConfigurations = new ConcurrentDictionary<string, string>(new Dictionary<string, string>
{
	["SyncPlusBurstPlan"] = "TrustParty",
	["UseSecondBurstMedicine"] = "True",
});
Check("Classic configuration migration reports seeded values", true,
	seedCanonicalConfiguration.Invoke(null, [retiredConfigurations, canonicalConfigurations]));
Check("existing canonical burst strategy wins", "TrustParty", canonicalConfigurations["SyncPlusBurstPlan"]);
Check("existing canonical potion choice wins", "True", canonicalConfigurations["UseSecondBurstMedicine"]);
Check("missing shared potion time is preserved", "420", canonicalConfigurations["SecondMedicineTime"]);
Check("missing inherited MCH setting is preserved", "False", canonicalConfigurations["OpenerBurstMeds"]);
Check("retired-only values do not pollute the canonical bucket", false,
	canonicalConfigurations.ContainsKey("RetiredOnlyValue"));
Check("Classic rollback bucket remains untouched", "keep in rollback bucket",
	retiredConfigurations["RetiredOnlyValue"]);

foreach (var eagerDefault in new[] { "Automatic", "Automatic (timeline + dungeons)" })
{
	var source = new ConcurrentDictionary<string, string>();
	var destination = new ConcurrentDictionary<string, string>();
	source["BurstPlan"] = "FRU / trust party";
	destination["SyncPlusBurstPlan"] = eagerDefault;
	seedCanonicalConfiguration.Invoke(null, [source, destination]);
	Check($"eager canonical default '{eagerDefault}' does not defeat Classic migration",
		"TrustParty", destination["SyncPlusBurstPlan"]);
}

foreach (var (retiredValue, canonicalValue) in new[]
{
	("Automatic (FRU + dungeons)", "Automatic"),
	("FRU / trust party", "TrustParty"),
	// Normal collapsed into Automatic - the two only ever differed in dungeon
	// banking, which never applied outside a dungeon anyway.
	("Normal / safe sync", "Automatic"),
	("Solo / ignore party buffs", "Personal"),
})
{
	var source = new ConcurrentDictionary<string, string>();
	var destination = new ConcurrentDictionary<string, string>();
	source["BurstPlan"] = retiredValue;
	seedCanonicalConfiguration.Invoke(null, [source, destination]);
	Check($"Classic burst value '{retiredValue}' migrates", canonicalValue,
		destination["SyncPlusBurstPlan"]);
}

Check("Sync+ owns a unique burst-plan config key", true,
	rotationType.GetProperty("SyncPlusBurstPlan", BindingFlags.Instance | BindingFlags.NonPublic) is not null);
Check("Sync+ no longer collides with the classic BurstPlan key", null,
	rotationType.GetProperty("BurstPlan", BindingFlags.Instance | BindingFlags.NonPublic));
Check("raid-buff anchor is a constrained enum", true,
	rotationType.GetProperty("AnchorJob", BindingFlags.Instance | BindingFlags.NonPublic)?.PropertyType.IsEnum);

static CustomAttributeData ConfigMetadata(Type owner, string propertyName)
{
	var property = owner.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public
		| BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
		?? throw new InvalidOperationException($"Missing config property {owner.Name}.{propertyName}");
	return property.CustomAttributes.Single(attribute =>
		attribute.AttributeType.Name == "RotationConfigAttribute");
}

var baseRotationType = rotationType.BaseType
	?? throw new InvalidOperationException("MCH Sync has no MCH_Reborn base type");
var configMetadata = new[]
{
	ConfigMetadata(rotationType, "SyncPlusBurstPlan"),
	ConfigMetadata(rotationType, "UseEncounterTimeline"),
	ConfigMetadata(rotationType, "WildfireWeave"),
	ConfigMetadata(rotationType, "PadFatebreaker"),
	ConfigMetadata(rotationType, "UseSecondBurstMedicine"),
	ConfigMetadata(rotationType, "SecondMedicineTime"),
	ConfigMetadata(rotationType, "AdvancedTiming"),
	ConfigMetadata(rotationType, "AnchorJob"),
	ConfigMetadata(rotationType, "LogBurstReport"),
	ConfigMetadata(rotationType, "MaximumWildfireHoldSeconds"),
	ConfigMetadata(rotationType, "OverlapWaitSeconds"),
	ConfigMetadata(rotationType, "LookAheadSeconds"),
	ConfigMetadata(baseRotationType, "OpenerBurstMeds"),
	ConfigMetadata(baseRotationType, "ShowMchUtilitySettings"),
	ConfigMetadata(baseRotationType, "BioMove"),
	ConfigMetadata(baseRotationType, "WildfireBoss"),
	ConfigMetadata(baseRotationType, "MitOverlap"),
	ConfigMetadata(baseRotationType, "AirAnchorCountdown"),
	ConfigMetadata(baseRotationType, "MultiTact"),
	ConfigMetadata(baseRotationType, "BmrDumpBeforeDowntime"),
};

static string NamedString(CustomAttributeData attribute, string name)
	=> attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name)
		.TypedValue.Value as string ?? string.Empty;

static int NamedInt(CustomAttributeData attribute, string name)
	=> attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name)
		.TypedValue.Value is int value ? value : int.MaxValue;

Check("MCH Sync exposes the expected configuration surface", 20, configMetadata.Length);
Check("default configuration view is reduced to eight controls", 8,
	configMetadata.Count(attribute => string.IsNullOrEmpty(NamedString(attribute, "Parent"))));
Check("every MCH setting has contextual help", true,
	configMetadata.All(attribute => !string.IsNullOrWhiteSpace(NamedString(attribute, "Tooltip"))));
Check("MCH settings use four task-oriented sections", "Advanced,Burst,Potions,Utility and safety",
	string.Join(',', configMetadata.Select(attribute => NamedString(attribute, "Section"))
		.Distinct().OrderBy(section => section)));
Check("every MCH setting has a unique stable order", configMetadata.Length,
	configMetadata.Select(attribute => NamedInt(attribute, "Order")).Distinct().Count());

// Named defaults describe a healthy raid frame: burst on, Wildfire ready, timeline present with
// nothing imminent, no raid buffs up. Each case overrides only what it is testing.
object Snapshot(
	bool autoBurst = true,
	bool reserved = false,
	bool synchronizeParty = true,
	bool requireLiveParty = false,
	bool wildfireReady = true,
	bool wildfireReadySoon = true,
	float wildfireHeldFor = 0f,
	bool timelineActive = true,
	float downtimeIn = 120f,
	float vulnerableIn = float.MaxValue,
	float vulnerableEndIn = float.MaxValue,
	bool partyBurstActive = false,
	bool fullPartyBurstActive = false,
	float secondsInPartyBurst = 0f,
	float? predictedPartyBurstIn = null,
	float payloadWindow = 10f,
	float maximumHold = 30f,
	float overlapWait = 6f,
	float lookAhead = 8f,
	bool objectiveBanked = false)
	=> snapshotCtor.Invoke(
	[
		autoBurst, reserved, synchronizeParty, requireLiveParty, wildfireReady, wildfireReadySoon,
		wildfireHeldFor, timelineActive, downtimeIn, vulnerableIn, vulnerableEndIn,
		partyBurstActive, fullPartyBurstActive, secondsInPartyBurst, predictedPartyBurstIn,
		payloadWindow, maximumHold, overlapWait, lookAhead, objectiveBanked,
	]);

(object Gate, object Plan, string Reason) Decide(object snapshot)
{
	var decision = evaluate.Invoke(null, [snapshot])!;
	return (gateProp.GetValue(decision)!, planProp.GetValue(decision)!, (string)reasonProp.GetValue(decision)!);
}

void CheckDecision(
	string name, object snapshot, object expectedGate, object expectedPlan,
	string? expectedReason = null)
{
	var (gate, plan, reason) = Decide(snapshot);
	if (Equals(gate, expectedGate) && Equals(plan, expectedPlan)
		&& (expectedReason == null || reason == expectedReason))
	{
		Console.WriteLine($"PASS {name}: {gate}/{plan} - {reason}");
		return;
	}

	failures++;
	Console.Error.WriteLine($"FAIL {name}: expected {expectedGate}/{expectedPlan}"
		+ (expectedReason == null ? string.Empty : $" - {expectedReason}")
		+ $", got {gate}/{plan} - {reason}");
}

// The Downtime action is a separate channel from gate/plan: it is what ShouldSpendRemainingReassemble
// reads to force a held Reassemble charge out before a phase change. Gate and plan alone cannot
// distinguish Dump from an ordinary Hold/Spend, so assert it directly or the dump path is untested.
void CheckDowntimeAction(string name, object snapshot, string expected)
{
	var actual = downtimeProp.GetValue(evaluate.Invoke(null, [snapshot])!)!.ToString();
	if (actual == expected)
	{
		Console.WriteLine($"PASS {name}: {actual}");
		return;
	}

	failures++;
	Console.Error.WriteLine($"FAIL {name}: expected {expected}, got {actual}");
}

// --- Top-level gates ---------------------------------------------------------------------------

CheckDecision("burst off leaves the base rotation alone",
	Snapshot(autoBurst: false), free, spend);

CheckDecision("dungeon reservation banks everything",
	Snapshot(reserved: true), hold, bank);

// --- Downtime feasibility ----------------------------------------------------------------------
// The headline change: a Wildfire that cannot land its payload is a wasted use, and resources
// banked into a phase change are wasted twice. Hold the burst, spend the resources.

CheckDecision("imminent downtime holds Wildfire but dumps resources",
	Snapshot(downtimeIn: 6f), hold, spend);

CheckDecision("downtime just inside the payload window still dumps",
	Snapshot(downtimeIn: 9.9f), hold, spend);

CheckDecision("no buffs before downtime: Wildfire waits for the next burst",
	Snapshot(downtimeIn: 15f), hold, spend);

// Reassemble's forced dump. ShouldSpendRemainingReassemble fires on TargetDying OR this action
// being Dump, and TargetDying is live game state - so this is the only half of the forced dump a
// test can reach, and without it the whole path rests on an unasserted enum.
CheckDowntimeAction("downtime inside the payload window signals the Reassemble dump",
	Snapshot(downtimeIn: 6f), "Dump");
CheckDowntimeAction("a dump still signals once the hold ceiling is reached",
	Snapshot(downtimeIn: 6f, wildfireHeldFor: 30f), "Dump");
CheckDowntimeAction("distant downtime does not dump Reassemble",
	Snapshot(downtimeIn: 120f), "None");
// The timeline being off is the common live case - both Lindwurm and Vamp Fatale reported
// "downtime in: unknown" all fight. A blind timeline must not fabricate a dump signal.
CheckDowntimeAction("an inactive timeline never signals a dump",
	Snapshot(timelineActive: false, downtimeIn: 6f), "None");

CheckDecision("distant downtime does not force a commit",
	Snapshot(downtimeIn: 90f, predictedPartyBurstIn: 4f), hold, bank);

CheckDecision("known insufficient uptime survives the hold ceiling",
	Snapshot(downtimeIn: 6f, wildfireHeldFor: 40f), hold, spend);
CheckDecision("immediate downtime remains a hard guard",
	Snapshot(downtimeIn: 0f, wildfireHeldFor: 40f), hold, spend);

CheckDecision("downtime is ignored without a timeline (still no burst without buffs)",
	Snapshot(timelineActive: false, downtimeIn: 4f), hold, spend);

CheckDecision("unknown downtime is ignored (still no burst without buffs)",
	Snapshot(downtimeIn: float.MaxValue), hold, spend);

// --- Vulnerability windows ---------------------------------------------------------------------
// BossMod's NextVulnerableIn always searches for the next window's START: once truly inside one,
// it skips past to the NEXT occurrence (or MaxValue if there isn't one), never a small positive
// number. "Currently inside" is represented here the way the real IPC does: VulnerableEndIn known
// and small, VulnerableIn unknown/MaxValue or larger than VulnerableEndIn.

CheckDecision("open vulnerability window commits",
	Snapshot(vulnerableIn: float.MaxValue, vulnerableEndIn: 20f), commit, spend);

CheckDecision("inside the window even with a further future occurrence known",
	Snapshot(vulnerableIn: 125f, vulnerableEndIn: 20f), commit, spend);

CheckDecision("inside a window that is about to close does not force a commit into the tail",
	Snapshot(vulnerableIn: float.MaxValue, vulnerableEndIn: 0.5f, partyBurstActive: true,
		secondsInPartyBurst: 1f), hold, bank);

CheckDecision("approaching but not yet open does not commit, even a moment before it starts",
	Snapshot(vulnerableIn: 0.5f, vulnerableEndIn: 10.5f), hold, bank);

CheckDecision("approaching vulnerability window banks",
	Snapshot(vulnerableIn: 5f, vulnerableEndIn: 25f), hold, bank);

CheckDecision("distant vulnerability window is not a burst",
	Snapshot(vulnerableIn: 40f, vulnerableEndIn: 60f), hold, spend);

CheckDecision("vulnerability window too short to matter is not a burst",
	Snapshot(vulnerableIn: 5f, vulnerableEndIn: 8f), hold, spend);

CheckDecision("window length is the gap between the endpoints, not the end alone",
	Snapshot(vulnerableIn: 20f, vulnerableEndIn: 24f), hold, spend);

CheckDecision("unknown vulnerability end is assumed usable",
	Snapshot(vulnerableIn: 5f, vulnerableEndIn: float.MaxValue), hold, bank);

CheckDecision("live raid buffs outrank an approaching vulnerability window",
	Snapshot(vulnerableIn: 5f, vulnerableEndIn: 25f, partyBurstActive: true,
		fullPartyBurstActive: true, secondsInPartyBurst: 1f), commit, spend);

CheckDecision("an open vulnerability window still wins over live raid buffs",
	Snapshot(vulnerableIn: float.MaxValue, vulnerableEndIn: 20f, partyBurstActive: true,
		secondsInPartyBurst: 1f), commit, spend);

CheckDecision("partial buffs still wait out the overlap under an approaching window",
	Snapshot(vulnerableIn: 5f, vulnerableEndIn: 25f, partyBurstActive: true,
		secondsInPartyBurst: 1f), hold, bank);

CheckDecision("downtime outranks a vulnerability window",
	Snapshot(downtimeIn: 5f, vulnerableIn: float.MaxValue, vulnerableEndIn: 20f), hold, spend);

// --- Last full window must not race ahead of a better window that still fits ------------------
// downtime in 17s, vulnerability opening in 5s: 5 + payload(10) = 15 <= 17, so the vulnerability
// window still lands before downtime and is worth the wait, not an immediate personal commit.

CheckDecision("a fitting vulnerability window defers the downtime-forced commit",
	Snapshot(downtimeIn: 17f, vulnerableIn: 5f, vulnerableEndIn: 25f), hold, bank);

CheckDecision("live raid buffs also defer the downtime-forced commit",
	Snapshot(downtimeIn: 17f, partyBurstActive: true, secondsInPartyBurst: 1f), hold, bank);

CheckDecision("partial buffs cannot wait past the last payload-safe point",
	Snapshot(downtimeIn: 13f, partyBurstActive: true, secondsInPartyBurst: 1f), commit, spend);

CheckDecision("full live buffs still commit at the last payload-safe point",
	Snapshot(downtimeIn: 13f, partyBurstActive: true, fullPartyBurstActive: true,
		secondsInPartyBurst: 1f), commit, spend);

CheckDecision("no downtime-forced Wildfire without buffs",
	Snapshot(downtimeIn: 17f), hold, spend);

CheckDecision("a vulnerability window that would not fit before downtime is not a burst",
	Snapshot(downtimeIn: 17f, vulnerableIn: 12f, vulnerableEndIn: 30f), hold, spend);

// --- SynchronizeParty must be checked before live raid buffs, not after ------------------------
// Personal mode and an Automatic dungeon boss both set SynchronizeParty false; neither should wait
// on a party buff that happens to be live from an unrelated source.

CheckDecision("Personal mode ignores a live but unrelated party buff",
	Snapshot(synchronizeParty: false, partyBurstActive: true, secondsInPartyBurst: 1f), commit, spend);

CheckDecision("Personal mode still commits with no buffs at all",
	Snapshot(synchronizeParty: false), commit, spend);

// --- Raid buff overlap -------------------------------------------------------------------------

CheckDecision("partial overlap waits for the rest of the window",
	Snapshot(partyBurstActive: true, secondsInPartyBurst: 1f), hold, bank);

CheckDecision("full overlap commits immediately",
	Snapshot(partyBurstActive: true, fullPartyBurstActive: true, secondsInPartyBurst: 1f), commit, spend);

CheckDecision("partial overlap commits once the wait expires",
	Snapshot(partyBurstActive: true, secondsInPartyBurst: 6.5f), commit, spend);

CheckDecision("expiring buff cancels the overlap wait",
	Snapshot(partyBurstActive: true, secondsInPartyBurst: 1f, overlapWait: 0f), commit, spend);

CheckDecision("sync disabled ignores party buffs entirely",
	Snapshot(synchronizeParty: false), commit, spend);

// --- The hold ceiling applies to every hold path ------------------------------------------------

CheckDecision("no hold ceiling: partial buffs keep waiting for the overlap",
	Snapshot(partyBurstActive: true, secondsInPartyBurst: 1f, wildfireHeldFor: 30f), hold, bank);

CheckDecision("no hold ceiling: an approaching vulnerability window keeps the hold",
	Snapshot(vulnerableIn: 5f, vulnerableEndIn: 25f, wildfireHeldFor: 31f), hold, bank);

CheckDecision("no hold ceiling: trust party keeps waiting for a live buff",
	Snapshot(timelineActive: false, requireLiveParty: true, wildfireHeldFor: 30f), hold, bank);

CheckDecision("no hold ceiling: predicted buffs keep the hold",
	Snapshot(timelineActive: false, predictedPartyBurstIn: 3f, wildfireHeldFor: 45f), hold, bank);

// --- Fallback prediction, used only without a timeline -------------------------------------------

CheckDecision("trust party waits for a live raid buff",
	Snapshot(timelineActive: false, requireLiveParty: true), hold, bank);

CheckDecision("near predicted burst banks",
	Snapshot(timelineActive: false, predictedPartyBurstIn: 4f), hold, bank);

CheckDecision("distant predicted burst holds Wildfire, spends the gauge",
	Snapshot(timelineActive: false, predictedPartyBurstIn: 45f), hold, spend);

CheckDecision("no anchor learned yet: no Wildfire without buffs",
	Snapshot(timelineActive: false, predictedPartyBurstIn: null), hold, spend);

CheckDecision("Wildfire not coming soon: no burst without buffs",
	Snapshot(timelineActive: false, wildfireReadySoon: false, predictedPartyBurstIn: 3f), hold, spend);

// --- Supporting policies -------------------------------------------------------------------------

var shouldObservePartyBurst = Method(policy, "ShouldObservePartyBurst");
Check("party recurrence uses a fixed cadence tolerance", 30f,
	policy.GetField("PartyBurstRecurrenceTolerance", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
Check("first observation anchors", true, shouldObservePartyBurst.Invoke(null, [-1f, 12f, 120f, false])!);
Check("buff flicker does not re-anchor", false, shouldObservePartyBurst.Invoke(null, [120f, 150f, 120f, false])!);
Check("on-cycle burst re-anchors", true, shouldObservePartyBurst.Invoke(null, [120f, 240f, 120f, false])!);
Check("off-cycle one-minute burst cannot replace the anchor", false,
	shouldObservePartyBurst.Invoke(null, [10f, 70f, 120f, false])!);
Check("large drift cannot replace the anchor", false,
	shouldObservePartyBurst.Invoke(null, [120f, 305f, 120f, false])!);
Check("missed-cycle recurrence re-anchors", true,
	shouldObservePartyBurst.Invoke(null, [120f, 360f, 120f, false])!);

Check("full party recovery reanchors an off-cycle burst", true,
	shouldObservePartyBurst.Invoke(null, [120f, 305f, 120f, true])!);
Check("ordinary off-cycle burst cannot claim recovery", false,
	shouldObservePartyBurst.Invoke(null, [120f, 305f, 120f, false])!);
Check("recovery does not repeatedly anchor the same overlap", false,
	shouldObservePartyBurst.Invoke(null, [305f, 306f, 120f, true])!);

var shouldHardHoldQueen = Method(policy, "ShouldHardHoldQueen");
Check("timeline downtime hard-holds Queen independently of Battery", true,
	shouldHardHoldQueen.Invoke(null, [true, true])!);
Check("safe uptime releases the timeline Queen hard hold", false,
	shouldHardHoldQueen.Invoke(null, [true, false])!);
Check("unavailable timeline leaves native lifetime guards in control", false,
	shouldHardHoldQueen.Invoke(null, [false, true])!);

var batteryPolicy = assembly.GetType($"{ns}MCHBatteryPolicy", true)!;
var wouldOvercap = Method(batteryPolicy, "WouldOvercap");
Check("FRU 90 Battery plus Air Anchor needs the earlier Queen weave", true,
	wouldOvercap.Invoke(null, [90, 20])!);
Check("100 Battery with a generator needs the earlier Queen weave", true,
	wouldOvercap.Invoke(null, [100, 10])!);
Check("exactly filling Battery does not preempt optional Hypercharge", false,
	wouldOvercap.Invoke(null, [80, 20])!);
Check("full Battery without a generator keeps ordinary ability priority", false,
	wouldOvercap.Invoke(null, [100, 0])!);
Check("imminent downtime still holds an otherwise overflowing Queen", true,
	(bool)wouldOvercap.Invoke(null, [100, 20])! && (bool)shouldHardHoldQueen.Invoke(null, [true, true])!);

var queenBatteryTarget = Method(policy, "QueenBatteryTarget");
// Before the first significant party burst is observed the phase is null. A synced fight must
// still fall back to the cycle-1 target of 90, not to the personal 100 - holding for 100 there
// blocks the 1-minute Queen. Personal mode keeps 100: it has no window to spend a partial Queen for.
Check("pre-anchor synced fight falls back to the 90 Battery Queen", 90,
	queenBatteryTarget.Invoke(null, [true, false, false, null, 0])!);
Check("personal mode still banks to 100 with no anchor", 100,
	queenBatteryTarget.Invoke(null, [false, false, false, null, 0])!);
Check("opener preserves the 60 Battery Queen", 60,
	queenBatteryTarget.Invoke(null, [true, true, true, 0f, 1])!);
Check("first odd minute consolidates Battery at 90", 90,
	queenBatteryTarget.Invoke(null, [true, false, false, 43f, 1])!);
Check("first cycle reserves 100 after the odd-minute Queen", 100,
	queenBatteryTarget.Invoke(null, [true, false, false, 95f, 1])!);
Check("later cycle protects the just-opened two-minute window", 100,
	queenBatteryTarget.Invoke(null, [true, false, false, 20f, 2])!);
Check("later cycle releases the genuine 50 Battery surplus", 50,
	queenBatteryTarget.Invoke(null, [true, false, false, 38f, 2])!);
Check("second cycle follows with a 60 Battery odd-minute Queen", 60,
	queenBatteryTarget.Invoke(null, [true, false, false, 68f, 2])!);
Check("third cycle follows with a 70 Battery odd-minute Queen", 70,
	queenBatteryTarget.Invoke(null, [true, false, false, 68f, 3])!);
Check("fourth cycle follows with an 80 Battery odd-minute Queen", 80,
	queenBatteryTarget.Invoke(null, [true, false, false, 68f, 4])!);
Check("fifth cycle loops the odd-minute target back to 60", 60,
	queenBatteryTarget.Invoke(null, [true, false, false, 68f, 5])!);
Check("sixth cycle resumes the ramp at 70", 70,
	queenBatteryTarget.Invoke(null, [true, false, false, 68f, 6])!);
Check("long fights keep looping rather than saturating", 80,
	queenBatteryTarget.Invoke(null, [true, false, false, 68f, 10])!);
Check("live party buffs reserve a full 100 Battery Queen", 100,
	queenBatteryTarget.Invoke(null, [true, true, false, 68f, 2])!);
Check("personal mode banks a full 100 Battery Queen", 100,
	queenBatteryTarget.Invoke(null, [false, false, false, null, 0])!);

var shouldHoldQueenForBatteryTarget = Method(policy, "ShouldHoldQueenForBatteryTarget");
Check("guide target holds a Battery surplus that still fits under the cap", true,
	shouldHoldQueenForBatteryTarget.Invoke(null, [50, 90, 20])!);
Check("guide target releases when reached", false,
	shouldHoldQueenForBatteryTarget.Invoke(null, [90, 90, 0])!);
// Holding through an overcap reserves nothing - the surplus is lost either way, so a Queen
// deployed now at least spends it. Chain Saw plus Excavator projects 40 onto an 80 Battery gauge.
Check("a projected overcap stands the guide-target hold down", false,
	shouldHoldQueenForBatteryTarget.Invoke(null, [80, 100, 40])!);
Check("a projection landing exactly on the cap still holds", true,
	shouldHoldQueenForBatteryTarget.Invoke(null, [80, 100, 20])!);

var shouldReleaseQueenAtBatteryTarget = Method(policy, "ShouldReleaseQueenAtBatteryTarget");
Check("50 Battery surplus releases at its target", true,
	shouldReleaseQueenAtBatteryTarget.Invoke(null, [50, 50])!);
Check("60 Battery odd-minute Queen releases at its target", true,
	shouldReleaseQueenAtBatteryTarget.Invoke(null, [60, 60])!);
Check("100 Battery burst reserve is not a minimum-gauge release", false,
	shouldReleaseQueenAtBatteryTarget.Invoke(null, [80, 100])!);

var holdReassemble = Method(policy, "ShouldHoldReassembleForBuffs");
var spendReassembleInPartyBurst = Method(policy, "ShouldSpendReassembleInPartyBurst");
var spendReassembleOnDrill = Method(burstActionPolicy, "ShouldSpendReassembleOnDrill");
// Reassemble is aligned to buffs the same way Wildfire and Queen are, but only when the hold is
// free: never at max charges (that loses a charge outright), and never once buffs are already up.
Check("a near buff window holds a spare Reassemble charge", true,
	holdReassemble.Invoke(null, [true, false, false, 4f, 8f])!);
var heldForObservedSixMinuteWindow = (bool)holdReassemble.Invoke(
	null, [true, false, false, 10f, 30f])!;
var releasedIntoAcceptedPartyBurst = (bool)spendReassembleInPartyBurst.Invoke(
	null, [true, true])!;
Check("a spare charge survives a ten-second prediction and releases onto Drill in accepted buffs", true,
	heldForObservedSixMinuteWindow && releasedIntoAcceptedPartyBurst
		&& (bool)spendReassembleOnDrill.Invoke(null, [false, releasedIntoAcceptedPartyBurst, true])!);
Check("partial party buffs keep the saved Reassemble charge banked", false,
	spendReassembleInPartyBurst.Invoke(null, [true, false])!);
Check("max charges never hold - a capped Reassemble is lost, not delayed", false,
	holdReassemble.Invoke(null, [true, false, true, 4f, 8f])!);
Check("live raid buffs spend Reassemble instead of waiting", false,
	holdReassemble.Invoke(null, [true, true, false, 4f, 8f])!);
Check("a distant buff window does not hold Reassemble", false,
	holdReassemble.Invoke(null, [true, false, false, 40f, 30f])!);
Check("personal mode never holds Reassemble for buffs", false,
	holdReassemble.Invoke(null, [false, false, false, 4f, 8f])!);
Check("an unlearned party cadence does not hold Reassemble", false,
	holdReassemble.Invoke(null, [true, false, false, null, 8f])!);

// Reproduce the post-Excavator handoff: Drill is usable while the combo is entering preservation.
var selectReassembledTool = Method(burstActionPolicy, "SelectReassembledTool");
var actionIds = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("RotationSolver.Basic"))
	.GetType("RotationSolver.Basic.Data.ActionID", true)!;
uint ToolId(string name) => Convert.ToUInt32(Enum.Parse(actionIds, name));
var airAnchorId = ToolId("AirAnchorPvE");
var drillId = ToolId("DrillPvE");
var excavatorId = ToolId("ExcavatorPvE");
var chainSawId = ToolId("ChainSawPvE");
var probedTools = new List<uint>();
uint SelectTool(bool active, bool overheated, params uint[] usable)
{
	probedTools.Clear();
	Func<uint, bool> canUse = id => { probedTools.Add(id); return usable.Contains(id); };
	return (uint)selectReassembledTool.Invoke(null, [active, overheated, canUse])!;
}
Check("active Reassemble hands off to the usable Drill after Excavator", drillId,
	SelectTool(true, false, drillId));
Check("handoff stops native probes after the selected Drill", true,
	probedTools.SequenceEqual(new[] { airAnchorId, drillId }));
Check("lost predicted tool recovers onto another usable tool", excavatorId,
	SelectTool(true, false, excavatorId, chainSawId));
Check("no usable tool falls through without holding the GCD", 0u, SelectTool(true, false));
Check("inactive Reassemble leaves normal selection alone", 0u, SelectTool(false, false, drillId));
Check("inactive Reassemble does not probe native tools", 0, probedTools.Count);
Check("Overheat retains its existing priority", 0u, SelectTool(true, true, drillId));
Check("Overheat does not probe Reassemble tools", 0, probedTools.Count);

// Check the compiled caller too: moving the selector behind combo preservation recreates the bug.
var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
	.Where(field => field.FieldType == typeof(OpCode))
	.Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => opcode.Value);
IEnumerable<string> CalledMethods(MethodInfo method)
{
	var il = method.GetMethodBody()!.GetILAsByteArray()!;
	for (var offset = 0; offset < il.Length;)
	{
		short value = il[offset++];
		if (value == 0xfe) value = (short)(0xfe00 | il[offset++]);
		var opcode = opcodes[value];
		if (opcode.OperandType == OperandType.InlineMethod)
			yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, offset))!.Name;
		offset += opcode.OperandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
			_ => 4,
		};
	}
}
var baseMch = assembly.GetType($"{ns}MCH_Reborn", true)!;
var generalGcdCalls = CalledMethods(baseMch.GetMethod("GeneralGCD", BindingFlags.Instance | BindingFlags.NonPublic)!).ToList();
var handoffCall = generalGcdCalls.IndexOf("SelectReassembledTool");
var comboCall = generalGcdCalls.IndexOf("IsLastComboAction");
Check("compiled Reassemble handoff runs before expiring-combo preservation", true,
	handoffCall >= 0 && comboCall > handoffCall);

var advancePartyBurstCycle = Method(policy, "AdvancePartyBurstCycle");
Check("first party-burst anchor starts cycle one", 1,
	advancePartyBurstCycle.Invoke(null, [0, -1f, 8f, 120f])!);
Check("normal recurrence advances one cycle", 2,
	advancePartyBurstCycle.Invoke(null, [1, 8f, 128f, 120f])!);
Check("missed recurrence preserves the cycle count", 3,
	advancePartyBurstCycle.Invoke(null, [1, 8f, 248f, 120f])!);

// Excavator's fixed +20 against the scheduled Queen target. Args: partyBurstActive, battery, target.
var holdExcavator = Method(policy, "ShouldHoldExcavatorForBattery");
Check("50 Battery holds Excavator so a Clean Shot can land the 60 Queen", true,
	holdExcavator.Invoke(null, [false, false, 50, 60])!);
Check("Excavator that lands the target exactly is spent on sight", false,
	holdExcavator.Invoke(null, [false, false, 40, 60])!);
Check("Excavator is spent freely well under the target", false,
	holdExcavator.Invoke(null, [false, false, 20, 100])!);
Check("a gauge already at target never holds Excavator", false,
	holdExcavator.Invoke(null, [false, false, 60, 60])!);
// The even minutes want the 660 potency under buffs; gauge tidiness does not outrank that.
Check("a live party burst spends Excavator regardless of overshoot", false,
	holdExcavator.Invoke(null, [true, false, 50, 60])!);
// The base rotation gates Full Metal Field on !HasExcavatorReady, so a held Excavator blocks it.
// Gauge precision is worth ~10 Battery; Full Metal Field is 945 effective potency.
Check("a banked Full Metal Machinist always spends Excavator", false,
	holdExcavator.Invoke(null, [false, true, 50, 60])!);

// ShouldReserve conflates two states, and its consumers must not disagree about which is which.
// Args: autoBurst, bankDungeonBurst, protectNextBossBurst, bossEncounter.
var shouldReserve = Method(policy, "ShouldReserve");
Check("burst off reserves", true, shouldReserve.Invoke(null, [false, false, false, false])!);
Check("dungeon trash banks for the boss", true, shouldReserve.Invoke(null, [true, true, false, false])!);
Check("the boss itself never reserves", false, shouldReserve.Invoke(null, [true, true, true, true])!);
Check("a normal fight does not reserve", false, shouldReserve.Invoke(null, [true, false, false, false])!);
// The burst-off case is why ShouldHoldQueen must read Decision.Resources rather than this: the
// scheduler answers !autoBurst as Free/SPEND, so treating "reserved" as "bank" pinned the gauge.
Check("protecting the next boss reserves off-boss", true,
	shouldReserve.Invoke(null, [true, false, true, false])!);

// encounterTime <= 30f || observedThisFight. The grace window is what lets a fight expect buffs
// that have not been seen yet; after it, only jobs that actually fired stay expected.
var shouldExpectPartyBuff = Method(policy, "ShouldExpectPartyBuff");
Check("early in a fight every tracked buff is expected", true,
	shouldExpectPartyBuff.Invoke(null, [5f, false])!);
Check("after the grace window an unseen buff stops being expected", false,
	shouldExpectPartyBuff.Invoke(null, [60f, false])!);
Check("a buff seen this fight stays expected all fight", true,
	shouldExpectPartyBuff.Invoke(null, [400f, true])!);

// Args: enabled, inCombat, combatTime, wildfireReady, commitWildfire.
var openerMedicine = Method(policy, "ShouldUseOpenerMedicine");
Check("opener potion fires on the opening Wildfire", true,
	openerMedicine.Invoke(null, [true, true, 3f, true, true])!);
Check("opener potion does not fire mid-fight", false,
	openerMedicine.Invoke(null, [true, true, 90f, true, true])!);
Check("opener potion waits for a committed Wildfire", false,
	openerMedicine.Invoke(null, [true, true, 3f, true, false])!);
Check("opener potion respects its config toggle", false,
	openerMedicine.Invoke(null, [false, true, 3f, true, true])!);

var shouldOverdriveQueen = Method(policy, "ShouldOverdriveQueen");
Check("active Queen overdrives before the target ends", true,
	shouldOverdriveQueen.Invoke(null, [true, true, false])!);
Check("active Queen overdrives before predicted downtime", true,
	shouldOverdriveQueen.Invoke(null, [true, false, true])!);
Check("inactive Queen never overdrives", false,
	shouldOverdriveQueen.Invoke(null, [false, true, true])!);
Check("safe active Queen finishes naturally", false,
	shouldOverdriveQueen.Invoke(null, [true, false, false])!);

// Detonator is the Wildfire equivalent of Overdrive and carries the same rule from The Balance:
// only ever early-release when the payload would otherwise be lost. Wildfire pays 240 per
// weaponskill landed before it expires, so detonating early throws away every GCD still to come.
// Args: hasWildfire, detonatorReady, targetDying, downtimeSoon.
var shouldDetonateWildfire = Method(policy, "ShouldDetonateWildfire");
Check("a dying target detonates Wildfire rather than losing it", true,
	shouldDetonateWildfire.Invoke(null, [true, true, true, false])!);
Check("imminent downtime detonates Wildfire rather than losing it", true,
	shouldDetonateWildfire.Invoke(null, [true, true, false, true])!);
Check("a healthy target lets Wildfire run its full duration", false,
	shouldDetonateWildfire.Invoke(null, [true, true, false, false])!);
Check("no Wildfire on the target means nothing to detonate", false,
	shouldDetonateWildfire.Invoke(null, [false, true, true, true])!);
// Detonator only replaces Wildfire on the hotbar once the debuff is live; if the action is not
// ready the terminal condition must not report true, or the caller burns its last weave slot on
// an action that cannot execute - the slot Hypercharge was deliberately ordered behind.
Check("an unready Detonator never claims the weave slot", false,
	shouldDetonateWildfire.Invoke(null, [true, false, true, true])!);

// Both arguments are distinct-job counts, not status counts - the caller is responsible for
// collapsing multi-status jobs like BRD (Battle Voice + Radiant Finale) before calling this.
var significant = Method(policy, "IsSignificantPartyBurst");
Check("a full window moves the anchor", true, significant.Invoke(null, [4, 4])!);
Check("half the party's jobs move the anchor", true, significant.Invoke(null, [2, 4])!);
Check("one job out of four does not move the anchor", false, significant.Invoke(null, [1, 4])!);
Check("a lone buff source still anchors", true, significant.Invoke(null, [1, 1])!);
Check("no live buffs never anchors", false, significant.Invoke(null, [0, 4])!);

var shouldUseWildfire = Method(policy, "ShouldUseWildfire");
Check("terminal target blocks Wildfire even with a timeline", false,
	shouldUseWildfire.Invoke(null, [true, false, false, true, true])!);
// Boss-only Wildfire has to be visible here, not just at the CanUse sites: every Hypercharge
// path is held behind a pending Wildfire weave derived from this answer.
Check("boss-only Wildfire refuses a non-boss target", false,
	shouldUseWildfire.Invoke(null, [true, false, false, false, false])!);
Check("boss-only Wildfire accepts an allowed target", true,
	shouldUseWildfire.Invoke(null, [true, false, false, false, true])!);
Check("healthy target accepts a committed Wildfire", true,
	shouldUseWildfire.Invoke(null, [true, false, false, false, true])!);
Check("dungeon trash reserves Wildfire", false,
	shouldUseWildfire.Invoke(null, [true, true, false, false, true])!);
Check("dungeon boss permits Wildfire", true,
	shouldUseWildfire.Invoke(null, [true, true, true, false, true])!);

var shouldUseHypercharge = Method(policy, "ShouldUseHypercharge");
Check("Full Metal hold blocks Hypercharge below cap", false, shouldUseHypercharge.Invoke(null, [false, false, false, false, true, false, false, false])!);
Check("Heat 100 breaks the Full Metal hold", true, shouldUseHypercharge.Invoke(null, [false, false, false, false, true, true, false, false])!);
Check("downtime outranks the Heat cap", false, shouldUseHypercharge.Invoke(null, [false, false, true, false, true, true, false, false])!);
Check("hold-ceiling release permits the Wildfire payload", true,
	shouldUseHypercharge.Invoke(null, [false, true, true, true, false, false, false, false])!);
Check("dying target blocks fresh terminal Hypercharge", false,
	shouldUseHypercharge.Invoke(null, [true, false, false, false, false, true, false, false])!);
Check("dying target preserves an active Wildfire payload", true,
	shouldUseHypercharge.Invoke(null, [true, true, false, false, false, false, false, false])!);
Check("imminent downtime preserves an active Wildfire payload", true,
	shouldUseHypercharge.Invoke(null, [false, true, true, false, false, false, false, false])!);

// A logged opener: Full Metal Machinist held across the whole Wildfire while Full Metal
// Field never resolved, so Hypercharge stayed blocked and the window came out 4/6. The hold is
// worth one GCD, and these three pin where it ends.
Check("held Full Metal blocks the Wildfire payload for its first GCD", false,
	shouldUseHypercharge.Invoke(null, [false, true, false, false, true, false, false, false])!);
Check("closing Wildfire ends the Full Metal hold", true,
	shouldUseHypercharge.Invoke(null, [false, true, false, false, true, false, true, false])!);
Check("spent Full Metal releases the payload before the window closes", true,
	shouldUseHypercharge.Invoke(null, [false, true, false, false, false, false, false, false])!);
// The bound is scoped to a live Wildfire; with none up, Full Metal Machinist still gates a fresh
// window on the Heat cap exactly as before.
Check("closing window does not open a fresh Hypercharge under held Full Metal", false,
	shouldUseHypercharge.Invoke(null, [false, false, false, false, true, false, true, false])!);

// Logged: the capped-Heat force path fired Hypercharge with Chain Saw three seconds
// out and pushed it 5.02s late. ToolChargeSoon has always carried this test, but it guards one of
// six call sites; these pin it on the property all six nest inside.
Check("returning tool blocks a fresh Hypercharge", false,
	shouldUseHypercharge.Invoke(null, [false, false, false, false, false, false, false, true])!);
Check("capped Heat does not outrank a returning tool", false,
	shouldUseHypercharge.Invoke(null, [false, false, false, false, false, true, false, true])!);
Check("hold-ceiling release outranks a returning tool", true,
	shouldUseHypercharge.Invoke(null, [false, false, false, true, false, false, false, true])!);
Check("no returning tool leaves the fresh window open", true,
	shouldUseHypercharge.Invoke(null, [false, false, false, false, false, false, false, false])!);
// The Wildfire payload is the reason the window exists; a returning tool must never delay it,
// or the fix above this comment is undone one branch over.
Check("returning tool never delays an active Wildfire payload", true,
	shouldUseHypercharge.Invoke(null, [false, true, false, false, false, false, false, true])!);

var protectTool = Method(burstActionPolicy, "ShouldProtectTool");
Check("enabled learned returning tool is protected", true, protectTool.Invoke(null, [true, true, true])!);
Check("disabled tool cannot hold Hypercharge", false, protectTool.Invoke(null, [false, true, true])!);
Check("unlearned tool cannot hold Hypercharge", false, protectTool.Invoke(null, [true, false, true])!);
Check("distant tool cannot hold Hypercharge", false, protectTool.Invoke(null, [true, true, false])!);
var projectBattery = Method(batteryPolicy, "ProjectedBatteryGain");
var disabledGeneratorReady = (bool)protectTool.Invoke(null, [false, true, true])!;
var disabledGeneratorGain = (int)projectBattery.Invoke(null,
	[false, disabledGeneratorReady, disabledGeneratorReady ? 20 : 0, disabledGeneratorReady])!;
Check("disabled generators cannot trigger emergency Queen", false,
	wouldOvercap.Invoke(null, [90, disabledGeneratorGain])!);
var selectedGeneratorGain = (int)projectBattery.Invoke(null, [false, true, 0, false])!;
Check("actually selected Air Anchor still triggers overflow protection", true,
	wouldOvercap.Invoke(null, [90, selectedGeneratorGain])!);

var holdHyperchargeForWildfire = Method(policy, "ShouldHoldHyperchargeForFullMetalWildfire");
Check("unavailable payload never holds Hypercharge for Wildfire", false,
	holdHyperchargeForWildfire.Invoke(null, [true, true, true, false])!);
Check("legal Wildfire weave still holds Hypercharge until commit", true,
	holdHyperchargeForWildfire.Invoke(null, [true, true, true, true])!);
Check("committed Wildfire releases the Hypercharge hold", false,
	holdHyperchargeForWildfire.Invoke(null, [true, true, false, true])!);
Check("uncommitted Wildfire never blocks Hypercharge", false,
	holdHyperchargeForWildfire.Invoke(null, [true, false, true, true])!);

var cappedHyperchargeAllowed = (bool)shouldUseHypercharge.Invoke(null,
	[false, false, false, false, true, true, false, false])!;
var pendingWildfireBlocksHypercharge = (bool)holdHyperchargeForWildfire.Invoke(null,
	[true, true, true, true])!;
Check("Heat 100 cannot preempt pending Wildfire before Full Metal", false,
	cappedHyperchargeAllowed && !pendingWildfireBlocksHypercharge);
Check("Heat 100 can release when the pending payload is unavailable", true,
	cappedHyperchargeAllowed && !(bool)holdHyperchargeForWildfire.Invoke(null, [true, true, true, false])!);

var holdFullMetalForWildfire = Method(policy, "ShouldHoldFullMetalForWildfire");
Check("party hold preserves Full Metal", true,
	holdFullMetalForWildfire.Invoke(null, [true, true, true, false, false])!);
Check("near-ready Wildfire preserves Full Metal", true,
	holdFullMetalForWildfire.Invoke(null, [true, true, false, true, false])!);
Check("raw downtime block preserves Full Metal", true,
	holdFullMetalForWildfire.Invoke(null, [true, true, true, true, true])!);
Check("ready committed Wildfire releases Full Metal", false,
	holdFullMetalForWildfire.Invoke(null, [true, true, true, true, false])!);
Check("distant Wildfire does not hold Full Metal", false,
	holdFullMetalForWildfire.Invoke(null, [true, false, false, false, true])!);
Check("Auto Burst off does not hold Full Metal", false,
	holdFullMetalForWildfire.Invoke(null, [false, true, false, false, true])!);

var delayFullMetal = Method(policy, "ShouldDelayFullMetalForLeadingWildfire");
Check("usable leading Wildfire delays Full Metal until its weave", true,
	delayFullMetal.Invoke(null, [true, true, true, true, false])!);
Check("scheduler hold does not delay Full Metal for an unusable Wildfire", false,
	delayFullMetal.Invoke(null, [false, true, true, true, false])!);
Check("unavailable payload does not delay Full Metal", false,
	delayFullMetal.Invoke(null, [true, true, true, false, false])!);
Check("downtime prevents the leading-Wildfire delay", false,
	delayFullMetal.Invoke(null, [true, true, true, true, true])!);

var stableDecisionReason = Method(policy, "StableDecisionReason");
Check("prediction countdown has a stable log key", "raid buffs predicted",
	stableDecisionReason.Invoke(null, ["raid buffs predicted in -7.4s"])!);
Check("vulnerability countdown has a stable log key", "vulnerability window",
	stableDecisionReason.Invoke(null, ["vulnerability window in 4.2s"])!);
Check("fixed decision reason remains unchanged", "raid buffs are up",
	stableDecisionReason.Invoke(null, ["raid buffs are up"])!);

var describeWildfireCastState = Method(policy, "DescribeWildfireCastState");
Check("pre-buff Wildfire is labeled as cast state, not payload coverage", "before tracked buffs at cast",
	describeWildfireCastState.Invoke(null, [0, 4])!);
Check("partial cast overlap remains cast-scoped", "partial tracked overlap at cast",
	describeWildfireCastState.Invoke(null, [2, 4])!);
Check("full cast overlap remains cast-scoped", "full tracked overlap at cast",
	describeWildfireCastState.Invoke(null, [4, 4])!);

var weaveBeforeFullMetal = Method(policy, "ShouldWeaveWildfireBeforeFullMetal");
Check("removed opener-only config migrates to every-burst fallback", "EveryBurst", Enum.GetValues(wildfireWeaveType).GetValue(0)!.ToString());
Check("Wildfire weaves before a selected Full Metal Field", true, weaveBeforeFullMetal.Invoke(null, [true, true, true, true, false, true, true, true, false])!);
Check("live party burst releases Wildfire before Full Metal selection", true, weaveBeforeFullMetal.Invoke(null, [true, true, true, false, true, true, true, true, false])!);
Check("disabled weave mode never fires", false, weaveBeforeFullMetal.Invoke(null, [false, true, true, true, true, true, true, true, false])!);
Check("no early weave without selected Full Metal or live party burst", false, weaveBeforeFullMetal.Invoke(null, [true, true, true, false, false, true, true, true, false])!);
Check("no weave once Full Metal Machinist is spent", false, weaveBeforeFullMetal.Invoke(null, [true, true, true, true, true, false, true, true, false])!);
Check("no weave without an executable payload", false, weaveBeforeFullMetal.Invoke(null, [true, true, true, true, true, true, false, true, false])!);
Check("no weave outside the late weave window", false, weaveBeforeFullMetal.Invoke(null, [true, true, true, true, true, true, true, false, false])!);
Check("no weave into imminent downtime", false, weaveBeforeFullMetal.Invoke(null, [true, true, true, true, true, true, true, true, true])!);
Check("a burst hold still blocks the early weave", false, weaveBeforeFullMetal.Invoke(null, [true, false, true, true, true, true, true, true, false])!);

var prioritizeFullMetal = Method(policy, "ShouldPrioritizeFullMetalAfterWildfire");
Check("leading Wildfire immediately prioritizes Full Metal Field", true,
	prioritizeFullMetal.Invoke(null, [true, false, true])!);
Check("active Wildfire keeps Full Metal priority after another action", true,
	prioritizeFullMetal.Invoke(null, [true, true, false])!);
Check("recovery with a live Wildfire protects its Full Metal regardless of configured mode", true,
	prioritizeFullMetal.Invoke(null, [true, true, true])!);
Check("spent Full Metal buff cannot force the GCD", false,
	prioritizeFullMetal.Invoke(null, [false, true, true])!);
Check("Full Metal is not forced before Wildfire", false,
	prioritizeFullMetal.Invoke(null, [true, false, false])!);

var prioritizeAirAnchor = Method(policy, "ShouldPrioritizeAirAnchor");
Check("ready Air Anchor outranks combo continuation", true,
	prioritizeAirAnchor.Invoke(null, [false, false, false])!);
Check("Wildfire payload handoff still outranks Air Anchor", false,
	prioritizeAirAnchor.Invoke(null, [true, false, false])!);
Check("Air Anchor cannot interrupt Hypercharge", false,
	prioritizeAirAnchor.Invoke(null, [false, true, false])!);
Check("single-target Air Anchor does not override the AOE rotation", false,
	prioritizeAirAnchor.Invoke(null, [false, false, true])!);

var stall = Method(policy, "ShouldWaitForWildfireHyperchargeGcd");
var wildfirePayload = Method(burstActionPolicy, "ShouldStartWildfireHypercharge");
Check("active Wildfire after Full Metal requires Hypercharge", true, wildfirePayload.Invoke(null, [true, false])!);
Check("held Full Metal blocks early Hypercharge", false, wildfirePayload.Invoke(null, [true, true])!);
Check("Full Metal recast cannot age out the Hypercharge stall", true, stall.Invoke(null, [true, true, false, 10f, 1.5f])!);
Check("animation lock cannot release the queued GCD while the Hypercharge charge is ready", true, stall.Invoke(null, [true, true, true, 0.2f, 1.5f])!);
Check("stall refuses while Hypercharge is actually on cooldown", false, stall.Invoke(null, [true, false, true, 0.2f, 1.5f])!);
Check("stall gives up after the queue-window watchdog", false, stall.Invoke(null, [true, true, true, 1.6f, 1.5f])!);
Check("no stall outside the post-Full-Metal Wildfire handoff", false, stall.Invoke(null, [false, true, true, 0.2f, 1.5f])!);

var overlapWait = Method(policy, "EffectiveOverlapWait");
Check("expiring buff zeroes the overlap wait", 0f, overlapWait.Invoke(null, [10f, 10f, 6f])!);
Check("fresh buffs keep the full overlap wait", 6f, overlapWait.Invoke(null, [19f, 10f, 6f])!);

var secondMedicine = Method(policy, "ShouldUseSecondMedicine");
Check("second potion fires inside its window", true, secondMedicine.Invoke(null, [true, true, false, 369f, 360f, 120f, true, true])!);
Check("second potion waits for a committed burst", false, secondMedicine.Invoke(null, [true, true, false, 369f, 360f, 120f, true, false])!);
Check("second potion stays off before its window", false, secondMedicine.Invoke(null, [true, true, false, 200f, 360f, 120f, true, true])!);

var finalQueen = Method(policy, "ShouldReleaseFinalQueen");
var queenLifetime = (float)rotationType.GetField("QueenExecutionWindow", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
var holdForLifetime = Method(policy, "ShouldHoldQueenForLifetime");
Check("Queen window includes Collider damage delivery", 18f, queenLifetime);
Check("final summon cannot start with only 16.28 seconds", true, holdForLifetime.Invoke(null,[16.28f,queenLifetime]));
Check("unknown TTK leaves existing availability checks in control", false, holdForLifetime.Invoke(null,[float.NaN,queenLifetime]));
Check("full damage lifetime permits summon", false, holdForLifetime.Invoke(null,[18f,queenLifetime]));
Check("final Queen releases earlier with damage delivery budget", true, finalQueen.Invoke(null,[90,false,22f,queenLifetime,5f,float.MaxValue]));
Check("final-window exception cannot defeat damage lifetime", false, finalQueen.Invoke(null,[90,false,16.28f,queenLifetime,5f,float.MaxValue]));
Check("90 Battery releases before final summon window closes", true, finalQueen.Invoke(null,[90,false,18f,16f,5f,float.MaxValue]));
Check("50 Battery can still fund a final full-duration Queen", true, finalQueen.Invoke(null,[50,false,18f,16f,5f,float.MaxValue]));
Check("do not summon below the required lifetime", false, finalQueen.Invoke(null,[100,false,14f,16f,5f,float.MaxValue]));
Check("normal long fight keeps the Battery schedule", false, finalQueen.Invoke(null,[90,false,80f,16f,5f,float.MaxValue]));
Check("unknown TTK does not force a Queen", false, finalQueen.Invoke(null,[90,false,float.NaN,16f,5f,float.MaxValue]));
Check("imminent jump still blocks final Queen", false, finalQueen.Invoke(null,[90,false,18f,16f,5f,10f]));
Check("active Queen cannot be replaced by terminal policy", false, finalQueen.Invoke(null,[90,true,18f,16f,5f,float.MaxValue]));
Check("user's longer summon minimum is retained", false, finalQueen.Invoke(null,[90,false,18f,25f,5f,float.MaxValue]));
// RSR finds settings by reflecting the selected rotation's type; private ones on MCH Sync would vanish from MCH Plan.
Check("MCH Plan inherits every MCH Sync setting", 0,
	rotationType.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
		.Count(p => p.GetCustomAttributesData().Any(a => a.AttributeType.Name == "RotationConfigAttribute") && p.GetMethod!.IsPrivate));
// Phase objectives (FRU): bank instead of dumping once the push is secured.
CheckDecision("secured phase banks even with raid buffs up", Snapshot(objectiveBanked: true, partyBurstActive: true, fullPartyBurstActive: true), hold, bank);
CheckDecision("secured phase outranks the downtime dump", Snapshot(objectiveBanked: true, downtimeIn: 3f), hold, bank);
CheckDecision("secured phase ignores the hold ceiling", Snapshot(objectiveBanked: true, wildfireHeldFor: 90f), hold, bank);
var phaseObjective = Method(policy, "PhaseObjective");
var objectiveIn = Method(policy, "ObjectiveIn");
var refill = Method(policy, "ToolRefillAfterUse");
Check("P2 Usurper is a timed 20% check (with margin)", (float?)0.18f, phaseObjective.Invoke(null, [0x459Fu]));
Check("Ice Veil has no objective", null, phaseObjective.Invoke(null, [0x45A5u]));
Check("Pandora is the last phase", null, phaseObjective.Invoke(null, [0x45AFu]));
Check("P1 must die", (float?)0f, phaseObjective.Invoke(null, [0x459Bu]));
Check("Crystals stay a normal burst target", null, phaseObjective.Invoke(null, [0x45A3u]));
Check("unknown target keeps plain TTK", 42f, objectiveIn.Invoke(null, [0.5f, 42f, null]));
Check("under the check is secured now", 0f, objectiveIn.Invoke(null, [0.15f, 30f, (float?)0.18f]));
Check("time to the check cuts the TTK line", 10f, objectiveIn.Invoke(null, [0.30f, 25f, (float?)0.18f]));
Check("unknown TTK stays unknown above the check", float.NaN, objectiveIn.Invoke(null, [0.30f, float.NaN, (float?)0.18f]));
var recentTtk = Method(policy, "RecentTTK");
Check("crystal: 64% -> 41% in 3.8s dies in ~6.8s", true, Math.Abs((float)recentTtk.Invoke(null, [0.64f, 0.41f, 3.8f])! - 6.77f) < 0.05f);
Check("too short a span gives no estimate", float.NaN, recentTtk.Invoke(null, [0.64f, 0.60f, 1f]));
Check("healing or idle target gives no estimate", float.NaN, recentTtk.Invoke(null, [0.40f, 0.41f, 3f]));
var recentHoldBelow = (float)policy.GetField("RecentTTKHoldBelow", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check("crystal that died in 6.9s is still held", true, (float)recentTtk.Invoke(null, [0.64f, 0.41f, 3.8f])! < recentHoldBelow);
Check("crystal burst reading (8.8s, 16s real) is not held", false, (float)recentTtk.Invoke(null, [0.997f, 0.714f, 3.5f])! < recentHoldBelow);
Check("capped Drill is back one recast later", 20f, refill.Invoke(null, [0f, 20f]));
Check("half-charged Drill needs its remaining recast too", 32f, refill.Invoke(null, [12f, 20f]));
// ---- MCH Plan: burst windows from the BossMod plan ----
var planScheduler = assembly.GetType($"{ns}MCHPlanScheduler", true)!;
var planSnapshotCtor = assembly.GetType($"{ns}MCHPlanSnapshot", true)!.GetConstructors().Single();
var planEvaluate = Method(planScheduler, "Evaluate");
(object Gate, object Plan, string Reason) PlanDecide(float startIn, bool buffs = false,
	bool banked = false, float downtimeIn = float.MaxValue, bool autoBurst = true,
	bool allBuffs = true, float secondsIn = 0f, float overlapWait = 6f, bool justAfter = false)
{
	var d = planEvaluate.Invoke(null, [planSnapshotCtor.Invoke([autoBurst, false, banked, downtimeIn, 10f, startIn, buffs, allBuffs, secondsIn, overlapWait, justAfter])])!;
	return (gateProp.GetValue(d)!, planProp.GetValue(d)!, (string)reasonProp.GetValue(d)!);
}
Check("plan: open window without raid buffs holds Wildfire", hold, PlanDecide(-1f).Gate);
Check("plan: open window without raid buffs banks the gauge", bank, PlanDecide(-1f).Plan);
Check("plan: open window with raid buffs commits", commit, PlanDecide(-1f, buffs: true).Gate);
Check("plan: live buffs 5s early commit", commit, PlanDecide(5f, buffs: true).Gate);
Check("plan: late buffs after the window still commit", commit, PlanDecide(100f, buffs: true).Gate);
Check("plan: last seconds before a window bank the gauge", bank, PlanDecide(6f).Plan);
Check("plan: no Wildfire between windows without buffs", hold, PlanDecide(110f).Gate);
Check("plan: gauge still spends between windows", spend, PlanDecide(110f).Plan);
Check("plan: secured phase objective banks inside a window", bank, PlanDecide(-1f, buffs: true, banked: true).Plan);
Check("plan: downtime too close holds Wildfire", hold, PlanDecide(-1f, buffs: true, downtimeIn: 4f).Gate);
Check("plan: burst off is free", free, PlanDecide(-1f, autoBurst: false).Gate);
Check("plan: partial buffs wait for the rest", hold, PlanDecide(-1f, buffs: true, allBuffs: false, secondsIn: 1f).Gate);
Check("plan: partial buffs bank while waiting", bank, PlanDecide(-1f, buffs: true, allBuffs: false, secondsIn: 1f).Plan);
Check("plan: partial buffs commit once the overlap wait runs out", commit, PlanDecide(-1f, buffs: true, allBuffs: false, secondsIn: 6f).Gate);
Check("plan: partial buffs commit when downtime is too close to wait", commit,
	PlanDecide(-1f, buffs: true, allBuffs: false, secondsIn: 1f, downtimeIn: 12f).Gate);
Check("plan: early partial buffs bank until the window (P4, 2/4 early buffs)", hold,
	PlanDecide(10.3f, buffs: true, allBuffs: false, secondsIn: 4f).Gate);
Check("plan: early full buffs still commit", commit, PlanDecide(10.3f, buffs: true).Gate);
Check("plan: overlap wait counts from the window opening, not the first buff", hold,
	PlanDecide(6f, buffs: true, allBuffs: false, secondsIn: 12f).Gate);
Check("plan: ... and commits once the wait from the opening has run", commit,
	PlanDecide(1f, buffs: true, allBuffs: false, secondsIn: 17f).Gate);
Check("plan: late partial buffs just after a window commit after the wait", commit,
	PlanDecide(100f, buffs: true, allBuffs: false, secondsIn: 6f, justAfter: true).Gate);
Check("plan: no overlap wait on adds commits partial buffs", commit, PlanDecide(-1f, buffs: true, allBuffs: false, overlapWait: 0f).Gate);

// Offline walk over the FRU plan as deployed (P5 pull clock), party buffs 1s into each window:
// Wildfire in every window, potions in every potion window, Wildfire saved across the P4 -> P5 transition.
{
	(float Start, bool Pot)[] windows = [(126, false), (248, false), (372, true), (508, false), (646, true), (862, false), (982.5f, true), (1103, false)];
	float wfReadyAt = 0, potReadyAt = 0; var wfUses = new List<float>(); var pots = new List<float>();
	for (var t = 10f; t < 1140f; t += 0.5f)
	{
		var w = windows.Where(x => x.Start + 20 > t).OrderBy(x => x.Start).FirstOrDefault();
		if (w.Start == 0) break;
		var open = t >= w.Start + 1;
		var (gate, _, _) = PlanDecide(w.Start - t, buffs: open);
		if (w.Pot && open && t >= wfReadyAt && t >= potReadyAt) { pots.Add(t); potReadyAt = t + 270; }
		if (Equals(gate, commit) && t >= wfReadyAt) { wfUses.Add(t); wfReadyAt = t + 120; }
	}
	Check("walk: Wildfire lands in every planned window", windows.Length,
		windows.Count(w => wfUses.Any(u => u >= w.Start && u < w.Start + 20)));
	Check("walk: no Wildfire between P4 and P5 (saved for P5)", 0, wfUses.Count(u => u > 666 && u < 862));
	Check("walk: a potion in all three potion windows", 3, pots.Count);
}

var detonateEarly = Method(policy, "ShouldDetonateEarly");
Check("crystal dying in 0.8s with 1.2s of Wildfire left detonates", true, detonateEarly.Invoke(null, [0.8f, 1.2f]));
Check("crystal dying in 1.5s, Wildfire ends in 0.6s: let it detonate itself", false, detonateEarly.Invoke(null, [1.5f, 0.6f]));
Check("target with 6s to live keeps Wildfire running", false, detonateEarly.Invoke(null, [6f, 4f]));
Check("no recent estimate never detonates", false, detonateEarly.Invoke(null, [float.NaN, 1f]));
var queenEnds = Method(policy, "QueenTargetEndsSoon");
Check("crystal dying in 2.5s under a 9s Queen overdrives", true, queenEnds.Invoke(null, [2.5f, 9f]));
Check("target living 6s more does not overdrive", false, queenEnds.Invoke(null, [6f, 9f]));
Check("Queen ending before the target needs no overdrive", false, queenEnds.Invoke(null, [3f, 2f]));
var crossbow = Method(burstActionPolicy,"AutoCrossbowTargetCount");
Check("level synced Crossbow threshold", 3, crossbow.Invoke(null,[false]));
Check("both upgraded oGCDs retain six-target threshold", 6, crossbow.Invoke(null,[true]));

// Audit regressions.
var syncGcdCalls = CalledMethods(rotationType.GetMethod("GeneralGCD", BindingFlags.Instance | BindingFlags.NonPublic)!).ToList();
var airAnchorShortcut = syncGcdCalls.IndexOf("ShouldPrioritizeAirAnchor");
Check("Air Anchor shortcut respects phase banking", true,
	airAnchorShortcut >= 0 && syncGcdCalls.IndexOf("ShouldHoldTool", airAnchorShortcut) > airAnchorShortcut);

var potionDue = Method(assembly.GetType($"{ns}MCHPlanScheduler", true)!, "PotionDue");
Check("late potion grace survives the final window disappearing", true, potionDue.Invoke(null, [null, true, true]));
Check("no window and no grace falls back to timed potions", null, potionDue.Invoke(null, [null, true, false]));
Check("grace without live raid buffs falls back to timed potions", null, potionDue.Invoke(null, [null, false, true]));

var downtimeBarrel = Method(policy, "ShouldUseDowntimeBarrel");
Check("FRU P4 downtime Barrel: 15s to uptime, P5 burst 112s away", true, downtimeBarrel.Invoke(null, [15f, 112f]));
Check("downtime Barrel waits until close to uptime", false, downtimeBarrel.Invoke(null, [30f, 112f]));
Check("downtime Barrel never steals a burst due soon after uptime", false, downtimeBarrel.Invoke(null, [15f, 30f]));
Check("downtime Barrel needs a known next burst", false, downtimeBarrel.Invoke(null, [15f, null]));
Check("downtime Barrel needs a BossMod uptime", false, downtimeBarrel.Invoke(null, [float.MaxValue, 112f]));
var preQueen = Method(policy, "ShouldPreSummonQueen");
Check("Queen pre-summons 4s before uptime", true, preQueen.Invoke(null, [4f, 60, false]));
Check("Queen not pre-summoned 10s out", false, preQueen.Invoke(null, [10f, 60, false]));
Check("Queen pre-summon needs 50 Battery", false, preQueen.Invoke(null, [4f, 40, false]));
var unclaimed = Method(policy, "BarrelBuffsUnclaimed");
Check("Barrel buffs with the next burst 100s away are spent", true, unclaimed.Invoke(null, [20f, 100f]));
Check("Barrel buffs a burst will claim are kept", false, unclaimed.Invoke(null, [20f, 5f]));
var syncGeneralAbility = rotationType.GetMethod("GeneralAbility", BindingFlags.Instance | BindingFlags.NonPublic)!;
Check("Sync owns a GeneralAbility for boss-gone actions", rotationType, syncGeneralAbility.DeclaringType);

var brd = assembly.GetType($"{ns}BRD_Reborn", true)!;
var brdGcdCalls = CalledMethods(brd.GetMethod("GeneralGCD", BindingFlags.Instance | BindingFlags.NonPublic)!).ToList();
Check("Barrage's Refulgent is checked before Resonant Arrow", true,
	brdGcdCalls.IndexOf("get_HasBarrage") >= 0 && brdGcdCalls.IndexOf("get_HasBarrage") < brdGcdCalls.IndexOf("get_ResonantArrowPvE"));
var grab = brd.GetMethod("ShouldGrabCodas", BindingFlags.Static | BindingFlags.NonPublic)!;
Check("P4->P5 downtime grabs Codas (Minuet back by uptime)", true, grab.Invoke(null, [76f, 76f]));
Check("P1->P2 downtime keeps the song cycle (Minuet 86s away, 46s downtime)", false, grab.Invoke(null, [46f, 86f]));
Check("no BMR downtime end never grabs", false, grab.Invoke(null, [float.MaxValue, 0f]));
var successor = brd.GetMethod("SuccessorBackInTime", BindingFlags.Static | BindingFlags.NonPublic)!;
Check("FRU P4->P5: Ballad back when Minuet ends", true, successor.Invoke(null, [76f, 1]));
Check("Ballad still cooling when Minuet expires at 119s is not safe to grab", false, successor.Invoke(null, [74f, 1]));
Check("Ballad ready exactly when Minuet expires at 120s is safe to grab", true, successor.Invoke(null, [75f, 1]));
Check("Paeon still cooling when preceding songs expire at 119s is not safe to grab", false, successor.Invoke(null, [29f, 2]));
Check("Paeon ready exactly when preceding songs expire at 120s is safe to grab", true, successor.Invoke(null, [30f, 2]));
Check("60s downtime, Minuet in 50s: grabbing Ballad leaves an uptime song gap", false, successor.Invoke(null, [50f, 1]));
Check("60s downtime, Minuet in 50s: Paeon is back by its turn", true, successor.Invoke(null, [50f, 2]));
var songCalls = CalledMethods(brd.GetMethod("SongAbility", BindingFlags.Instance | BindingFlags.NonPublic)!).ToList();
Check("Pitch Perfect drains before any song switch", true,
	songCalls.IndexOf("get_PitchPerfectPvE") >= 0 && songCalls.IndexOf("get_PitchPerfectPvE") < songCalls.IndexOf("TrySongTrackAction"));
var twoStack = brd.GetMethod("TwoStackPPBeforeEmpyreal", BindingFlags.Static | BindingFlags.NonPublic)!;
Check("Auto Burst off: two-stack PP before Empyreal with Finale ready", true, twoStack.Invoke(null, [2, true, false, false]));
Check("Auto Burst on: two-stack PP waits for a ready Finale", false, twoStack.Invoke(null, [2, true, true, false]));
Check("Auto Burst on: two-stack PP before Empyreal once Finale is cooling", true, twoStack.Invoke(null, [2, true, true, true]));
var potionTimes = brd.GetMethod("PotionDue", BindingFlags.Static | BindingFlags.NonPublic)!;
Check("potion burst near 240s", true, potionTimes.Invoke(null, [244f, "240,630,970"]));
Check("no potion burst at 120s", false, potionTimes.Invoke(null, [124f, "240,630,970"]));
Check("blank potion list never pots", false, potionTimes.Invoke(null, [240f, ""]));
var brdAttackCalls = CalledMethods(brd.GetMethod("AttackAbility", BindingFlags.Instance | BindingFlags.NonPublic)!).ToList();
var buffWait = brdAttackCalls.IndexOf("AttackAbility");
var empyreal = brdAttackCalls.LastIndexOf("get_EmpyrealArrowPvE");
Check("capped Pitch Perfect runs before the buff-alignment wait", true,
	buffWait > brdAttackCalls.IndexOf("get_PitchPerfectPvE"));
Check("every Pitch Perfect check runs before Empyreal Arrow", true,
	brdAttackCalls.LastIndexOf("get_PitchPerfectPvE") < empyreal);
var apexCalls = CalledMethods(brd.GetMethod("CanUseApexArrow", BindingFlags.Instance | BindingFlags.NonPublic)!).ToList();
Check("Auto Burst off releases Apex before the Raging Strikes / Battle Voice waits", true,
	apexCalls.IndexOf("get_IsBurst") >= 0 && apexCalls.IndexOf("get_IsBurst") < apexCalls.IndexOf("get_RagingStrikesPvE")
	&& apexCalls.IndexOf("get_IsBurst") < apexCalls.IndexOf("get_BattleVoicePvE"));
Check("AoE Apex release comes after the Battle Voice hold", true,
	apexCalls.IndexOf("get_LadonsbitePvE") > apexCalls.IndexOf("get_BattleVoicePvE"));
Check("Blast Arrow is checked before Resonant Arrow", true,
	brdGcdCalls.IndexOf("get_BlastArrowPvE") >= 0 && brdGcdCalls.IndexOf("get_BlastArrowPvE") < brdGcdCalls.IndexOf("get_ResonantArrowPvE"));
Check("padding stops every GCD", 0, brdGcdCalls.IndexOf("get_PaddingFatebreaker"));
var hyperchargeCalls = CalledMethods(rotationType.GetProperty("ShouldUseHypercharge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!).ToList();
Check("Hypercharge banks only once the HP check is met, not when it is predicted", true,
	hyperchargeCalls.Contains("get_BankingTools") && !hyperchargeCalls.Contains("get_ObjectiveBanked"));
foreach (var name in new[] { "GeneralGCD", "EmergencyAbility", "AttackAbility" })
	Check($"MCH padding gates {name} first", 0,
		CalledMethods(rotationType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!).ToList().IndexOf("get_PaddingFatebreaker"));
var pad = Method(assembly.GetType("RotationSolver.RebornRotations.FRUPad", true)!, "ShouldPad");
Check("pad: party kills Fatebreaker 6s before the cast ends", true, pad.Invoke(null, [12f, 6f]));
Check("pad: party kills it 3s early, too close to hold", false, pad.Invoke(null, [9f, 6f]));
Check("pad: party too slow, keep hitting", false, pad.Invoke(null, [9f, 12f]));
Check("pad: last 5s of the cast always goes", false, pad.Invoke(null, [4.5f, 0.5f]));
Check("pad: HP not dropping, hit", false, pad.Invoke(null, [12f, float.NaN]));
var brdPlanOpen = Method(assembly.GetType($"{ns}BRD_Plan", true)!, "IsOpen");
Check("BRD Plan: window reached opens the burst", true, brdPlanOpen.Invoke(null, [0f, false]));
Check("BRD Plan: 5s early without party buffs holds", false, brdPlanOpen.Invoke(null, [5f, false]));
Check("BRD Plan: party buffs 5s early pull the window in", true, brdPlanOpen.Invoke(null, [5f, true]));
Check("BRD Plan: party buffs 20s early do not", false, brdPlanOpen.Invoke(null, [20f, true]));
var brdPlanHolds = Method(assembly.GetType($"{ns}BRD_Plan", true)!, "HoldsBurst");
Check("BRD Plan: next window 115s out holds", true, brdPlanHolds.Invoke(null, [115f, false, true, false]));
Check("BRD Plan: missed window bursts in its late grace", false, brdPlanHolds.Invoke(null, [115f, false, true, true]));
Check("BRD Plan: no visible window with a plan holds", true, brdPlanHolds.Invoke(null, [null, false, true, false]));
Check("BRD Plan: late grace outranks the kill-gated hold", false, brdPlanHolds.Invoke(null, [null, false, true, true]));
Check("BRD Plan: no plan this pull never holds", false, brdPlanHolds.Invoke(null, [null, false, false, false]));
var barrelPhase = Method(policy, "BarrelOnCooldownPhase");
Check("Barrel on cooldown: P3 Oracle", true, barrelPhase.Invoke(null, [0x45A7u]));
Check("Barrel on cooldown: P4 Usurper", true, barrelPhase.Invoke(null, [0x45A9u]));
Check("Barrel on cooldown: P4 Oracle", true, barrelPhase.Invoke(null, [0x45ABu]));
Check("Barrel on cooldown: not P2 Usurper", false, barrelPhase.Invoke(null, [0x459Fu]));
Check("Barrel on cooldown: not P1 Fatebreaker", false, barrelPhase.Invoke(null, [0x459Bu]));
var returnGain = Method(policy, "ReturnBatteryGain");
Check("CT return: Air Anchor + Chain Saw (+Excavator) bring 60", 60, returnGain.Invoke(null, [true, true, false]));
Check("CT return: nothing ready brings 0", 0, returnGain.Invoke(null, [false, false, false]));
var batteryHold = Method(policy, "ShouldHoldQueenForBatteryTarget");
Check("CT return: 50 Battery + 60 from tools releases the target-100 hold", false, batteryHold.Invoke(null, [50, 100, 60]));
Check("CT return: 50 Battery + 20 still holds for 100", true, batteryHold.Invoke(null, [50, 100, 20]));
var soloFinale = Method(assembly.GetType($"{ns}BRD_Reborn", true)!, "ShouldUseStandaloneFinale");
Check("BRD CT Finale: 9s of CT left, 2 Codas, BV cooling", true, soloFinale.Invoke(null, [true, 9f, 2, true]));
Check("BRD CT Finale: 25s left would expire the buff in downtime", false, soloFinale.Invoke(null, [true, 25f, 3, true]));
Check("BRD CT Finale: 5s left is too late for P5", false, soloFinale.Invoke(null, [true, 5f, 2, true]));
Check("BRD CT Finale: 1 Coda is too weak", false, soloFinale.Invoke(null, [true, 9f, 1, true]));
Check("BRD CT Finale: BV ready means the normal burst", false, soloFinale.Invoke(null, [true, 9f, 3, false]));
Check("BRD CT Finale: unknown downtime end", false, soloFinale.Invoke(null, [true, float.MaxValue, 3, true]));
Check("BRD CT Finale: not outside FRU CT", false, soloFinale.Invoke(null, [false, 9f, 3, true]));
var brdType = assembly.GetType($"{ns}BRD_Reborn", true)!;
var refreshPays = Method(brdType, "BurstEndRefreshPays");
var dotRefreshed = Method(brdType, "DotRefreshed");
Check("BRD IJ: same +30% snapshot, 25s left still pays (20s more buffed)", true, refreshPays.Invoke(null, [1.3f, 1.3f, 25f]));
Check("BRD IJ: same +15% snapshot, 28s left does not", false, refreshPays.Invoke(null, [1.15f, 1.15f, 28f]));
Check("BRD IJ: unbuffed DoTs into a +30% burst pay", true, refreshPays.Invoke(null, [1.3f, 1f, 28f]));
Check("BRD IJ: weaker buffs than the DoT's at 30s left do not", false, refreshPays.Invoke(null, [1.1f, 1.4f, 30f]));
Check("BRD IJ: timer jump 20s -> 45s is a refresh", true, dotRefreshed.Invoke(null, [20f, 45f]));
Check("BRD IJ: ticking timer is not", false, dotRefreshed.Invoke(null, [20f, 19.9f]));
var brdDoTPays = Method(assembly.GetType($"{ns}BRD_Reborn", true)!, "DoTPaysOff");
Check("BRD: no DoT 5s before downtime", false, brdDoTPays.Invoke(null, [5f]));
Check("BRD: DoT 17s before downtime (CT return length)", true, brdDoTPays.Invoke(null, [17f]));
Check("BRD: DoT with no downtime predicted", true, brdDoTPays.Invoke(null, [float.MaxValue]));
Check("BRD Reborn holds nothing for a plan", true,
	CalledMethods(brd.GetProperty("HoldBurst", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!).All(m => m != "get_Buffs"));

if (failures > 0)
{
	Console.Error.WriteLine($"{failures} check(s) failed.");
	return 1;
}

Console.WriteLine("All MCH Sync checks passed.");
return 0;
