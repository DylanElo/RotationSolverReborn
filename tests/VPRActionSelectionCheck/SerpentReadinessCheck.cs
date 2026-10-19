using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class SerpentReadinessCheck
{
    public static bool Run(string root)
    {
        var path = Path.Combine(root, "RotationSolver.Basic/Rotations/Basic/ViperRotation.cs");
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        string[] names = ["DeathRattle", "LastLash", "FirstLegacy", "SecondLegacy", "ThirdLegacy", "FourthLegacy"];
        string[] dreadActions = ["HuntersCoil", "SwiftskinsCoil", "HuntersDen", "SwiftskinsDen", "FirstGeneration", "SecondGeneration", "ThirdGeneration", "FourthGeneration"];
        string[] dreadProperties = ["DreadActive", "SwiftskinsCoilOnly", "HuntersCoilOnly", "PitActive", "SwiftskinsDenOnly", "HuntersDenOnly", "FirstGenReady", "SecondGenReady", "ThirdGenReady", "FourthGenReady"];
        var members = syntax.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(p => names.Contains(p.Identifier.Text.Replace("Ready", "")) || dreadProperties.Contains(p.Identifier.Text)).Select(p => p.ToString());
        var settings = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => names.Concat(dreadActions).Any(n => m.Identifier.Text == "Modify" + n + "PvE"))
            .Select(m => m.ToString().Replace("static partial void", "public static void"));
        // These are discrete native gauge states; 7/8/9 identify the three twin follow-ups.
        var source = "using System; enum SerpentCombo : byte {" + string.Join(',', names.Select((n, i) => n + "=" + (i + 1))) + "}"
            + "enum DreadCombo : byte { Dreadwinder=1, HuntersCoil=2, SwiftskinsCoil=3, PitOfDread=4, HuntersDen=5, SwiftskinsDen=6 } enum ActionID { None, HuntersCoilPvE, SwiftskinsCoilPvE }"
            + "enum StatusID { Reawakened } class ActionConfig { public int AoeCount; } class ActionSetting { public Func<bool> ActionCheck = null!; public Func<ActionConfig> CreateConfig = null!; public StatusID[] StatusNeed = []; }"
            + "class Gauge { public SerpentCombo SerpentCombo; public DreadCombo DreadCombo; } class Probe { public static Gauge JobGauge = new(); public static byte MaxAnguine=5, AnguineTributeStacks; public static ActionID LastGcd; public static bool IsLastGCD(ActionID action) => action == LastGcd;"
            + string.Join('\n', members) + string.Join('\n', settings) + "}";
        var compilation = CSharpCompilation.Create("VprSerpentReadiness", [CSharpSyntaxTree.ParseText(source)],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) throw new Exception(string.Join('\n', result.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var probe = assembly.GetType("Probe")!;
        var gauge = probe.GetField("JobGauge")!.GetValue(null)!;
        var state = gauge.GetType().GetField("SerpentCombo")!;
        var settingType = assembly.GetType("ActionSetting")!;
        int failures = 0;
        for (byte raw = 0; raw <= 9; raw++)
        {
            state.SetValue(gauge, Enum.ToObject(state.FieldType, raw));
            for (int i = 0; i < names.Length; i++)
            {
                var arguments = new[] { Activator.CreateInstance(settingType, nonPublic: true)! };
                probe.GetMethod("Modify" + names[i] + "PvE")!.Invoke(null, arguments);
                var canUse = (Func<bool>)settingType.GetField("ActionCheck")!.GetValue(arguments[0])!;
                if (canUse() == (raw == i + 1)) continue;
                Console.Error.WriteLine($"FAIL SerpentCombo {raw}: {names[i]} selected readiness {canUse()}, expected {raw == i + 1}");
                failures++;
            }
        }
        Console.WriteLine($"Serpent follow-up action gates: {60 - failures}/60 passed");
        var dreadState = gauge.GetType().GetField("DreadCombo")!;
        var lastGcd = probe.GetField("LastGcd")!;
        var maxAnguine = probe.GetField("MaxAnguine")!;
        var tribute = probe.GetField("AnguineTributeStacks")!;
        int dreadChecks = 0, dreadFailures = 0;
        for (byte raw = 0; raw <= 10; raw++)
        {
            dreadState.SetValue(gauge, Enum.ToObject(dreadState.FieldType, raw));
            for (int i = 0; i < dreadActions.Length; i++)
            {
                var arguments = new[] { Activator.CreateInstance(settingType, nonPublic: true)! };
                probe.GetMethod("Modify" + dreadActions[i] + "PvE")!.Invoke(null, arguments);
                var canUse = (Func<bool>)settingType.GetField("ActionCheck")!.GetValue(arguments[0])!;
                for (int last = 0; last <= 2; last++)
                for (byte max = 4; max <= 5; max++)
                for (byte stacks = 0; stacks <= 5; stacks++)
                {
                    lastGcd.SetValue(null, Enum.ToObject(lastGcd.FieldType, last));
                    maxAnguine.SetValue(null, max);
                    tribute.SetValue(null, stacks);
                    bool expected = i switch
                    {
                        0 => (raw == 1 || raw == 3) && last != 1,
                        1 => (raw == 1 || raw == 2) && last != 2,
                        2 => raw == 4 || raw == 6,
                        3 => raw == 4 || raw == 5,
                        _ => raw == i + 3 && stacks == max - (i - 4),
                    };
                    dreadChecks++;
                    if (canUse() == expected) continue;
                    if (dreadFailures < 20) Console.Error.WriteLine($"FAIL DreadCombo {raw}: {dreadActions[i]}, last GCD {last}, tribute {stacks}/{max}: readiness {canUse()}, expected {expected}");
                    dreadFailures++;
                }
            }
        }
        Console.WriteLine($"Dread/Pit/Generation action gates: {dreadChecks - dreadFailures}/{dreadChecks} passed");
        failures += dreadFailures;
        return failures == 0;
    }
}
