using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

if (args.Length != 1) throw new ArgumentException("Usage: NoCastingStatusWindowCheck <RotationSolver.dll>");
var path = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
	var dependency = Path.Combine(Path.GetDirectoryName(path)!, $"{name.Name}.dll");
	if (!File.Exists(dependency)) dependency = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev", $"{name.Name}.dll");
	return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var method = assembly.GetType("RotationSolver.Updaters.NoCastingStatusWindow", true)!
	.GetMethod("IsActive", BindingFlags.Static | BindingFlags.NonPublic)!;
var checks = 0;
void Check(uint id, float remaining, bool expected)
{
	var actual = (bool)method.Invoke(null, [id, remaining])!;
	if (actual != expected) throw new Exception($"Status {id}, {remaining}s: expected {expected}, got {actual}");
	checks++;
}
foreach (uint id in new uint[] { 4747, 4748, 4759, 4760, 4751, 4976 })
{
	Check(id, 32f, false);
	Check(id, 7f, false);
	Check(id, 3f, false);
	Check(id, 2.99f, true);
	Check(id, 0f, true); // still present until the game removes it
}
// Observed: Higanbana cancelled, then Midare 11.2s later.
Check(4747, 3.835f, false);
Check(4751, 28.795f, false);
Check(4751, 17.608f, false);
// Existing Pyretic, Acceleration Bomb and custom-list behavior stays unchanged.
foreach (uint id in new uint[] { 639, 960, 1072, 1132, 99999 })
{
	Check(id, 20f, true);
	Check(id, 1f, true);
}
var helper = assembly.GetType("RotationSolver.Helpers.NoCastingStatusHelper", true)!
	.GetMethod("PlayerHasNoCastingStatus", BindingFlags.Static | BindingFlags.NonPublic)!;
if (!Calls(helper).Contains(method)) throw new Exception("NoCastingStatusHelper does not use the tested status window");
checks++;
foreach (var (owner, name) in new[] { ("CancelCastUpdater", "UpdateCancelCast"), ("AutoAttackUpdater", "PlayerHasNoCastingStatus") })
{
	var caller = assembly.GetType($"RotationSolver.Updaters.{owner}", true)!.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
	if (!Calls(caller).Contains(helper)) throw new Exception($"{owner}.{name} does not use NoCastingStatusHelper");
	checks++;
}
Console.WriteLine($"PASS {checks} no-casting window and integration checks against {path}");

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
