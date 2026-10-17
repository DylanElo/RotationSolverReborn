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
var brdOwner = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "HasNativeBrdOwner");
var supported = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "IsSupportedUtility");
var reserve = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "ShouldReserveUtilityWindow");
var holdGcd = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "ShouldHoldGcdForUtility");
var reservable = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "CanReserveBrdUtility");
var holdAbility = Method("RotationSolver.Updaters.PlannedMchUtilityWeave", "ShouldHoldAbilityForUtility");
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
foreach (var (ready, version, expected) in new[] { (true, 1, false), (true, 2, true), (false, 2, false), (true, 3, true) })
{
	if ((bool)brdOwner.Invoke(null, [ready, version])! != expected) throw new Exception("BRD native ownership must require handshake v2");
	checks++;
}
foreach (var (job, id, expected) in new[] { ("BRD", 7408u, true), ("BRD", 7405u, true), ("BRD", 3561u, true), ("BRD", 16889u, false), ("MCH", 16889u, true), ("MCH", 2887u, true), ("MCH", 7408u, false), ("DNC", 7408u, false) })
{
	var nativeJob = Enum.Parse(supported.GetParameters()[0].ParameterType, job);
	if ((bool)supported.Invoke(null, [nativeJob, id])! != expected) throw new Exception($"Wrong native utility allowlist: {job}/{id}");
	checks++;
}
foreach (var (name, nativeOwner, id, target, param, option, equivalent, expected) in new[] {
	("native self Minne", true, 7408u, 0, 0, "Use", false, true),
	("legacy fallback cannot hold GCD for unselectable queued utility", false, 7408u, 0, 0, "Use", false, false),
	("conditional Troubadour already covered by equivalent buff", true, 7405u, 0, 0, "Use88IfNotActive", true, false),
	("conditional Troubadour without equivalent buff", true, 7405u, 0, 0, "Use88IfNotActive", false, true),
	("unconditional Troubadour preserves native condition", true, 7405u, 0, 0, "Use88", true, true),
	("target override eligibility stays with native executor", true, 7408u, 1, 0, "Use", false, false),
	("dispel eligibility stays with native executor", true, 3561u, 0, 0, "Use", false, false) })
{
	if ((bool)reservable.Invoke(null, [nativeOwner, id, target, param, option, equivalent])! != expected) throw new Exception(name);
	checks++;
}
foreach (var (name, start, end, gcd, expectedReserve, expectedHold) in new[] {
	("upcoming narrow Minne saves weave", .3f, .7f, 1f, true, false),
	("GCD immediately before Minne yields until utility accepted", .1f, .5f, 0f, true, true),
	("GCD now still leaves end of future utility window available", .5f, .9f, 0f, false, false),
	("safe future Minne does not cause arbitrary GCD delay", .69f, 1.39f, 0f, false, false),
	("queued future GCD would cover entire narrow utility window", .65f, 1.15f, .55f, true, true),
	("already open narrow window still holds GCD", -.1f, .3f, 0f, true, true),
	("utility future enough for next GCD slot", 1f, 1.5f, 0f, false, false),
	("ordinary broad utility needs no GCD hold", 0f, 5f, 0f, false, false),
	("expired utility never reserves or holds", -1f, 0f, 0f, false, false),
	("invalid forecast never holds", float.NaN, .5f, 0f, false, false),
	("invalid end never holds", 0f, float.NaN, 0f, false, false),
	("inverted window never holds", .6f, .4f, 0f, false, false),
	("GCD ready after opening needs no pre-window hold", .2f, .6f, .4f, true, false) })
{
	if ((bool)reserve.Invoke(null, [start, end, gcd])! != expectedReserve || (bool)holdGcd.Invoke(null, [start, end, gcd])! != expectedHold)
		throw new Exception(name);
	checks += 2;
}
if (!(bool)reserve.Invoke(null, [.4f, .8f, 2.48f])! || (bool)reserve.Invoke(null, [.4f, .8f, 0f])!)
	throw new Exception("No-GCD downtime must reserve against next idle window rather than an already-ready GCD");
checks++;
foreach (var (end, expected) in new[] { (.68f, true), (.7f, true), (.71f, false), (0f, false), (float.NaN, false), (float.PositiveInfinity, false) })
{
	if ((bool)holdAbility.Invoke(null, [end])! != expected) throw new Exception("First filler must not lock out imminent native utility");
	checks++;
}
var endpoint = Method("RotationSolver.IPC.IPCProvider", "PlannedMchUtilityWeave");
var capability = Method("RotationSolver.IPC.BMRPlan_IPCSubscriber", "get_HasNativeMchWeaveOwner");
var brdCapability = Method("RotationSolver.IPC.BMRPlan_IPCSubscriber", "get_HasNativeBrdWeaveOwner");
var queue = Method("RotationSolver.Updaters.BMRPlanUpdater", "QueueActiveActions");
if (!Calls(endpoint).Contains(decide)) throw new Exception("Live IPC endpoint does not use tested policy");
if (!Calls(capability).Contains(owner)) throw new Exception("Capability probe does not use tested ownership policy");
if (!Calls(queue).Contains(capability)) throw new Exception("Legacy planner queue does not suppress duplicate execution");
if (!Calls(queue).Contains(brdCapability)) throw new Exception("BRD planner must suppress duplicate native execution");
if (!Calls(queue).Contains(reserve) || !Calls(queue).Contains(holdGcd)) throw new Exception("Live BRD planner does not reserve weave slots and narrow windows");
if (!Calls(queue).Contains(reservable)) throw new Exception("Live BRD lookahead ignores tested native ownership or eligibility");
if (!Calls(queue).Contains(holdAbility)) throw new Exception("Live BRD lookahead allows first filler to lock out urgent utility");
if (!Calls(endpoint).Contains(supported)) throw new Exception("Live IPC does not use tested job allowlist");
if (endpoint.GetCustomAttributesData().All(a => a.AttributeType.Name != "EzIPCAttribute")) throw new Exception("Weave gate is not exposed over IPC");
checks += 9;
Console.WriteLine($"PASS {checks} BRD/MCH native planner weave and integration checks against {path}");

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
