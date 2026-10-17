using RotationSolver.Basic.Helpers;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

var budget = new WeaveBudget();
var checks = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception(name);
    checks++;
}
bool Use(uint id, bool accepted = true, ushort sequence = 0, int reserved = 0, bool utility = false)
    => budget.TryUse(true, false, false, id, () =>
    {
        if (accepted && sequence != 0) budget.RecordRequest(id, sequence);
        return accepted;
    }, reserved, utility);
void Gcd(uint effect = 0) => budget.Record(true, 100, effect);

Check("initial slot available", !budget.Exhausted);
Gcd();
Check("first accepted request leaves one slot before its effect", Use(101, sequence: 1) && budget.Used == 1);
Check("second accepted request exhausts budget before effects", Use(102, sequence: 2) && budget.Exhausted);
var nativeCalls = 0;
Check("third native execution is never invoked", !budget.TryUse(true, false, false, 103, () => { nativeCalls++; return true; }) && nativeCalls == 0);
budget.Record(false, 101, 1, 1);
budget.Record(false, 102, 2, 2);
Check("confirmations settle reservations without spending twice", budget.Used == 2);
Check("third selection is blocked", budget.ShouldBlock(false, false));
Check("true GCD selection remains allowed", !budget.ShouldBlock(true, false));
Check("next accepted GCD waits for its confirmed effect to reset budget", budget.TryUse(true, true, false, 100, () => true) && budget.Exhausted);
Gcd(3);
Check("confirmed GCD resets budget", budget.Used == 0);
Check("failed native use releases reservation", !Use(101, false) && budget.Used == 0);
try { budget.TryUse(true, false, false, 101, () => throw new InvalidOperationException("test")); }
catch (InvalidOperationException) { }
Check("throwing native use releases reservation", budget.Used == 0);
Check("queued accepted request reserves one slot", Use(101) && budget.Used == 1);
Check("repeated queued same-ID request cannot consume another charge", !Use(101) && budget.Used == 1);
budget.RecordRequest(101, 4);
budget.Record(false, 101, 4, 4);
Check("queued request associates its later native sequence once", budget.Used == 1);
Check("same charged action can execute again after confirmation", Use(101, sequence: 5) && budget.Exhausted);
budget.Record(false, 101, 5, 5);
Gcd(6);
budget.RecordRequest(201, 6);
Check("external BMR or manual request spends first slot immediately", budget.Used == 1);
Check("one RSR weave can follow an external request", Use(101, sequence: 7));
Check("two total accepted requests stop third RSR request", !Use(102));
budget.Record(false, 201, 7, 6);
budget.Record(false, 101, 8, 7);
Check("external and RSR confirmations both settle", budget.Used == 2);
Check("outer UseAction observer does not recount inner UseActionLocation request", !budget.RecordRequest(201, 6) && budget.Used == 2);
budget.RecordRequest(202, 8);
Check("manual native input is observed without suppressing it", budget.Used == 3 && budget.Exhausted);
Gcd(9);
Check("first RSR weave before planned utility allowed", Use(101, sequence: 9, reserved: 1));
Check("second filler yields a reserved utility slot", !Use(102, reserved: 1));
Check("reserved native utility remains available", !budget.ShouldBlock(false, false, 1, true));
budget.RecordRequest(201, 10);
Check("BMR utility plus first RSR request fills shared cap", budget.Exhausted);
Check("urgent utility cannot override hard two-weave cap", budget.ShouldBlock(false, false, 1, true));
Gcd(10);
Check("two imminent utilities reserve both slots", !Use(101, reserved: 2));
budget.RecordRequest(201, 11);
budget.RecordRequest(202, 12);
Check("two native utilities use exactly two slots", budget.Used == 2);
budget.Record(false, 201, 11, 11);
budget.Record(false, 202, 12, 12);
Check("duplicate effect does not spend a third slot", budget.Used == 2);
budget.Record(true, 100, 10);
Check("replayed multi-target GCD effect cannot reopen budget", budget.Exhausted);
Gcd(13);
Check("new confirmed GCD still reopens budget after duplicate", !budget.Exhausted);
budget.Record(false, 301, 14, 13);
budget.Record(false, 301, 14, 13);
Check("duplicated manual effect counts once", budget.Used == 1);
Check("potion shares ability budget", Use(49235, sequence: 14) && budget.Exhausted);
budget.Record(false, 49235, 15, 14);
Check("potion confirmation settles once", budget.Used == 2);
Check("mudra exemption remains executable", budget.TryUse(true, false, true, 2259, () => true));
Check("engine outside combat remains uncapped", budget.TryUse(false, false, false, 101, () => true));
budget.Reset();
Check("combat reset clears pending and effects", budget.Used == 0 && Use(101, sequence: 1));
Gcd(1);
Check("new combat can reuse previous effect numbers", budget.Used == 0);
budget.Reset();
budget.Record(true, 100, 1, 0, 709);
Check("no-target absence must last a full GCD before idle reopening", !budget.RefreshIdleWindow(false, 2.5f, 709.1));
Check("downtime Minuet request accepted", budget.TryUse(true, false, false, 3559, () => { budget.RecordRequest(3559, 20, 737); return true; }, now: 737));
budget.Record(false, 3559, 2, 20, 737.1);
Check("idle no-target interval opens fresh window before Troubadour", budget.RefreshIdleWindow(false, 2.5f, 742.8));
budget.RecordRequest(7405, 21, 742.8);
budget.Record(false, 7405, 3, 21, 742.9);
Check("CT Finale survives prior song and planned mitigation", budget.TryUse(true, false, false, 25785, () => { budget.RecordRequest(25785, 22, 744.1); return true; }, now: 744.1));
budget.Record(false, 25785, 4, 22, 744.2);
Check("third action inside same idle GCD duration stays blocked", !budget.RefreshIdleWindow(false, 2.5f, 746) && budget.Exhausted);
Check("available GCD prevents idle reset and restarts absence timer", !budget.RefreshIdleWindow(true, 2.5f, 746.1) && budget.Exhausted);
Check("a single missing-target frame cannot reset old budget", !budget.RefreshIdleWindow(false, 2.5f, 747));
Check("idle reset needs sustained absence and aged actions", budget.RefreshIdleWindow(false, 2.5f, 749.5));
budget.RecordRequest(201, 23, 750);
Check("unconfirmed queued native requests cannot expire into extra actions", !budget.RefreshIdleWindow(false, 2.5f, 800));
budget.Record(false, 201, 5, 23, 800);
Check("late confirmation waits another full GCD duration", !budget.RefreshIdleWindow(false, 2.5f, 801));
Check("settled long-idle window can reopen", budget.RefreshIdleWindow(false, 2.5f, 802.5));
Check("unknown or invalid GCD duration never resets budget", !budget.RefreshIdleWindow(false, float.NaN, 900) && !budget.RefreshIdleWindow(false, 0, 900));
budget.Reset();
budget.Record(true, 100, 50, 0, 1000);
budget.RecordRequest(101, 30, 1000.5);
budget.RecordRequest(102, 31, 1001);
budget.Record(true, 100, 50, 0, 1000.3);
Check("same-counter GCD callback within 1s stays a duplicate", budget.Exhausted);
budget.Record(true, 100, 50, 0, 1002.5);
Check("stale-counter GCD a full GCD later still reopens the budget", budget.Used == 0);

