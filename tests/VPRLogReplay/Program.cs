using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
if (!args.Contains("--fixtures") || !args.Contains("--output")) throw new ArgumentException("--fixtures and --output are required; use --source-directory for the preserved baseline");
var sourceIndex = Array.IndexOf(args, "--source-directory");
if (sourceIndex >= 0 && sourceIndex + 1 >= args.Length) throw new ArgumentException("--source-directory requires a directory containing VPR_Reborn.cs and VPR_SyncPlus.cs");
var sourceDirectory = sourceIndex >= 0 ? Path.GetFullPath(args[sourceIndex + 1]) : null;
string SourcePath(string file) => sourceDirectory == null ? Path.Combine(root, file) : Path.Combine(sourceDirectory, Path.GetFileName(file));
string Read(string file) => File.ReadAllText(SourcePath(file));
string Members(string file, params string[] names)
{
    var syntax = CSharpSyntaxTree.ParseText(Read(file)).GetRoot();
    return string.Join("\n", names.Select(name => {
        var member = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members
            .SingleOrDefault(m => m is MethodDeclarationSyntax d && d.Identifier.Text == name || m is PropertyDeclarationSyntax p && p.Identifier.Text == name);
        return member?.ToString() ?? (new[] { "PackageRelease", "AllocateBeforeFree", "AllocateViceBeforeFree", "TimingChoice", "ReawakenPayload", "ReawakenCycle", "CandidateFinalKillIn", "LearnedBurstWindows", "NormalComboRemaining", "EarnedIreCoilLoss", "EarnedViceCoilLoss", "PrioritizeEarnedGenerator", "CalculateTimingChoice", "ReawakenAdmission", "TryUseViceCombo", "AttackAbility", "NormalComboPending", "EarnedIreCoilLossAt", "BuildSnapshot", "ReserveWeaveForIre", "ShouldDelayIreToken" }.Contains(name) ? "" : throw new Exception("Missing production member " + name));
    }));
}
const string dir = "RotationSolver/RebornRotations/Melee/";
const string nativeFile = "RotationSolver.Basic/Rotations/Basic/ViperRotation.cs";
var nativeSyntax = CSharpSyntaxTree.ParseText(Read(nativeFile)).GetRoot();
var finalKill = CSharpSyntaxTree.ParseText(Read("RotationSolver.Basic/Rotations/CustomRotation_OtherInfo.cs")).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text=="FinalKill").ToString().Replace("internal static float FinalKill(","public static float ReplayFinalKill(");
string NativeCheck(string action) {
    var method = nativeSyntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text=="Modify"+action);
    var lambda = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single(a=>a.Left.ToString()=="setting.ActionCheck").Right as ParenthesizedLambdaExpressionSyntax;
    return "public bool Replay"+action+"Check()=>"+lambda!.ExpressionBody!.ToString()+";";
}
var fingerprints = new[]{dir+"VPR_Reborn.cs",dir+"VPR_SyncPlus.cs",nativeFile,"RotationSolver.Basic/Rotations/CustomRotation_OtherInfo.cs"}.ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath(p)))));
foreach(var pair in fingerprints) Console.WriteLine(pair.Key+" SHA256 "+pair.Value);
var production = "class Reborn : Boundary {" + Members(dir + "VPR_Reborn.cs", "GeneralGCD", "AllowReawaken", "AllowViceCombo", "AllowUncoiledFury", "PreferUncoiledFury", "SpendFuryBeforeFreeReawaken", "TryUseReawaken", "TryUseUncoiledFury", "AllowSerpentsIre", "AttackAbility", "EmergencyAbility", "TryUseViceCombo") + "}\n"
    + "class Sync : Reborn {" + Members(dir + "VPR_SyncPlus.cs", "AllowReawaken", "PackageRelease", "AllocateBeforeFree", "AllocateViceBeforeFree", "ShouldUseReawaken", "TargetDying", "TargetEndsBeforeBurstPayload", "IsCurrentTargetBoss", "PlannedBurstWindow", "AllowViceCombo", "AllowUncoiledFury", "PreferUncoiledFury", "SpendFuryBeforeFreeReawaken", "UpdateExpectedPartyBuffs", "FindDebuffCarrier", "IsPartyBuffActive", "PartyBuffRemaining", "IsAnchorWorthy", "TryUseReawaken", "TryUseUncoiledFury", "CandidateAllowsBurst", "EvaluateBurstDecision", "Decision", "ShouldCommitPersonalBurst", "ShouldHoldFlexibleResources", "GeneralGCD", "UpdateBurstTracker", "ResetPartyBurstAnchor", "SecondsInPartyBurst", "BurstHeldFor", "EmergencyAbility", "ShouldUseSelectedBurstMedicine", "AllowBurstMedicine", "AllowSerpentsIre", "ShouldUseSerpentsIre", "FinishNormalCombo", "SecondsUntilExpectedPartyBurst", "ReawakenReady", "TimingChoice", "ReawakenPayload", "ReawakenCycle", "CandidateFinalKillIn", "LearnedBurstWindows", "NormalComboRemaining", "EarnedIreCoilLoss", "EarnedViceCoilLoss", "PrioritizeEarnedGenerator", "CalculateTimingChoice", "ReawakenAdmission", "TryUseViceCombo", "AttackAbility", "NormalComboPending", "EarnedIreCoilLossAt", "BuildSnapshot", "ReserveWeaveForIre", "ShouldDelayIreToken") + "}\n";
