using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Read(string path) {
 if(args.Length==2 && args[0]=="--baseline-ref") {
  var p=Process.Start(new ProcessStartInfo("git"){WorkingDirectory=root,RedirectStandardOutput=true,ArgumentList={"show",args[1]+":"+path}})!;
  var value=p.StandardOutput.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new Exception("git show failed");return value;
 }
 return File.ReadAllText(Path.Combine(root,path));
}
var objectText=Read("RotationSolver.Basic/Helpers/ObjectHelper.cs");
var targetText=Read("RotationSolver.Basic/Actions/ActionTargetInfo.cs");
var objects=CSharpSyntaxTree.ParseText(objectText).GetRoot();
var targets=CSharpSyntaxTree.ParseText(targetText).GetRoot();
string Method(string name)=>string.Join("\n",objects.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m=>m.Identifier.Text==name).Select(m=>m.ToString()));
var priority=Method("IsTopPriorityHostile")+Method("IsAttackable");
var helperText=Read("RotationSolver.Basic/Helpers/TargetHelper.cs");
var validity=CSharpSyntaxTree.ParseText(helperText).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text=="ValidityCheck").ToString().Replace("private static bool","public static bool");
var crystal=objects.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m=>m.Identifier.Text=="IsFRUCrystalPriority");
var fields=string.Join("\n",objects.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f=>f.Declaration.Variables.Any(v=>v.Identifier.Text.StartsWith("FRUCrystal"))));
var methods=string.Join("\n",new[]{"FindHostile","FindHostileRaw"}.Select(name=>targets.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(m=>m.Identifier.Text==name).ToString()));
var production="static class TargetHelper {"+validity+"} static partial class ObjectHelper {"+priority+fields+crystal+"}\nstatic class Selector { public static IBattleChara? Pick(IEnumerable<IBattleChara>? battleChara, TargetingType? commandTargeting=null) {return FindHostile();"+methods+"}}\n";
Console.WriteLine("SOURCE ObjectHelper "+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(objectText))));
Console.WriteLine("SOURCE TargetHelper "+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(helperText))));
Console.WriteLine("SOURCE ActionTargetInfo "+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetText))));
var generated="using System; using System.Linq; using System.Collections.Generic; using System.Numerics;\n"+File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Boundary.cs.txt"))+production+File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Scenarios.cs.txt"));
var compilation=CSharpCompilation.Create("TargetDecisions",[CSharpSyntaxTree.ParseText(generated)],((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p=>MetadataReference.CreateFromFile(p)),new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true));
using var stream=new MemoryStream();var result=compilation.Emit(stream);
if(!result.Success){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"generated.cs"),generated);foreach(var d in result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error))Console.Error.WriteLine(d);return 2;}
stream.Position=0;var assembly=AssemblyLoadContext.Default.LoadFromStream(stream);return (int)assembly.GetType("Scenarios")!.GetMethod("Run")!.Invoke(null,null)!;
