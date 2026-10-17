using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string? baseline = args.Length == 2 && args[0] == "--baseline-ref" ? args[1] : null;
string ReadPath(string path)
{
    if (baseline == null) return File.ReadAllText(Path.Combine(root, path));
    var p = Process.Start(new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, ArgumentList = { "show", baseline + ":" + path } })!;
    var source = p.StandardOutput.ReadToEnd(); p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception("git show failed");
    return source;
}
string Read(string file) => ReadPath("RotationSolver/RebornRotations/Ranged/" + file + ".cs");
var brd = CSharpSyntaxTree.ParseText(Read("BRD_Reborn")).GetRoot();
var mch = CSharpSyntaxTree.ParseText(Read("MCH_Reborn")).GetRoot();
var sync = CSharpSyntaxTree.ParseText(Read("MCH_SyncPlus")).GetRoot();
var bardBase = CSharpSyntaxTree.ParseText(ReadPath("RotationSolver.Basic/Rotations/Basic/BardRotation.cs")).GetRoot();
string Members(SyntaxNode source, string owner, params string[] names)
{
    var cls = source.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == owner);
    return string.Join("\n", names.Select(name =>
    {
        var member = cls.Members.SingleOrDefault(m => m is MethodDeclarationSyntax d && d.Identifier.Text == name || m is PropertyDeclarationSyntax p && p.Identifier.Text == name);
        if (member == null && baseline != null && name is "TryApplyDoT" or "CanUseDoT" or "ShouldReserveP4BurstWeave" or "ShouldReserveSongWeave" or "ReserveFruSongWeave" or "TargetLivesForDoT" or "HoldReassembleForChainSawCleave") return "";
        if (member == null) throw new Exception($"Required production method missing: {owner}.{name}");
        var body = member.ToString();
        Console.WriteLine($"SOURCE {owner}.{name} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..12]}");
        return body;
    }));
}
string Whole(SyntaxNode source, string name) => source.DescendantNodes().OfType<TypeDeclarationSyntax>().Single(c => c.Identifier.Text == name).ToString();
var brdConstants = string.Join('\n', brd.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "BRD_Reborn")
    .Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)).Select(f => f.ToString()));
var nativeBardActions = new[] { "StraightShotPvE", "RefulgentArrowPvE", "WideVolleyPvE", "ShadowbitePvE", "BurstShotPvE", "HeavyShotPvE", "LadonsbitePvE", "QuickNockPvE", "StormbitePvE", "CausticBitePvE", "WindbitePvE", "VenomousBitePvE" };
var nativeBard = "class NativeBardDefaults {" + Members(bardBase, "BardRotation", nativeBardActions.Select(a => "Modify" + a).ToArray()).Replace("static partial void", "static void")
    + " public static void Apply(FakeAction a) { var s=a.Setting; switch(a.ID){"
    + string.Join("", nativeBardActions.Select(a => $"case ActionID.{a}: Modify{a}(ref s); break;"))
    + "} a.Setting=s; if(s.CreateConfig!=null)a.Config=s.CreateConfig(); } }\n";
var production = nativeBard + "class Brd : Boundary {" + brdConstants + Members(brd, "BRD_Reborn", "GeneralGCD", "EmergencyAbility", "AttackAbility", "FinalKillDump", "BurstBuffEndsWithin", "ChargesCapInDowntime", "ShouldReserveP4BurstWeave", "ShouldReserveSongWeave", "ReserveFruSongWeave", "TwoStackPPBeforeEmpyreal", "CanUseApexArrow", "TryApplyDoT", "CanUseDoT", "DoTPaysOff", "TargetLivesForDoT", "BurstEndRefreshPays", "DotSnapshotOn", "TrackDotSnapshots", "NextDotSnapshot", "DotRefreshed", "BuffSnapshotOn", "FinaleWeight") + "}\n"
    + "class Mch : Boundary {" + Members(mch, "MCH_Reborn", "GeneralGCD", "AttackAbility", "HoldReassembleForChainSawCleave") + "}\n"
    + "class Sync : Mch {" + string.Join(" ", sync.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "MCH_SyncPlus").Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) && f.ToString().Contains(" Final")).Select(f => f.ToString())) + Members(sync, "MCH_SyncPlus", "GeneralGCD", "AttackAbility") + "}\n"
    + Whole(mch, "MCHBurstActionPolicy") + "\n" + Whole(sync, "MCHSyncPlusPolicy");
var actions = Regex.Matches(production, @"\b\w+PvE\b").Select(m => m.Value).Distinct().Order().ToArray();
var statuses = Regex.Matches(production, @"StatusID\.(\w+)").Select(m => m.Groups[1].Value).Distinct().ToArray();
var generated = "using System; using System.Linq; using System.Collections.Generic; using System.Numerics; using ActionSetting=FakeSetting;\n"
    + "enum ActionID { None," + string.Join(',', actions) + "}\n"
    + "enum StatusID {" + string.Join(',', statuses) + "}\n"
    + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Boundary.cs.txt"))
        .Replace("// ACTIONS", string.Join('\n', actions.Select(a => $"public FakeAction {a} = new(ActionID.{a});")))
    + production + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scenarios.cs.txt"));
var compilation = CSharpCompilation.Create("SelectionScenarios", [CSharpSyntaxTree.ParseText(generated)],
    ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var stream = new MemoryStream();
var result = compilation.Emit(stream);
if (!result.Success)
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "generated.cs"), generated);
    foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(d);
    return 2;
}
stream.Position = 0;
var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
return (int)assembly.GetType("Scenarios")!.GetMethod("Run")!.Invoke(null, null)!;