if (args.Length == 1)
{
    var path = Path.GetFullPath(args[0]);
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var dependency = Path.Combine(Path.GetDirectoryName(path)!, $"{name.Name}.dll");
        if (!File.Exists(dependency)) dependency = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev", $"{name.Name}.dll");
        return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    MethodInfo Method(string type, string method) => assembly.GetType(type, true)!.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
    var execute = Method("RotationSolver.Commands.RSCommands", "DoAction");
    var data = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(path)!, "RotationSolver.Basic.dll"));
    var guard = data.GetType("RotationSolver.Basic.DataCenter", true)!.GetMethod("TryUseWithinWeaveLimit", BindingFlags.Static | BindingFlags.NonPublic)!;
    Check("production final execution uses tested shared guard", Calls(execute).Any(m => m.Name == guard.Name && m.DeclaringType == guard.DeclaringType));
    Check("production shared execution calls actual budget TryUse", Calls(guard).Any(m => m.Name == "TryUse" && m.DeclaringType?.Name == "WeaveBudget"));
    Check("UseAction observer reaches native accepted-request recorder", Calls(Method("RotationSolver.Updaters.ActionQueueManager", "UseOriginal")).Any(m => m.Name == "ObserveAccepted"));
    Check("native BMR UseActionLocation path reaches same observer", Calls(Method("RotationSolver.Updaters.ActionQueueManager", "UseActionLocationDetour")).Any(m => m.Name == "ObserveAccepted"));
    Check("native accepted-request observer records budget before effects", Calls(Method("RotationSolver.Updaters.ActionQueueManager", "ObserveAccepted")).Any(m => m.Name == "RecordRequest" && m.DeclaringType?.Name == "WeaveBudget"));
    Check("effect handler settles actual native sequence", Calls(Method("RotationSolver.Watcher", "ActionFromSelf")).Any(m => m.Name == "Record" && m.DeclaringType?.Name == "WeaveBudget"));
    var idleGuard = data.GetType("RotationSolver.Basic.DataCenter", true)!.GetMethod("RefreshWeaveBudget", BindingFlags.Static | BindingFlags.NonPublic)!;
    Check("native idle boundary checks both casting and animation lock", Calls(idleGuard).Any(m => m.Name == "get_IsCasting") && Calls(idleGuard).Any(m => m.Name == "get_AnimationLock"));
    Check("native idle boundary calls tested budget refresh", Calls(idleGuard).Any(m => m.Name == "RefreshIdleWindow"));
}
Console.WriteLine($"PASS: {checks} weave request, execution, reservation, and effect checks");

static List<MethodBase> Calls(MethodInfo method)
{
    var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
    var il = method.GetMethodBody()!.GetILAsByteArray()!;
    var calls = new List<MethodBase>();
    for (var i = 0; i < il.Length;)
    {
        ushort code = il[i++];
        if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
        var op = opcodes[code];
        if (op.OperandType == OperandType.InlineMethod) calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, i))!);
        i += op.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1, OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i), _ => 4 };
    }
    return calls;
}
