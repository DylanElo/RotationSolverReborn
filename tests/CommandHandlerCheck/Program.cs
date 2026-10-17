using ECommons.DalamudServices;
using ECommons.GameHelpers;
using RotationSolver.Commands;
using RotationSolver.IPC;
using RotationSolver.Updaters;

var checks = 0;
void Check(string name, bool actual)
{
    if (!actual) throw new Exception(name);
    checks++;
}
void ResetState()
{
    RSCommands.SetStateCommandType(StateCommandType.Off);
    Service.Config.TargetingIndex = 0;
    Service.Config.ToggleAuto = Service.Config.ToggleManual = false;
    DataCenter.IsPvP = Service.Config.PvpStateControl = DataCenter.PvPAutomationBlocked = false;
}
var commands = Svc.Commands;
RSCommands.Disable();
Check("first startup Disable does not remove unregistered commands", commands.Removes == 0 && commands.MissingRemoves == 0);
RSCommands.Enable();
Check("only root aliases registered", commands.Commands.Keys.Order().SequenceEqual(new[] { "/rotation", "/rsr" }.Order()));
Check("macro argument strings never registered as command names", commands.Commands.Keys.All(name => !name.Any(char.IsWhiteSpace)));
Check("both aliases visible in help", commands.Commands.Values.All(c => c.ShowInHelp && c.HelpMessage.StartsWith("en")));
TextExtensions.Language = "ja";
RSCommands.Enable();
Check("re-enable refreshes help", commands.Commands.Values.All(c => c.HelpMessage.StartsWith("ja")));
Check("re-enable produces no duplicate or missing errors", commands.DuplicateAdds == 0 && commands.MissingRemoves == 0);

foreach (var alias in new[] { Service.COMMAND, Service.ALTCOMMAND })
{
    ResetState();
    var opens = RotationSolverPlugin.OpenCount;
    commands.Dispatch(alias);
    Check(alias + " empty opens config", RotationSolverPlugin.OpenCount == opens + 1 && !DataCenter.State);
    commands.Dispatch(alias + "   ");
    Check(alias + " whitespace opens config", RotationSolverPlugin.OpenCount == opens + 2);
    commands.Dispatch(alias + " aUtO");
    Check(alias + " mixed-case Auto enters production Auto state", DataCenter.State && !DataCenter.IsManual && !DataCenter.IsTargetOnly && !DataCenter.IsAutoDuty);
    Check(alias + " Auto resets cancel timer", ActionUpdater.AutoCancelTime == DateTime.MinValue);
    ActionUpdater.NextAction = new object(); ActionUpdater.NextGCDAction = new object();
    Service.CanMove = false;
    var resets = DataCenter.Resets; var releases = Wrath_IPCSubscriber.Releases;
    commands.Dispatch(alias + " oFf");
    Check(alias + " Off clears state", !DataCenter.State && !DataCenter.IsManual && !DataCenter.IsPvPStateEnabled);
    Check(alias + " Off clears queued actions", ActionUpdater.NextAction is null && ActionUpdater.NextGCDAction is null);
    Check(alias + " Off resets records/releases IPC/movement", DataCenter.Resets == resets + 1 && Wrath_IPCSubscriber.Releases == releases + 1 && Service.CanMove);
    commands.Dispatch(alias + " Auto");
    commands.Dispatch(alias + " CaNcEl");
    Check(alias + " cancel alias stops production state", !DataCenter.State);
    commands.Dispatch(alias + " Manual");
    Check(alias + " Manual state", DataCenter.State && DataCenter.IsManual);
    commands.Dispatch(alias + " Auto lowhp");
    Check(alias + " named targeting resolves configured index", DataCenter.State && !DataCenter.IsManual && Service.Config.TargetingIndex == 1);
    commands.Dispatch(alias + " Auto");
    Check(alias + " repeated Auto cycles targeting", Service.Config.TargetingIndex == 2);
    commands.Dispatch(alias + " Auto 1");
    Check(alias + " numeric targeting selects index", Service.Config.TargetingIndex == 1);
    commands.Dispatch(alias + " Auto 4");
    Check(alias + " numeric targeting wraps index", Service.Config.TargetingIndex == 1);
    commands.Dispatch(alias + " Off");
    var errors = Svc.Chat.Errors.Count;
    commands.Dispatch(alias + " Auto Farthest");
    Check(alias + " unconfigured named targeting rejects activation", !DataCenter.State && Svc.Chat.Errors.Count == errors + 1);
    commands.Dispatch(alias + " Auto nearest");
    Check(alias + " named targeting from Off", DataCenter.State && Service.Config.TargetingIndex == 2);
    commands.Dispatch(alias + " TargetOnly");
    Check(alias + " TargetOnly state", DataCenter.State && DataCenter.IsTargetOnly && !DataCenter.IsManual);
    commands.Dispatch(alias + " Burst");
    Check(alias + " special dispatcher executes production special handler", DataCenter.SpecialType == SpecialCommandType.Burst && RSCommands._specialString == "Burst");
    commands.Dispatch(alias + " sEtTiNgS  AutoBurst false ");
    Check(alias + " other dispatcher preserves trimmed payload", RSCommands.LastOther == (OtherCommandType.Settings, "AutoBurst false"));
    var beforeUnknown = RotationSolverPlugin.OpenCount;
    commands.Dispatch(alias + " UnknownCommand");
    Check(alias + " unknown opens config", RotationSolverPlugin.OpenCount == beforeUnknown + 1);
    commands.Dispatch(alias + " Off");
    Player.Object = null;
    commands.Dispatch(alias + " Auto");
    Check(alias + " no logged-in player cannot activate", !DataCenter.State);
    Player.Object = new();
}

ResetState();
commands.Dispatch(Service.AUTOCOMMAND);
Check("existing Auto macro payload routes through root alias", DataCenter.State && !DataCenter.IsManual);
commands.Dispatch(Service.OFFCOMMAND);
Check("existing Off macro payload routes through root alias", !DataCenter.State);
RSCommands.Disable();
Check("disable removes owned aliases", commands.Commands.Count == 0);
var removes = commands.Removes;
RSCommands.Disable();
Check("repeated disable performs no missing removes", commands.Removes == removes && commands.MissingRemoves == 0);
var foreignCalls = 0;
var foreign = new Dalamud.Game.Command.CommandInfo((_, _) => foreignCalls++);
commands.AddHandler(Service.ALTCOMMAND, foreign);
RSCommands.Enable();
Check("foreign alias collision leaves existing handler", ReferenceEquals(commands.Commands[Service.ALTCOMMAND], foreign));
commands.Dispatch(Service.ALTCOMMAND);
Check("foreign handler still functional", foreignCalls == 1);
RSCommands.Disable();
Check("disable leaves foreign handler owned by other plugin", commands.Commands.Count == 1 && ReferenceEquals(commands.Commands[Service.ALTCOMMAND], foreign));
Check("collision cleanup never removes absent handler", commands.MissingRemoves == 0);
commands.RemoveHandler(Service.ALTCOMMAND);
RSCommands.Enable();
Check("reload reacquires free aliases", commands.Commands.Count == 2);
commands.Dispatch(Service.COMMAND + " Auto");
Check("freshly reloaded command actually activates", DataCenter.State);
RSCommands.Disable();
Console.WriteLine($"PASS {checks} actual command-handler checks (full production registration, dispatcher and state transitions; external services faked)");
