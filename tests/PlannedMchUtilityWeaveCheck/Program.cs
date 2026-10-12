using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

if (args.Length != 1) throw new ArgumentException("Usage: PlannedMchUtilityWeaveCheck <RotationSolver.dll>");
var path = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
	var dependency = Path.Combine(Path.GetDirectoryName(path)!, $"{name.Name}.dll");
	if (!File.Exists(dependency)) dependency = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev", $"{name.Name}.dll");
	return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
MethodInfo Method(string type, string name) => assembly.GetType(type, true)!.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
var decide = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "Decide");
var owner = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "HasNativeOwner");
var checks = 0;
void Check(string scenario, object[] inputs, int expected)
{
	var actual = (int)decide.Invoke(null, inputs)!;
	if (actual != expected) throw new Exception($"{scenario}: expected {expected}, got {actual}");
	checks++;
}
Check("ordinary safe weave", [true, true, 0f, .7f, 3f, 1.5f, .5f, true, false], 2);
Check("Blazing Shot short recast safe slot", [true, true, 0f, .7f, 3f, .9f, .3f, true, false], 2);
Check("double weave would clip", [true, true, 0f, .7f, 3f, .6f, .3f, true, false], 1);
Check("GCD ready executes before utility", [true, true, 0f, .7f, 3f, 0f, .3f, true, false], 1);
Check("native narrow deadline is urgent", [true, true, 0f, .7f, .3f, .6f, .3f, true, false], 3);
Check("protect Wildfire Full Metal handoff", [true, true, 0f, .7f, 3f, 1.5f, .5f, true, true], 1);
Check("urgent mitigation wins handoff", [true, true, 0f, .7f, .3f, 1.5f, .5f, true, true], 3);
Check("cannot execute through animation lock", [true, true, .2f, .7f, .1f, 1.5f, .5f, true, false], 1);
Check("downtime allows utility", [true, true, 0f, .7f, 3f, 0f, .3f, false, false], 2);
Check("disabled engine native fallback", [false, true, 0f, .7f, 3f, 1.5f, .5f, true, false], 0);
Check("other actions native fallback", [true, false, 0f, .7f, 3f, 1.5f, .5f, true, false], 0);
Check("expired window native fallback", [true, true, 0f, .7f, 0f, 1.5f, .5f, true, false], 0);
Check("invalid lock native fallback", [true, true, 0f, float.NaN, 3f, 1.5f, .5f, true, false], 0);
foreach (var (ready, version, expected) in new[] { (true, 1, true), (true, 0, false), (false, 1, false), (true, 2, true) })
{
	if ((bool)owner.Invoke(null, [ready, version])! != expected) throw new Exception("Duplicate executor capability/fallback policy failed");
	checks++;
}
var endpoint = Method("RotationSolver.IPC.IPCProvider", "PlannedMchUtilityWeave");
var capability = Method("RotationSolver.IPC.BMRPlan_IPCSubscriber", "get_HasNativeMchWeaveOwner");
var queue = Method("RotationSolver.Updaters.BMRPlanUpdater", "QueueActiveActions");
if (!Calls(endpoint).Contains(decide)) throw new Exception("Live IPC endpoint does not use tested policy");
if (!Calls(capability).Contains(owner)) throw new Exception("Capability probe does not use tested ownership policy");
if (!Calls(queue).Contains(capability)) throw new Exception("Legacy planner queue does not suppress duplicate execution");
if (endpoint.GetCustomAttributesData().All(a => a.AttributeType.Name != "EzIPCAttribute")) throw new Exception("Weave gate is not exposed over IPC");
checks += 4;
Console.WriteLine($"PASS {checks} MCH native planner weave and integration checks against {path}");

static List<MethodBase> Calls(MethodInfo method)
{
	var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
		.ToDictionary(o => unchecked((ushort)o.Value));
	var il = method.GetMethodBody()!.GetILAsByteArray()!;
	var calls = new List<MethodBase>();
	for (var i = 0; i < il.Length;)
	{
		ushort code = il[i++];
		if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
		var op = opcodes[code];
		if (op.OperandType == OperandType.InlineMethod)
			calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, i))!);
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
