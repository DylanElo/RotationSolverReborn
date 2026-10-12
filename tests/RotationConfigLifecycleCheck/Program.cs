using RotationSolver.Basic.Configuration;

var configs = Service.Config;
var classicName = typeof(ClassicRotation).FullName!;
var plusName = typeof(PlusRotation).FullName!;
var classicValues = configs.GetRotationConfigurations(Job.MCH, classicName);
var plusValues = configs.GetRotationConfigurations(Job.MCH, plusName);

classicValues[nameof(ClassicRotation.BurstPlan)] = "Automatic (timeline + dungeons)";
plusValues[nameof(PlusRotation.BurstPlan)] = "Plus saved";
configs.RotationChoice = classicName;

var classic = new ClassicRotation();
var classicConfigs = new RotationConfigSet(classic);
var plus = new PlusRotation();
var plusConfigs = new RotationConfigSet(plus);

Assert(classic.BurstPlan == ClassicPlan.Default, "Classic did not fall back from the Plus-only dormant value.");
Assert(plus.BurstPlan == PlusPlan.Saved, "Unselected Plus did not load its own persisted value during construction.");
Assert(classicValues[nameof(ClassicRotation.BurstPlan)] == "Automatic (FRU + dungeons)", "Classic's dormant Plus-only value was not normalized, or constructing Plus overwrote it again.");
Assert(!classicValues.ContainsKey(nameof(PlusRotation.PlusOnlyPlan)), "Constructing Plus wrote its unique enum key into the selected Classic bucket.");
Assert(plusValues[nameof(PlusRotation.PlusOnlyPlan)] == "Plus-only default", "Plus's enum default was not persisted in Plus's own bucket.");
Assert(plusConfigs.Single(config => config.Name == nameof(PlusRotation.PlusOnlyPlan)).IsDefault,
	"A persisted enum description was not recognized as its declared default.");

plusValues[nameof(PlusRotation.BurstPlan)] = "Plus switched";
Assert(plus.BurstPlan == PlusPlan.Saved, "The runtime value changed before the rotation switch.");

configs.RotationChoice = plusName;
plusConfigs.Reload();

Assert(plus.BurstPlan == PlusPlan.Switched, "Switching did not refresh the selected rotation's runtime property.");
Assert(classic.BurstPlan == ClassicPlan.Default, "Switching Plus changed Classic's runtime property.");
Assert(classicValues[nameof(ClassicRotation.BurstPlan)] == "Automatic (FRU + dungeons)", "Switching Plus changed Classic's persisted value.");

var plusBurstPlan = plusConfigs.Single(config => config.Name == nameof(PlusRotation.BurstPlan));
Assert(!plusBurstPlan.IsDefault, "A non-default enum value was reported as default.");
plusBurstPlan.Value = plusBurstPlan.DefaultValue;
Assert(plusBurstPlan.IsDefault && plus.BurstPlan == PlusPlan.Default,
	"Resetting an enum to its declared default did not update the runtime property.");
plusBurstPlan.Value = "Plus saved";

Assert(plus.BurstPlan == PlusPlan.Saved, "Updating Plus's configuration did not update its runtime property.");
Assert(plusValues[nameof(PlusRotation.BurstPlan)] == "Plus saved", "Updating Plus did not persist to Plus's bucket.");
Assert(classicValues[nameof(ClassicRotation.BurstPlan)] == "Automatic (FRU + dungeons)", "Updating Plus leaked into Classic's bucket.");

var siblingJobValues = configs.GetRotationConfigurations(Job.GLA, plusName);
siblingJobValues[nameof(PlusRotation.BurstPlan)] = "Plus switched";
DataCenter.Job = Job.GLA;
plusConfigs.Reload();

Assert(plus.BurstPlan == PlusPlan.Switched, "A cached rotation did not follow the active job's persisted bucket.");
Assert(plusValues[nameof(PlusRotation.BurstPlan)] == "Plus saved", "Reloading a sibling job changed MCH persistence.");

Console.WriteLine("Rotation configuration lifecycle check passed.");
return 0;

static void Assert(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

internal enum ClassicPlan : byte
{
	[Description("Automatic (FRU + dungeons)")]
	Default,

	[Description("Classic saved")]
	Saved,
}

internal enum PlusPlan : byte
{
	[Description("Automatic (timeline + dungeons)")]
	Default,

	[Description("Plus saved")]
	Saved,

	[Description("Plus switched")]
	Switched,
}

internal enum PlusOnlyPlan : byte
{
	[Description("Plus-only default")]
	Default,
}

internal sealed class ClassicRotation : ICustomRotation
{
	[RotationConfig(CombatType.PvE)]
	public ClassicPlan BurstPlan { get; set; } = ClassicPlan.Default;
}

internal sealed class PlusRotation : ICustomRotation
{
	[RotationConfig(CombatType.PvE)]
	public PlusPlan BurstPlan { get; set; } = PlusPlan.Default;

	[RotationConfig(CombatType.PvE)]
	public PlusOnlyPlan PlusOnlyPlan { get; set; } = PlusOnlyPlan.Default;
}
