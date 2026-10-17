using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var folder = Path.Combine(root, "RotationSolver/RebornRotations/Ranged");
CompilationUnitSyntax Read(string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(folder, name + ".cs"))).GetCompilationUnitRoot();
var plan = Read("MCH_Plan");
var sync = Read("MCH_SyncPlus");
var mch = Read("MCH_Reborn");
string Type(CompilationUnitSyntax source, string name) => source.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Single(t => t.Identifier.Text == name).ToString();
string Member(CompilationUnitSyntax source, string owner, string name)
{
    var member = source.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == owner).Members.Single(m => m switch {
        PropertyDeclarationSyntax p => p.Identifier.Text == name,
        MethodDeclarationSyntax f => f.Identifier.Text == name,
        _ => false
    });
    var text = member.ToString();
    Console.WriteLine($"SOURCE {owner}.{name} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12]}");
    return text.Replace("protected override", "protected virtual");
}
// Compile the whole plan, its real cached dispatcher and final Wildfire gate. Only external state is faked.
var planClass = plan.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "MCH_Plan").WithAttributeLists(default).ToString().Replace("public sealed class", "sealed class");
var production = planClass + string.Join("\n", new[] { "MCHPlanWindow", "MCHPlanSnapshot", "MCHPlanScheduler" }.Select(n => Type(plan, n)))
    + string.Join("\n", new[] { "MCHSyncPlusPlan", "MCHSyncPlusBurstGate", "MCHSyncPlusResourcePlan", "MCHSyncPlusDowntimeAction", "MCHSyncPlusDecision", "MCHSyncPlusSnapshot", "MCHSyncPlusScheduler" }.Select(n => Type(sync, n)));
var methods = string.Join("\n", new[] { "Decision", "SynchronizePartyBurst", "TimelineAvailable", "ShouldUseWildfire", "ShouldCommitPersonalBurst", "ShouldUseBarrelOnCooldown" }.Select(n => Member(sync, "MCH_SyncPlus", n)));
var policy = "static class MCHSyncPlusPolicy {" + string.Join("\n", new[] { "ShouldUseWildfire", "StableDecisionReason", "BarrelOnCooldownPhase", "ShouldUseDowntimeBarrel" }.Select(n => Member(sync, "MCHSyncPlusPolicy", n))) + "}"
    + "static class MCHBurstActionPolicy {" + Member(mch,"MCHBurstActionPolicy","ShouldUseWildfireAroundFullMetal") + "}";
var attack = mch.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "MCH_Reborn").Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "AttackAbility");
var selection = attack.Body!.Statements.SkipWhile(s => !s.ToString().StartsWith("var LowLevelHyperCheck =")).Take(3).ToArray();
if (selection.Length != 3 || selection[2] is not IfStatementSyntax branch || branch.Condition.ToString() != "ShouldUseWildfire && !bmrBlockWildfire") throw new Exception("Production Wildfire selector shape changed");
var syncAttack = sync.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "MCH_SyncPlus").Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "AttackAbility");
var padding = syncAttack.Body!.Statements.First();
if (padding is not IfStatementSyntax pad || pad.Condition.ToString() != "PaddingFatebreaker") throw new Exception("Production padding gate shape changed");
var selector = "public bool SelectWildfire(out object? act) {act=null;" + padding + string.Join("\n", selection.Select(s => s.ToString())) + "return false;}";
var source = "using System; using System.Linq; using System.Collections.Generic; using System.ComponentModel;\n"
    + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Boundary.cs.txt")).Replace("// PRODUCTION MEMBERS", methods + selector)
    + production + policy + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scenarios.cs.txt"));
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("MCHPlanFallbackCompiled", [CSharpSyntaxTree.ParseText(source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var stream = new MemoryStream();
var result = compilation.Emit(stream);
if (!result.Success) { foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(d); return 1; }
stream.Position = 0;
var personal = args.Contains("--before-config-fix");
var configIndex = Array.IndexOf(args,"--config");
if(configIndex>=0) {
    using var config = JsonDocument.Parse(File.ReadAllText(args[configIndex+1]));
    var setting = config.RootElement.GetProperty("_rotationConfigurationsDict").GetProperty("MCH").GetProperty("RotationSolver.RebornRotations.Ranged.MCH_Plan").GetProperty("SyncPlusBurstPlan").GetString();
    personal = setting switch {"Trust party buffs"=>false,"Solo / ignore party buffs"=>true,_=>throw new Exception("Unexpected saved burst strategy: "+setting)};
    Console.WriteLine("CONFIG MCH_Plan.SyncPlusBurstPlan="+setting);
}
return (int)AssemblyLoadContext.Default.LoadFromStream(stream).GetType("Scenarios")!.GetMethod("Run")!.Invoke(null, [personal])!;
