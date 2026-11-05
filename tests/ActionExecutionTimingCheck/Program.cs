using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Read(string path)
{
    if (args.Length == 2 && args[0] == "--baseline-ref")
    {
        using var process = Process.Start(new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, ArgumentList = { "show", args[1] + ":" + path } })!;
        var source = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("git show failed");
        return source;
    }
    return File.ReadAllText(Path.Combine(root, path));
}
var commandsText = Read("RotationSolver/Commands/RSCommands_Actions.cs");
var updaterText = Read("RotationSolver/Updaters/ActionUpdater.cs");
var commands = new ClockBoundary().Visit(CSharpSyntaxTree.ParseText(commandsText).GetRoot())!;
var updater = new ClockBoundary().Visit(CSharpSyntaxTree.ParseText(updaterText).GetRoot())!;
string Methods(SyntaxNode source, params string[] names) => string.Join("\n", source.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => names.Contains(m.Identifier.Text)));
var fields = string.Join("\n", commands.DescendantNodes().OfType<FieldDeclarationSyntax>());
var production = "static partial class RSCommands {" + fields + Methods(commands, "CanDoAnAction", "DoAction", "RandomSeconds") + "}\n"
    + "static partial class ActionUpdater {" + Methods(updater, "CanDoAction", "PlayerHasLockActions", "IsPlayerOccupied", "CanQueueGcd") + "}\n";
foreach (var (name, source) in new[] { ("RSCommands_Actions", commandsText), ("ActionUpdater", updaterText) })
    Console.WriteLine($"SOURCE {name} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}");
var generated = "using System; using System.Linq; using System.Collections.Generic; using System.Numerics; using System.Reflection;\n"
    + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Boundary.cs.txt")) + production
    + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scenarios.cs.txt"));
var compilation = CSharpCompilation.Create("ActionExecutionTiming", [CSharpSyntaxTree.ParseText(generated)],
    ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
using var stream = new MemoryStream();
var result = compilation.Emit(stream);
if (!result.Success)
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "generated.cs"), generated);
    foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(diagnostic);
    return 2;
}
stream.Position = 0;
var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
return (int)assembly.GetType("Scenarios")!.GetMethod("Run")!.Invoke(null, null)!;

// Clock reads are a platform boundary, like native input/state. Leave scheduler policy intact.
sealed class ClockBoundary : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        => node.Expression is IdentifierNameSyntax { Identifier.Text: "DateTime" }
            && node.Name.Identifier.Text == "Now"
            ? SyntaxFactory.ParseExpression("Clock.Now").WithTriviaFrom(node)
            : base.VisitMemberAccessExpression(node);
}
