using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1)
{
	Console.Error.WriteLine("Usage: MCHCorePolicyCheck <RotationSolver.dll>");
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

const string ns = "RotationSolver.RebornRotations.Ranged.";
var mitigationPolicy = assembly.GetType($"{ns}MCHMitigationPolicy", true)!;
var shouldUseTactician = mitigationPolicy.GetMethod("ShouldUseTactician", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldUseDismantle = mitigationPolicy.GetMethod("ShouldUseDismantle", BindingFlags.Static | BindingFlags.NonPublic)!;
var burstActionPolicy = assembly.GetType($"{ns}MCHBurstActionPolicy", true)!;
var shouldUseWildfireAroundFullMetal = burstActionPolicy.GetMethod("ShouldUseWildfireAroundFullMetal", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldDumpHeatBeforeDowntime = burstActionPolicy.GetMethod("ShouldDumpHeatBeforeDowntime", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldForceCappedHypercharge = burstActionPolicy.GetMethod("ShouldForceCappedHypercharge", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldSpendReassembleOnDrill = burstActionPolicy.GetMethod("ShouldSpendReassembleOnDrill", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldUseChargeAction = burstActionPolicy.GetMethod("ShouldUseChargeAction", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldStartFlexibleHypercharge = burstActionPolicy.GetMethod("ShouldStartFlexibleHypercharge", BindingFlags.Static | BindingFlags.NonPublic)!;
var batteryPolicy = assembly.GetType($"{ns}MCHBatteryPolicy", true)!;
var projectedBatteryGain = batteryPolicy.GetMethod("ProjectedBatteryGain", BindingFlags.Static | BindingFlags.NonPublic)!;
var shouldDeployQueen = batteryPolicy.GetMethod("ShouldDeployQueen", BindingFlags.Static | BindingFlags.NonPublic)!;

var fullMetalWildfireCases = new[]
{
	new FullMetalWildfireCase("Wildfire waits before Full Metal", false, false, true, true, false),
	new FullMetalWildfireCase("completed Full Metal waits for the late weave", true, false, false, false, false),
	new FullMetalWildfireCase("completed Full Metal without Heat waits", true, false, true, false, false),
	new FullMetalWildfireCase("late recovery allows Wildfire", false, true, true, true, true),
	new FullMetalWildfireCase("recovery without Heat still waits", false, true, true, false, false),
	new FullMetalWildfireCase("early recovery waits for a late weave", false, true, false, true, false),
};

foreach (var item in fullMetalWildfireCases)
{
	var actual = (bool)shouldUseWildfireAroundFullMetal.Invoke(null,
	[
		item.LastGcdWasFullMetal,
		item.RecoverLostFullMetalBurst, item.LateWeave, item.HasHeatForWildfire,
	])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var downtimeHeatCases = new[]
{
	new DowntimeHeatCase("BMR can dump unreserved Heat before downtime", true, true, false, false, true, false, 120f, 10f, false, true),
	new DowntimeHeatCase("party synchronization keeps reserved Heat", true, true, false, false, true, false, 120f, 10f, true, false),
	new DowntimeHeatCase("active Wildfire blocks a downtime dump", true, true, true, false, true, false, 0f, 10f, false, false),
	new DowntimeHeatCase("Wildfire ready before downtime keeps Heat", true, true, false, false, true, true, 8f, 10f, false, false),
	new DowntimeHeatCase("Wildfire ready after downtime cannot suppress the dump", true, true, false, false, true, true, 12f, 10f, false, true),
	new DowntimeHeatCase("distant downtime does not force Heat", true, false, false, false, true, false, 120f, 90f, false, false),
};

foreach (var item in downtimeHeatCases)
{
	var actual = (bool)shouldDumpHeatBeforeDowntime.Invoke(null,
	[
		item.Enabled, item.DowntimeSoon, item.HasWildfire, item.IsOverheated,
		item.HasHeatResource, item.IsPreBurst, item.WildfireReadyIn, item.DowntimeIn,
		item.HoldFlexibleResources,
	])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var cappedHeatCases = new[]
{
	new CappedHeatCase("Heat cap bypasses tool and combo holds", true, false, false, true),
	new CappedHeatCase("active Wildfire keeps Full Metal ahead of Hypercharge", true, true, true, false),
	new CappedHeatCase("uncapped Heat uses the normal path", false, false, false, false),
};

foreach (var item in cappedHeatCases)
{
	var actual = (bool)shouldForceCappedHypercharge.Invoke(null,
		[item.HeatCapped, item.HasWildfire, item.HasFullMetalMachinist])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var mitigationCases = new[]
{
	new MitigationCase("overlap restriction blocks Tactician during Dismantle", true, true, false),
	new MitigationCase("restriction allows Tactician without Dismantle", true, false, true),
	new MitigationCase("disabled restriction allows overlap", false, true, true),
};

foreach (var item in mitigationCases)
{
	var actual = (bool)shouldUseTactician.Invoke(null, [item.RestrictOverlap, item.DismantleActive])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var dismantleCases = new[]
{
	new MitigationCase("overlap restriction blocks Dismantle during ranged mitigation", true, true, false),
	new MitigationCase("restriction allows Dismantle without ranged mitigation", true, false, true),
	new MitigationCase("disabled restriction allows Dismantle overlap", false, true, true),
};

foreach (var item in dismantleCases)
{
	var actual = (bool)shouldUseDismantle.Invoke(null, [item.RestrictOverlap, item.DismantleActive])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var reassembleCases = new[]
{
	new ReassembleCase("capped Reassemble can protect Drill", true, false, true, true),
	new ReassembleCase("dying target releases remaining Reassemble onto Drill", false, true, true, true),
	new ReassembleCase("uncapped Reassemble remains reserved from Drill", false, false, true, false),
	new ReassembleCase("released Reassemble does not force an unrelated GCD", false, true, false, false),
};

foreach (var item in reassembleCases)
{
	var actual = (bool)shouldSpendReassembleOnDrill.Invoke(null,
	[
		item.ChargesCapped, item.SpendRemainingCharge, item.NextGcdIsDrill,
	])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var chargeActionCases = new[]
{
	new ChargeActionCase("charge action can weave after a GCD", false, true),
	new ChargeActionCase("charge action waits after Wildfire or Hypercharge", true, false),
	new ChargeActionCase("charge pair splits across GCDs", true, false),
};

foreach (var item in chargeActionCases)
{
	var actual = (bool)shouldUseChargeAction.Invoke(null, [item.LastActionBlocksCharge])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var batteryCases = new[]
{
	new BatteryCase("below 50 Battery cannot deploy", 40, false, false, 0, false, false, false, false, false),
	new BatteryCase("normal policy waits at 89 Battery", 89, false, false, 0, false, false, false, false, false),
	new BatteryCase("normal policy deploys at 90 Battery", 90, false, false, 0, false, false, false, false, true),
	new BatteryCase("held capped Battery waits without another generator", 100, false, false, 0, false, true, false, false, false),
	new BatteryCase("held capped Battery deploys before a combo finisher", 100, true, false, 0, false, true, false, false, true),
	new BatteryCase("90 Battery deploys before a twenty-gauge tool", 90, false, true, 0, false, true, false, false, true),
	new BatteryCase("80 Battery can safely accept one twenty-gauge tool", 80, false, true, 0, false, true, false, false, false),
	// Chain Saw and the Excavator it grants are separate GCDs, each preceded by its own chance
	// to deploy, so they must not stack into an overcap that cannot happen. Summing them
	// force-fired a held Queen at 80 Battery; the largest single generator leaves it held.
	new BatteryCase("Chain Saw plus a ready Excavator does not fake an overcap", 80, false, false, 20, true, true, false, false, false),
	new BatteryCase("synced-down Chain Saw does not assume Excavator", 80, false, false, 20, false, true, false, false, false),
	new BatteryCase("three near generators still project only the largest", 80, true, true, 20, true, true, false, false, false),
	// A raid pull at 249s: 50 Battery, generators merely near, a hold in force. The
	// old sum read 110 and spent a 50-Battery Queen, while the gauge actually climbed +20/+20/+10
	// and reached exactly 100 twelve seconds later.
	new BatteryCase("50 Battery holds while generators are only near", 50, true, true, 20, true, true, false, false, false),
	new BatteryCase("an existing Excavator is projected separately", 90, false, false, 0, true, true, false, false, true),
	new BatteryCase("committed burst releases a 50 Battery Queen", 50, false, false, 0, false, false, true, false, true),
	new BatteryCase("a hold still wins over the committed-burst threshold", 50, false, false, 0, false, true, true, false, false),
	new BatteryCase("opener releases the 60 Battery Queen", 60, false, false, 0, false, false, false, true, true),
	new BatteryCase("a hold still wins over the opener threshold", 60, false, false, 0, false, true, false, true, false),
};

foreach (var item in batteryCases)
{
	var gain = (int)projectedBatteryGain.Invoke(null,
	[
		item.TenBatteryGcdNext, item.AirAnchorSoon, item.ChainSawGain, item.ExcavatorReady,
	])!;
	var actual = (bool)shouldDeployQueen.Invoke(null,
	[
		(byte)item.Battery, gain, item.QueenHeld, item.BurstWindow, item.OpenerRelease,
	])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

var fightFiveFrames = new[]
{
	new QueenTimelineFrame("before Chain Saw", 60, 20, true, true),
	new QueenTimelineFrame("before Excavator", 40, 20, true, false),
	new QueenTimelineFrame("before Air Anchor", 20, 20, true, false),
};
var simulatedBattery = 80;
var lostBattery = 0;
var deployments = 0;
foreach (var frame in fightFiveFrames)
{
	var deploy = (bool)shouldDeployQueen.Invoke(null,
		[(byte)simulatedBattery, frame.ProjectedGain, frame.QueenHeld, false, false])!;
	if (deploy)
	{
		deployments++;
		simulatedBattery = 0;
	}

	lostBattery += Math.Max(0, simulatedBattery + frame.ImmediateGain - 100);
	simulatedBattery = Math.Min(100, simulatedBattery + frame.ImmediateGain);
	if (deploy != frame.ExpectedDeploy)
	{
		Console.Error.WriteLine($"FAIL {frame.Name}: expected deploy {frame.ExpectedDeploy}, got {deploy}");
		return 1;
	}
}
if (deployments != 1 || lostBattery != 0 || simulatedBattery != 60)
{
	Console.Error.WriteLine($"FAIL Queen timeline: deployments={deployments}, lost={lostBattery}, final={simulatedBattery}");
	return 1;
}
Console.WriteLine("PASS Queen timeline: one deployment, zero Battery lost, final Battery 60");

var flexibleHyperchargeCases = new[]
{
	new FlexibleHyperchargeCase("no live combo allows flexible Hypercharge", 0f, 12.5f, true),
	new FlexibleHyperchargeCase("combo margin blocks flexible Hypercharge", 11.9f, 12.5f, false),
	new FlexibleHyperchargeCase("safe combo margin allows flexible Hypercharge", 12.6f, 12.5f, true),
};

foreach (var item in flexibleHyperchargeCases)
{
	var actual = (bool)shouldStartFlexibleHypercharge.Invoke(null,
	[
		item.LiveComboTime, item.SafetyWindow,
	])!;
	if (actual != item.Expected)
	{
		Console.Error.WriteLine($"FAIL {item.Name}: expected {item.Expected}, got {actual}");
		return 1;
	}
	Console.WriteLine($"PASS {item.Name}: {actual}");
}

return 0;

internal sealed record FullMetalWildfireCase(
	string Name,
	bool LastGcdWasFullMetal,
	bool RecoverLostFullMetalBurst,
	bool LateWeave,
	bool HasHeatForWildfire,
	bool Expected);

internal sealed record MitigationCase(
	string Name,
	bool RestrictOverlap,
	bool DismantleActive,
	bool Expected);

internal sealed record DowntimeHeatCase(
	string Name,
	bool Enabled,
	bool DowntimeSoon,
	bool HasWildfire,
	bool IsOverheated,
	bool HasHeatResource,
	bool IsPreBurst,
	float WildfireReadyIn,
	float DowntimeIn,
	bool HoldFlexibleResources,
	bool Expected);

internal sealed record CappedHeatCase(
	string Name,
	bool HeatCapped,
	bool HasWildfire,
	bool HasFullMetalMachinist,
	bool Expected);

internal sealed record ReassembleCase(
	string Name,
	bool ChargesCapped,
	bool SpendRemainingCharge,
	bool NextGcdIsDrill,
	bool Expected);

internal sealed record ChargeActionCase(
	string Name,
	bool LastActionBlocksCharge,
	bool Expected);

internal sealed record BatteryCase(
	string Name,
	int Battery,
	bool TenBatteryGcdNext,
	bool AirAnchorSoon,
	int ChainSawGain,
	bool ExcavatorReady,
	bool QueenHeld,
	bool BurstWindow,
	bool OpenerRelease,
	bool Expected);

internal sealed record QueenTimelineFrame(
	string Name,
	int ProjectedGain,
	int ImmediateGain,
	bool QueenHeld,
	bool ExpectedDeploy);

internal sealed record FlexibleHyperchargeCase(
	string Name,
	float LiveComboTime,
	float SafetyWindow,
	bool Expected);