var tree = CSharpSyntaxTree.ParseText(Read(dir + "VPR_SyncPlus.cs")).GetRoot();
production += string.Join('\n', tree.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => m is TypeDeclarationSyntax t && t.Identifier.Text.StartsWith("VPRSyncPlus") || m is EnumDeclarationSyntax e && e.Identifier.Text.StartsWith("VPRSyncPlus")).Select(m => m.ToString()));
if (!production.Contains("VPRSyncPlusObservedWindow")) production += "readonly record struct VPRSyncPlusObservedWindow(float Start,float Duration,ulong Target);";
if (!production.Contains("VPRSyncPlusTimingChoice")) production += "readonly record struct VPRSyncPlusTimingChoice(bool Advance,int Count,bool FuryBefore);";
var actions = Regex.Matches(production, @"\b\w+PvE\b").Select(m => m.Value).Distinct().ToArray();
var statuses = Regex.Matches(production, @"StatusID\.(\w+)").Select(m => m.Groups[1].Value).Append("Debuff").Append("Buff").Concat(Enumerable.Range(0,20).Select(i=>"External"+i)).Distinct();
var source = "using System; using System.Linq; using System.Collections.Generic; using System.ComponentModel; using System.Text.Json; using System.IO;\n"
    + "enum ActionID{None," + string.Join(',', actions) + "} enum StatusID{" + string.Join(',', statuses) + "}\n"
    + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Boundary.cs.txt")).Replace("// NATIVE CHECKS",NativeCheck("VicewinderPvE")+NativeCheck("SerpentsIrePvE")+finalKill).Replace("// ACTIONS", string.Join('\n', actions.Select(a => $"public FakeAction {a}=new(ActionID.{a});")))
    + production + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scenarios.cs.txt"));
var compilation = CSharpCompilation.Create("VprSelection", [CSharpSyntaxTree.ParseText(source)],
    ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var stream = new MemoryStream();
var result = compilation.Emit(stream);
if (!result.Success) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"generated.cs"),source); foreach(var d in result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)) Console.Error.WriteLine(d); return 2; }
stream.Position=0;
var exitCode=(int)AssemblyLoadContext.Default.LoadFromStream(stream).GetType("Scenarios")!.GetMethod("Run")!.Invoke(null,[args])!;
File.WriteAllText(Path.Combine(args[Array.IndexOf(args,"--output")+1],"source-hashes.json"),System.Text.Json.JsonSerializer.Serialize(fingerprints,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
return exitCode;
