using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var sourceIndex = Array.IndexOf(args, "--source-directory");
if (sourceIndex >= 0 && sourceIndex + 1 >= args.Length) throw new ArgumentException("--source-directory requires a directory containing VPR_Reborn.cs and VPR_SyncPlus.cs");
var sourceDirectory = sourceIndex >= 0 ? Path.GetFullPath(args[sourceIndex + 1]) : args.Contains("--before") ? Path.Combine(root, "artifacts/elite-intent-20261017/before") : null;
string SourcePath(string file) => sourceDirectory == null ? Path.Combine(root, file) : Path.Combine(sourceDirectory, Path.GetFileName(file));
string Read(string file) => File.ReadAllText(SourcePath(file));
string Members(string file, params string[] names)
{
    var syntax = CSharpSyntaxTree.ParseText(Read(file)).GetRoot();
    return string.Join("\n", names.Select(name => {
        var member = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members
            .SingleOrDefault(m => m is MethodDeclarationSyntax d && d.Identifier.Text == name || m is PropertyDeclarationSyntax p && p.Identifier.Text == name);
        return member?.ToString() ?? (sourceDirectory != null && new[] { "SpendFuryBeforeFreeReawaken", "TryUseReawaken", "TryUseUncoiledFury", "CandidateAllowsBurst", "EvaluateBurstDecision", "ShouldUseSelectedBurstMedicine" }.Contains(name) ? "" : throw new Exception("Missing production member " + name));
    }));
}
const string dir = "RotationSolver/RebornRotations/Melee/";
foreach (var file in new[] { "VPR_Reborn.cs", "VPR_SyncPlus.cs" }) Console.WriteLine(file + " SHA256 " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath(dir + file)))));
var production = "class Reborn : Boundary {" + Members(dir + "VPR_Reborn.cs", "GeneralGCD", "AllowReawaken", "AllowViceCombo", "AllowUncoiledFury", "PreferUncoiledFury", "SpendFuryBeforeFreeReawaken", "TryUseReawaken", "TryUseUncoiledFury") + "}\n"
    + "class Sync : Reborn {" + Members(dir + "VPR_SyncPlus.cs", "AllowReawaken", "ShouldUseReawaken", "TargetDying", "TargetEndsBeforeBurstPayload", "IsCurrentTargetBoss", "PlannedBurstWindow", "AllowViceCombo", "AllowUncoiledFury", "PreferUncoiledFury", "SpendFuryBeforeFreeReawaken", "UpdateExpectedPartyBuffs", "FindDebuffCarrier", "IsPartyBuffActive", "PartyBuffRemaining", "IsAnchorWorthy", "TryUseReawaken", "TryUseUncoiledFury", "CandidateAllowsBurst", "EvaluateBurstDecision", "Decision", "ShouldCommitPersonalBurst", "ShouldHoldFlexibleResources", "GeneralGCD", "UpdateBurstTracker", "ResetPartyBurstAnchor", "SecondsInPartyBurst", "BurstHeldFor", "EmergencyAbility", "ShouldUseSelectedBurstMedicine", "AllowBurstMedicine") + "}\n";
var tree = CSharpSyntaxTree.ParseText(Read(dir + "VPR_SyncPlus.cs")).GetRoot();
production += string.Join('\n', tree.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => m is TypeDeclarationSyntax t && t.Identifier.Text.StartsWith("VPRSyncPlus") || m is EnumDeclarationSyntax e && e.Identifier.Text.StartsWith("VPRSyncPlus")).Select(m => m.ToString()));
var actions = Regex.Matches(production, @"\b\w+PvE\b").Select(m => m.Value).Distinct().ToArray();
var statuses = Regex.Matches(production, @"StatusID\.(\w+)").Select(m => m.Groups[1].Value).Append("Debuff").Append("Buff").Distinct();
var source = "using System; using System.Linq; using System.Collections.Generic; using System.ComponentModel;\n"
    + "enum ActionID{None," + string.Join(',', actions) + "} enum StatusID{" + string.Join(',', statuses) + "}\n"
    + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Boundary.cs.txt")).Replace("// ACTIONS", string.Join('\n', actions.Select(a => $"public FakeAction {a}=new(ActionID.{a});")))
    + production + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scenarios.cs.txt"));
var compilation = CSharpCompilation.Create("VprSelection", [CSharpSyntaxTree.ParseText(source)],
    ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var stream = new MemoryStream();
var result = compilation.Emit(stream);
if (!result.Success) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"generated.cs"),source); foreach(var d in result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)) Console.Error.WriteLine(d); return 2; }
stream.Position=0;
return (int)AssemblyLoadContext.Default.LoadFromStream(stream).GetType("Scenarios")!.GetMethod("Run")!.Invoke(null,null)!;
