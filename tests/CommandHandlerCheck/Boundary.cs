global using System.ComponentModel;
global using RotationSolver.Basic.Data;
global using RotationSolver.Data;
global using RotationSolver;

namespace Dalamud.Game.Command
{
    public sealed class CommandInfo(Action<string, string> handler)
    {
        public Action<string, string> Handler { get; } = handler;
        public string HelpMessage { get; set; } = "";
        public bool ShowInHelp { get; set; }
    }
}
namespace Dalamud.Game.Config { public enum UiControlOption { AutoFaceTargetOnAction } }
namespace Dalamud.Game.Gui.Toast { public sealed class QuestToastOptions { public int IconId { get; set; } } }
namespace ECommons.GameHelpers
{
    public static class Player { public static PlayerObject? Object { get; set; } = new(); }
    public sealed class PlayerObject { public JobRef ClassJob { get; } = new(); }
    public sealed class JobRef { public Job Value { get; } = new(); }
    public sealed class Job { public JobRole GetJobRole() => JobRole.Ranged; }
}
namespace ECommons.DalamudServices
{
    public static class Svc
    {
        public static FakeCommands Commands { get; } = new();
        public static FakeChat Chat { get; } = new();
        public static FakeToasts Toasts { get; } = new();
        public static FakeGameConfig GameConfig { get; } = new();
    }
    public sealed class FakeCommands
    {
        public Dictionary<string, Dalamud.Game.Command.CommandInfo> Commands { get; } = [];
        public int DuplicateAdds, MissingRemoves, Adds, Removes;
        public bool AddHandler(string name, Dalamud.Game.Command.CommandInfo info)
        {
            Adds++;
            if (!Commands.TryAdd(name, info)) { DuplicateAdds++; return false; }
            return true;
        }
        public bool RemoveHandler(string name)
        {
            Removes++;
            if (Commands.Remove(name)) return true;
            MissingRemoves++; return false;
        }
        public void Dispatch(string input)
        {
            var split = input.IndexOf(' ');
            var name = split < 0 ? input : input[..split];
            Commands[name].Handler(name, split < 0 ? "" : input[(split + 1)..]);
        }
    }
    public sealed class FakeChat
    {
        public List<string> Messages { get; } = [];
        public List<string> Errors { get; } = [];
        public void Print(string message) => Messages.Add(message);
        public void PrintError(string message) => Errors.Add(message);
    }
    public sealed class FakeToasts { public int Count; public void ShowQuest(string text, Dalamud.Game.Gui.Toast.QuestToastOptions options) => Count++; }
    public sealed class FakeGameConfig { public FakeUiControl UiControl { get; } = new(); }
    public sealed class FakeUiControl { public int Writes; public void Set(string key, int value) => Writes++; }
}
namespace RotationSolver
{
    public static class Service
    {
        public const string COMMAND = "/rotation", ALTCOMMAND = "/rsr", AUTOCOMMAND = "/rotation Auto", OFFCOMMAND = "/rotation Off";
        public static FakeConfig Config { get; } = new();
        public static bool CanMove;
    }
    public sealed class FakeConfig
    {
        public List<TargetingType> TargetingTypes { get; } = [TargetingType.Big, TargetingType.LowHP, TargetingType.Nearest];
        public int TargetingIndex;
        public bool ShowToggledSettingInChat, ShowInfoOnToast, PvpStateControl, ToggleManual, ToggleAuto;
    }
    public static class RotationSolverPlugin { public static int OpenCount; public static void OpenConfigWindow() => OpenCount++; }
    public enum UiString { Commands_Rotation, Commands_Start, Commands_Off }
    public static class TextExtensions
    {
        public static string Language = "en";
        public static string GetDescription(this UiString value) => Language + value;
        public static string ToStateString(this StateCommandType value, JobRole role) => value.ToString();
        public static string ToSpecialString(this SpecialCommandType value, JobRole role) => value.ToString();
    }
}
namespace RotationSolver.Data
{
    public enum JobRole { None, Ranged }
    public static class DataCenter
    {
        public static Action<SpecialCommandType>? OnSpecialTypeChanged;
        public static bool State, IsPvP, IsPvPStateEnabled, IsManual, IsTargetOnly, IsAutoDuty, IsHenched, PvPAutomationBlocked;
        public static bool Replay, AutoFace = true;
        public static int MoveMode, Resets;
        public static float SpecialTimeLeft = -1;
        public static TargetingType? TargetingTypeOverride;
        public static TargetingType TargetingType => TargetingTypeOverride ?? Service.Config.TargetingTypes[Service.Config.TargetingIndex];
        public static RotationSolver.IPC.IpcStateOverrides? IpcOverrides;
        public static SpecialCommandType SpecialType;
        public static bool IsInDutyReplay() => Replay;
        public static bool AutoFaceTargetOnActionSetting() => AutoFace;
        public static int MoveModeSetting() => MoveMode;
        public static void ResetAllRecords() => Resets++;
    }
}
namespace RotationSolver.IPC
{
    public sealed class IpcStateOverrides { }
    public static class Wrath_IPCSubscriber { public static int Releases; public static void Release() => Releases++; }
}
namespace RotationSolver.Updaters
{
    public static class ActionUpdater { public static object? NextAction, NextGCDAction; public static DateTime AutoCancelTime; }
}
namespace RotationSolver.Commands
{
    public static partial class RSCommands
    {
        public static (OtherCommandType Type, string Extra)? LastOther;
        private static void DoOtherCommand(OtherCommandType type, string extra) => LastOther = (type, extra);
    }
}
