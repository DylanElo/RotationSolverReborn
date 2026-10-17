// The slice of RotationSolver.Basic and Dalamud that the compiled rotation files touch, backed by the sim's game state
// (Game). Decision logic that lives in RotationSolver.Basic (CanUse, status timing, cooldown checks) is copied here
// from the real source, with the line it mirrors noted; everything that would ask the game asks Game instead.
using System.Numerics;
using FightSim;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Rotations;

namespace Dalamud.Game.ClientState.JobGauge.Enums
{
	public enum Song : byte { None = 0, MagesBallad = 1, ArmysPaeon = 2, WanderersMinuet = 3 }
}

namespace Dalamud.Interface.Colors
{
	public static class ImGuiColors { public static Vector4 DalamudViolet; }
}

namespace Dalamud.Bindings.ImGui
{
	public static class ImGui
	{
		public static void Text(string _) { }
		public static void TextColored(Vector4 _, string __) { }
		public static void Spacing() { }
	}
}

namespace Dalamud.Interface { }

namespace ECommons.DalamudServices
{
	/// <summary>Dalamud service boundaries: logging and current object-table lookup.</summary>
	public static class Svc
	{
		public static class Log { public static void Info(string _) { } }
		public static ObjectTable Objects { get; } = new();
		public sealed class ObjectTable : IEnumerable<Dalamud.Game.ClientState.Objects.Types.IBattleChara>
		{
			public Dalamud.Game.ClientState.Objects.Types.IBattleChara? SearchById(ulong id) => Game.FindObject(id);
			public IEnumerator<Dalamud.Game.ClientState.Objects.Types.IBattleChara> GetEnumerator() => Game.CurrentObjects.GetEnumerator();
			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
		}
	}
}

namespace Dalamud.Game.ClientState.Objects.Types
{
	public sealed class StatusEntry
	{
		public uint StatusId;
		public float RemainingTime;
	}

	/// <summary>An actor as the rotation sees it: the player or an enemy from the timeline.</summary>
	public class IBattleChara
	{
		public ulong GameObjectId { get; init; }
		public uint BaseId { get; init; }
		public string Name { get; init; } = "";
		public bool IsCasting => CastActionId != 0;
		public uint CastActionId { get; set; }
		public float TotalCastTime { get; set; }
		public float CurrentCastTime { get; set; }
		/// <summary>Remaining seconds per status (own statuses only: the sim tracks no one else's on enemies).</summary>
		public Dictionary<RotationSolver.Basic.Data.StatusID, float> Statuses { get; } = [];
		public Dictionary<RotationSolver.Basic.Data.StatusID, byte> Stacks { get; } = [];
		public bool IsTargetable => GameObjectId == 1 || FightSim.Game.Hostiles.Contains(this);
		public bool IsDead => GameObjectId == 1 && FightSim.Game.Timeline.PlayerDebuffs.Any(d => d.Name == "Dead" && FightSim.Game.Now >= d.Start && FightSim.Game.Now < d.End);
		public Func<float> HealthRatio { get; init; } = () => 1f;
		public IEnumerable<StatusEntry> StatusList => Statuses.Where(s => s.Value > 0).Select(s => new StatusEntry { StatusId = (uint)s.Key, RemainingTime = s.Value });
	}
}

namespace RotationSolver.Basic.Attributes
{
	public enum ConfigUnitType { None, Seconds, Degree, Yalms, Percent, Pixels }

	[AttributeUsage(AttributeTargets.Class)]
	public sealed class RotationAttribute(string name, CombatType type) : Attribute
	{
		public string Name { get; } = name;
		public CombatType Type { get; } = type;
		public string GameVersion { get; set; } = "";
		public string Description { get; set; } = "";
	}

	[AttributeUsage(AttributeTargets.Class)]
	public sealed class SourceCodeAttribute : Attribute { public string Path { get; set; } = ""; }

	[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
	public sealed class RotationDescAttribute(params ActionID[] _) : Attribute { }

	[AttributeUsage(AttributeTargets.Property)]
	public sealed class RangeAttribute(float min, float max, ConfigUnitType unit, float speed = 0.005f) : Attribute { }
}

namespace RotationSolver.Basic.Configuration.RotationConfig
{
	[AttributeUsage(AttributeTargets.Property)]
	public sealed class RotationConfigAttribute(CombatType type) : Attribute
	{
		public string Name { get; set; } = "";
		public string Section { get; set; } = "";
		public int Order { get; set; }
		public string Tooltip { get; set; } = "";
		public string Parent { get; set; } = "";
	}
}

namespace RotationSolver.Basic.Data
{
	public enum CombatType : byte { None, PvP, PvE, Both }
	public enum TerritoryContentType : byte { None, Raids, Dungeons, Trials, UltimateRaids }
	public sealed record TerritoryInfo(TerritoryContentType ContentType);
	public enum MedicineType : byte { None, Strength, Dexterity, Intelligence, Mind }
	public enum TargetType : byte { Big, Small, HighHP, LowHP, HighMaxHP, LowMaxHP, Interrupt, Provoke, Death, Dispel, Move, Heal, BeAttacked, Self }
	public enum SpecialActionType : byte { None, MeleeRange, MovingForward, MovingBackward, FixedDistanceMoveBackward, HostileMovingAttack, MeleeRangedAttack }

	public readonly record struct BMRPlannedAction(string ModuleType, string TrackName, string OptionName, uint ActionType, uint ActionId,
		float ActivationIn, float WindowEndIn, int Target, int TargetParam);
}

namespace RotationSolver.Basic.Actions
{
	using Dalamud.Game.ClientState.Objects.Types;
	using RotationSolver.Basic.Helpers;

	public interface IAction
	{
		uint ID { get; }
		uint AdjustedID { get; }
	}

	public interface ICooldown { }

	public interface IBaseTrait { bool EnoughLevel { get; } }

	public sealed class BaseTrait : IBaseTrait { public bool EnoughLevel => true; }

	public sealed class ActionSetting
	{
		public StatusID[]? StatusProvide { get; set; }
		public StatusID[]? StatusNeed { get; set; }
		public StatusID[]? TargetStatusProvide { get; set; }
		public StatusID[]? TargetStatusNeed { get; set; }
		public bool StatusFromSelf { get; set; } = true;
		public bool IsFriendly { get; set; }
		public uint UnlockedByQuestID { get; set; }
		public SpecialActionType SpecialType { get; set; }
		public ActionID[]? ComboIds { get; set; }
		public ActionID[]? ComboIdsNot { get; set; }
		public bool IgnoreGuard { get; set; }
		public TargetType TargetType { get; set; }
		public Func<bool>? ActionCheck { get; set; }
		public Func<bool>? RotationCheck { get; set; }
		public Func<IBattleChara, bool> CanTarget { get; set; } = _ => true; // ActionSetting default
		public Func<uint>? MPOverride { get; set; }
		public Func<ActionConfig>? CreateConfig { get; set; }
	}

	public sealed class ActionConfig
	{
		public bool IsEnabled { get; set; } = true;
		public bool ShouldCheckStatus { get; set; } = true;
		public bool ShouldCheckTargetStatus { get; set; } = true;
		public bool ShouldCheckCombo { get; set; } = true;
		public byte StatusRefreshGcdCount { get; set; } = 2;
		public byte AoeCount { get; set; } = 3;
		public float TimeToKill { get; set; }
		public bool IsRestrictedDOT { get; set; }
	}

	public struct TargetResult
	{
		public IBattleChara? Target;
		public IBattleChara[] AffectedTargets;
	}

	/// <summary>Per-action game facts the sim needs; Game.Info fills them from the job model.</summary>
	public readonly struct ActionBasicInfo(BaseAction action)
	{
		public bool EnoughLevel => Game.Job.Spec(action.ActionId).Level <= Game.Level;
		public bool IsRealGCD => Game.Job.Spec(action.ActionId).IsGcd;
		public bool IsGeneralGCD => IsRealGCD && Game.Job.Spec(action.ActionId).Recast == 0;
		public uint AdjustedID => (uint)Game.Job.Adjusted(action.ActionId);
		public bool EnoughLevelAndQuest() => EnoughLevel;
	}

	public readonly struct ActionCooldownInfo(BaseAction action)
	{
		// Native CoolDownGroup is fixed from the base action; adjusted IDs supply charge/action facts.
		private Cooldown Cd => Game.Cooldown(action.ActionId);
		private Cooldown AdjustedCd => Game.Cooldown(Game.Job.Adjusted(action.ActionId));
		public float RecastTime => Cd.Total;
		internal float RecastTimeElapsedRaw => Cd.Elapsed;
		public float RecastTimeElapsed => RecastTimeElapsedRaw - DataCenter.DefaultGCDRemain;
		public bool IsCoolingDown => AdjustedCd.IsCoolingDown;
		public float RecastTimeRemain => RecastTime - RecastTimeElapsedRaw;
		public float RecastTimeOneChargeRaw => AdjustedCd.OneCharge;
		public bool HasOneCharge => !IsCoolingDown || RecastTimeElapsedRaw >= RecastTimeOneChargeRaw;
		public ushort CurrentCharges => (ushort)AdjustedCd.Charges;
		public ushort MaxCharges => (ushort)Math.Max(AdjustedCd.MaxCharges, 1);
		internal float RecastTimeRemainOneChargeRaw => RecastTimeOneChargeRaw <= 0 ? 0 : RecastTimeRemain % RecastTimeOneChargeRaw;
		public float RecastTimeRemainOneCharge => RecastTimeRemainOneChargeRaw - DataCenter.DefaultGCDRemain;
		public float RecastTimeElapsedOneCharge => (RecastTimeOneChargeRaw <= 0 ? 0 : RecastTimeElapsedRaw % RecastTimeOneChargeRaw) - DataCenter.DefaultGCDElapsed;
		public bool ElapsedOneChargeAfterGCD(uint gcdCount = 0, float offset = 0) => ElapsedOneChargeAfter(DataCenter.GCDTime(gcdCount, offset));
		public bool ElapsedOneChargeAfter(float time) => IsCoolingDown && time <= RecastTimeElapsedOneCharge;
		public bool ElapsedAfterGCD(uint gcdCount = 0, float offset = 0) => ElapsedAfter(DataCenter.GCDTime(gcdCount, offset));
		public bool ElapsedAfter(float time) => IsCoolingDown && time <= RecastTimeElapsed;
		public bool WillHaveOneChargeGCD(uint gcdCount = 0, float offset = 0) => WillHaveOneCharge(DataCenter.GCDTime(gcdCount, offset));
		public bool WillHaveOneCharge(float remain) => HasOneCharge || RecastTimeRemainOneCharge <= remain;
		public bool WillHaveXChargesGCD(uint charges, uint gcdCount = 0, float offset = 0) => WillHaveXCharges(charges, DataCenter.GCDTime(gcdCount, offset));
		public bool WillHaveXCharges(uint charges, float remain)
		{
			if (charges <= CurrentCharges) return true;
			var requiredTime = (charges - CurrentCharges - 1) * RecastTimeOneChargeRaw;
			return RecastTimeRemainOneCharge <= remain - requiredTime;
		}

		// ActionCooldownInfo.CooldownCheck
		internal bool CooldownCheck(bool isEmpty, byte gcdCountForAbility, ActionBasicInfo info)
		{
			if (!info.IsGeneralGCD)
			{
				if (IsCoolingDown)
				{
					if (info.IsRealGCD)
					{
						if (!WillHaveOneChargeGCD(0, 0)) return false;
					}
					else if (!HasOneCharge && RecastTimeRemainOneChargeRaw > DataCenter.DefaultGCDRemain)
					{
						return false;
					}
				}
				if (!isEmpty && RecastTimeRemain > DataCenter.DefaultGCDRemain + (DataCenter.DefaultGCDTotal * gcdCountForAbility)) return false;
			}
			if (!info.IsRealGCD && (Game.AnimationLock > 0f || !HasOneCharge)) return false;
			return true;
		}
	}

	public interface IBaseAction : IAction
	{
		ActionSetting Setting { get; }
		ActionConfig Config { get; }
		ActionBasicInfo Info { get; }
		ActionCooldownInfo Cooldown { get; }
		TargetResult Target { get; }
		bool EnoughLevel { get; }
		bool IsEnabled { get; }
		bool CanUse(out IAction act, bool skipStatusProvideCheck = false, bool skipStatusNeed = false, bool skipTargetStatusNeedCheck = false,
			bool skipComboCheck = false, bool skipCastingCheck = false, bool usedUp = false, bool skipAoeCheck = false, bool skipTTKCheck = false,
			byte gcdCountForAbility = 0, bool checkActionManagerDirectly = false, TargetType targetOverride = default);
	}

	public delegate void SettingModifier(ref ActionSetting setting);

	public sealed class BaseAction : IBaseAction
	{
		public ActionID ActionId { get; }
		public uint ID => (uint)ActionId;
		public uint AdjustedID => Info.AdjustedID;
		public ActionSetting Setting { get; }
		public ActionConfig Config { get; }
		public ActionBasicInfo Info => new(this);
		public ActionCooldownInfo Cooldown => new(this);
		public TargetResult Target { get; private set; }
		public bool EnoughLevel => Info.EnoughLevel;
		public bool IsEnabled => Config.IsEnabled;
		public override string ToString() => ActionId.ToString();

		private BaseAction(ActionID id, ActionSetting setting)
		{
			ActionId = id;
			Setting = setting;
			Config = setting.CreateConfig?.Invoke() ?? new ActionConfig();
			if (setting.TargetStatusProvide != null) Config.TimeToKill = 0; // BaseAction.Config getter
		}

		public static BaseAction Create(ActionID id, SettingModifier modify)
		{
			var setting = new ActionSetting();
			modify(ref setting);
			return new BaseAction(id, setting);
		}

		// BaseAction.CanUse
		public bool CanUse(out IAction act, bool skipStatusProvideCheck = false, bool skipStatusNeed = false, bool skipTargetStatusNeedCheck = false,
			bool skipComboCheck = false, bool skipCastingCheck = false, bool usedUp = false, bool skipAoeCheck = false, bool skipTTKCheck = false,
			byte gcdCountForAbility = 0, bool checkActionManagerDirectly = false, TargetType targetOverride = default)
		{
			act = this;
			if (!BasicCheck(skipStatusProvideCheck, skipStatusNeed)) return false;
			if (!skipComboCheck && Info.IsGeneralGCD && !ComboCheck()) return false;
			if (!Cooldown.CooldownCheck(usedUp, gcdCountForAbility, Info)) return false;
			if (!skipTTKCheck && DataCenter.AverageTTK < Config.TimeToKill) return false;
			var target = FindTarget(skipAoeCheck, skipStatusProvideCheck, skipTargetStatusNeedCheck);
			if (target == null) return false;
			Target = target.Value;
			return true;
		}

		// ActionBasicInfo.BasicCheck, minus what the sim has no use for (PvP, MP, quests, bad statuses other than lockouts).
		private bool BasicCheck(bool skipStatusProvideCheck, bool skipStatusNeed)
		{
			if (!Info.EnoughLevel || !Config.IsEnabled || Game.Locked) return false;
			if (!Game.Job.GameAllows(Game.Job.Adjusted(ActionId))) return false; // ActionManager.GetActionStatus
			var player = Game.Player;
			if (!skipStatusNeed && Setting.StatusNeed != null && player.WillStatusEndGCD(Config.StatusRefreshGcdCount, 0, Setting.StatusFromSelf, Setting.StatusNeed)) return false;
			if (!skipStatusProvideCheck && Setting.StatusProvide != null && !player.WillStatusEndGCD(Config.StatusRefreshGcdCount, 0, Setting.StatusFromSelf, Setting.StatusProvide)) return false;
			if (Info.IsGeneralGCD && Setting.StatusProvide?.Length > 0 && Setting.IsFriendly && CustomRotation.IsLastGCD(true, this) && Game.TimeSinceLastAction < 3) return false;
			return (Setting.ActionCheck?.Invoke() ?? true) && (Setting.RotationCheck?.Invoke() ?? true);
		}

		// ActionBasicInfo.CheckForCombo, with the combo actions from ComboIds (the sheet's ActionCombo is not modelled).
		private bool ComboCheck()
		{
			if (!Config.ShouldCheckCombo) return true;
			if (Setting.ComboIdsNot?.Contains(DataCenter.LastComboAction) == true) return false;
			if (Setting.ComboIds is not { Length: > 0 } combo) return true;
			return combo.Contains(DataCenter.LastComboAction) && DataCenter.ComboTime >= DataCenter.DefaultGCDRemain;
		}

		// ActionTargetInfo.FindTarget, for what a ranged DPS uses: self, the current target, or the AoE around it.
		private TargetResult? FindTarget(bool skipAoeCheck, bool skipStatusProvideCheck, bool skipTargetStatusNeedCheck)
		{
			var spec = Game.Job.Spec(Game.Job.Adjusted(ActionId));
			if (Setting.IsFriendly || Setting.TargetType == TargetType.Self || spec.Shape == AoeShape.Self)
			{
				return new TargetResult { Target = Game.Player, AffectedTargets = [] };
			}
			var target = DataCenter.CurrentTarget;
			if (target == null || !Game.InRange(target)) return null;
			if (Setting.CanTarget != null && !Setting.CanTarget(target)) return null;
			if (!CheckStatus(target, skipStatusProvideCheck, skipTargetStatusNeedCheck)) return null;
			var affected = Game.AffectedBy(spec, target);
			if (spec.Shape != AoeShape.Single && !skipAoeCheck && affected.Length < Config.AoeCount) return null;
			return new TargetResult { Target = target, AffectedTargets = affected };
		}

		// ActionTargetInfo.CheckStatus
		private bool CheckStatus(IBattleChara chara, bool skipStatusProvideCheck, bool skipTargetStatusNeedCheck)
		{
			if (!Config.ShouldCheckTargetStatus && !Config.ShouldCheckStatus) return true;
			if (Setting.TargetStatusNeed != null && !skipTargetStatusNeedCheck
				&& chara.WillStatusEndGCD(Config.StatusRefreshGcdCount, 0, Setting.StatusFromSelf, Setting.TargetStatusNeed)) return false;
			if (Setting.TargetStatusProvide != null && !skipStatusProvideCheck
				&& !chara.WillStatusEndGCD(Config.StatusRefreshGcdCount, 0, Setting.StatusFromSelf, Setting.TargetStatusProvide)) return false;
			return true;
		}
	}

	/// <summary>The tincture: RSR's MedicineItem, on the sim's potion cooldown.</summary>
	public sealed class MedicineItem : IAction
	{
		public static readonly MedicineItem Instance = new();
		public uint ID => 0;
		public uint AdjustedID => 0;
		public override string ToString() => "Potion";
	}
}

namespace RotationSolver.Basic.Helpers
{
	using Dalamud.Game.ClientState.Objects.Types;
	using RotationSolver.Basic.Actions;
	public static class IActionHelper
	{
		public static bool IsLastActionGCD() => Game.LastAction == Game.LastGcd;
	}

	public static class StatusHelper
	{
		public static StatusID[] RangePhysicalDefense { get; } = [];
		public static StatusID[] PurifyPvPStatuses { get; } = [];

		// StatusHelper: raw remaining time of the earliest matching status, 0 when none.
		public static float StatusTime(this IBattleChara chara, bool isFromSelf, params ReadOnlySpan<StatusID> ids)
		{
			var found = false;
			var min = float.MaxValue;
			foreach (var id in ids)
			{
				if (chara.Statuses.TryGetValue(id, out var t) && t > 0) { found = true; min = Math.Min(min, t); }
			}
			return found ? min : 0f;
		}

		public static bool HasStatus(this IBattleChara chara, bool isFromSelf, params ReadOnlySpan<StatusID> ids)
		{
			foreach (var id in ids)
			{
				if (chara.Statuses.TryGetValue(id, out var t) && t > 0) return true;
			}
			return false;
		}

		public static bool WillStatusEnd(this IBattleChara chara, float time, bool isFromSelf = true, params ReadOnlySpan<StatusID> ids)
		{
			var statusTime = chara.StatusTime(isFromSelf, ids);
			return (statusTime >= 0f || !chara.HasStatus(isFromSelf, ids)) && statusTime <= time;
		}

		public static bool WillStatusEndGCD(this IBattleChara chara, uint gcdCount = 0, float offset = 0, bool isFromSelf = true, params ReadOnlySpan<StatusID> ids)
			=> chara.WillStatusEnd(DataCenter.GCDTime(gcdCount, offset), isFromSelf, ids);

		public static bool PlayerHasStatus(bool isFromSelf, params ReadOnlySpan<StatusID> ids) => Game.Player.HasStatus(isFromSelf, ids);
		public static float PlayerStatusTime(bool isFromSelf, params ReadOnlySpan<StatusID> ids) => Game.Player.StatusTime(isFromSelf, ids);
		public static bool PlayerWillStatusEnd(float time, bool isFromSelf = true, params ReadOnlySpan<StatusID> ids) => Game.Player.WillStatusEnd(time, isFromSelf, ids);
		public static bool PlayerWillStatusEndGCD(uint gcdCount = 0, float offset = 0, bool isFromSelf = true, params ReadOnlySpan<StatusID> ids)
			=> Game.Player.WillStatusEndGCD(gcdCount, offset, isFromSelf, ids);

		public static float GetHealthRatio(this IBattleChara chara) => chara.HealthRatio();
		public static bool IsBossFromIcon(this IBattleChara chara) => true;
		// ObjectHelper.IsDying with the user's DyingTimeToKill 10 and IsDyingConfig 0.02.
		public static bool IsDying(this IBattleChara chara) => chara != null && (chara.GetTTK() <= 10f || chara.GetHealthRatio() < 0.02f);

		public static byte StatusStack(this IBattleChara chara, bool isFromSelf, params ReadOnlySpan<StatusID> ids)
		{
			byte min = byte.MaxValue;
			var found = false;
			foreach (var id in ids)
			{
				if (chara.HasStatus(isFromSelf, id)) { found = true; min = Math.Min(min, chara.Stacks.GetValueOrDefault(id)); }
			}
			return found ? min : (byte)0;
		}

		public static byte PlayerStatusStack(bool isFromSelf, params ReadOnlySpan<StatusID> ids) => Game.Player.StatusStack(isFromSelf, ids);

		/// <summary>ObjectHelper.GetTTK: seconds to kill from the HP lost since first seen, NaN without data.</summary>
		public static float GetTTK(this IBattleChara chara) => Game.TimeToKill(chara);
	}

	public static class ActionHelper
	{
		public static bool IsTheSameTo(this IAction action, bool isAdjust, params IAction[] actions)
		{
			foreach (var a in actions)
			{
				if (a.ID == action.ID || isAdjust && a.AdjustedID == action.AdjustedID) return true;
			}
			return false;
		}
	}

	/// <summary>RSR's verbose action tracer; the sim keeps it off.</summary>
	internal static class ActionTracer
	{
		public static bool Enabled => false;
		public static bool Verbose => false;
		// Rare state changes (always) land in --trace output between the casts.
		public static void Note(string text, bool always = false) { if (always) Game.Casts.Add((Game.Now, "NOTE " + text, null, 0)); }
	}
}

namespace RotationSolver.Basic
{
	using Dalamud.Game.ClientState.Objects.Types;

	internal static class DataCenter
	{
		public static bool IsInFRU => Game.IsInFRU;
		public static float CombatTimeRaw => Game.Now;
		public static float DefaultGCDTotal => Game.GcdTotal;
		public static float DefaultGCDRemain => Game.GcdRemain;
		public static float DefaultGCDElapsed => Math.Max(0f, DefaultGCDTotal - DefaultGCDRemain);
		public static float GCDTime(uint gcdCount = 0, float offset = 0) => (DefaultGCDTotal * gcdCount) + offset;
		public static float AverageTTK => Game.AverageTTK;
		public static IBattleChara? CurrentTarget => Game.CurrentTargetOverride ?? Game.Target;
		public static List<IBattleChara> AllHostileTargets => Game.Hostiles;
		public static IEnumerable<IBattleChara> AllTargets => Game.CurrentObjects.Where(c => c.IsTargetable);
		public static bool HasHostilesInRange => Game.Hostiles.Any(Game.InRange);
		public static bool HasHostilesInMaxRange => HasHostilesInRange;
		public static float BMRNextDowntimeIn => Game.DowntimeIn;
		public static float BMRNextDowntimeEndIn => Game.DowntimeEndIn;
		// BossMod's FRU module is loaded; it publishes no vulnerability windows.
		public static bool BMRHasActiveModule => true;
		public static string? BMRActiveModuleName => "FRU";
		public static float BMRNextVulnerableIn => float.MaxValue;
		public static float BMRNextVulnerableEndIn => float.MaxValue;
		public static bool BMRDebugTimelineDowntimeFunc => true;
		public static bool BMRDebugTimelineVulnFunc => true;
		public static bool BMRDebugTimelineVulnEndFunc => true;
		public static IBattleChara? HostileTarget => Game.Target;
		public static int NumberOfAllHostilesInMaxRange => Game.Hostiles.Count;
		public static bool IsMoving => false; // ponytail: movement is not in the timeline
		public static bool IsExtremeCastingSpecialIndicator() => false; // M10S only
		public static RotationSolver.Basic.Data.TerritoryInfo? Territory => new(RotationSolver.Basic.Data.TerritoryContentType.UltimateRaids);
		public static uint TerritoryID => Game.IsInFRU ? 1238u : 0u;
		public static float CalculatedActionAhead => Math.Max(0f, DefaultGCDTotal * Game.ActionAhead); // the user's Action6Head
		public static ActionID LastComboAction => (Game.Job as FightSim.Machinist)?.LastComboAction ?? ActionID.None;
		public static float ComboTime => (Game.Job as FightSim.Machinist)?.ComboTime ?? 0f;
		public static IEnumerable<(RotationSolver.RebornRotations.DateTime Time, Dictionary<ulong, float>? Hps)> RecordedHP => Game.RecordedHP;
	}

	internal static class Service
	{
		public static ActionID GetAdjustedActionId(ActionID id) => Game.Job.Adjusted(id);
	}
}

namespace RotationSolver.IPC
{
	using RotationSolver.Basic.Data;

	/// <summary>BossMod's plan, from the timeline's plan windows (RotationSolver/IPC/BMRPlan_IPCSubscriber.cs).</summary>
	internal static class BMRPlan_IPCSubscriber
	{
		public static bool IsEnabled => Game.Timeline.PlanWindows.Count > 0;
		public static int PlanVersion => 1;
		public static List<BMRPlannedAction> GetUpcomingPlannedActions(float lookAhead) => Game.PlannedActions(lookAhead);
	}
}

namespace RotationSolver.RebornRotations
{
	/// <summary>
	/// Shadows System.DateTime for the rotation files (they live under this namespace), so DateTime.Now is sim time.
	/// </summary>
	public readonly record struct DateTime(double Seconds) : IComparable<DateTime>
	{
		public static DateTime Now => new(Game.Now);
		public static DateTime MinValue => new(-1e9);
		public DateTime AddSeconds(double s) => new(Seconds + s);
		public static TimeSpan operator -(DateTime a, DateTime b) => TimeSpan.FromSeconds(a.Seconds - b.Seconds);
		public static bool operator <(DateTime a, DateTime b) => a.Seconds < b.Seconds;
		public static bool operator >(DateTime a, DateTime b) => a.Seconds > b.Seconds;
		public static bool operator <=(DateTime a, DateTime b) => a.Seconds <= b.Seconds;
		public static bool operator >=(DateTime a, DateTime b) => a.Seconds >= b.Seconds;
		public int CompareTo(DateTime other) => Seconds.CompareTo(other.Seconds);
	}
}

namespace RotationSolver.Basic.Rotations.Basic
{
	public partial class BardRotation
	{
		internal static FightSim.BardGauge JobGauge => FightSim.Bard.Gauge;
	}

	public partial class MachinistRotation
	{
		internal static FightSim.MachinistGauge JobGauge => FightSim.Machinist.Gauge;
	}
}

namespace RotationSolver.Basic.Rotations
{
	using Dalamud.Game.ClientState.Objects.Types;
	using RotationSolver.Basic.Actions;

	/// <summary>
	/// CustomRotation's statics and hooks the rotations use, and the parts of its dispatcher (CustomRotation_Invoke,
	/// _GCD, _Ability) that pick DPS actions: Emergency then General GCD; Emergency, Attack, then General ability.
	/// </summary>
	public abstract partial class CustomRotation
	{
		public enum StatusType { Buff, Debuff }

		public class StatusInfo(string name, string jobAbbr, StatusType type, params StatusID[] ids)
		{
			public StatusID[] Ids { get; } = ids;
			public string Name { get; } = name;
			public string JobAbbr { get; } = jobAbbr;
			public StatusType Type { get; } = type;
		}

		// CustomRotation_OtherInfo.JobBuffs, for the jobs FightSim models.
		private static readonly Dictionary<string, StatusInfo[]> JobBuffs = new()
		{
			["AST"] = [new("Divination", "AST", StatusType.Buff, StatusID.Divination)],
			["BRD"] = [new("Battle Voice", "BRD", StatusType.Buff, StatusID.BattleVoice), new("Radiant Finale", "BRD", StatusType.Buff, StatusID.RadiantFinale_2964, StatusID.RadiantFinale)],
			["MNK"] = [new("Brotherhood", "MNK", StatusType.Buff, StatusID.Brotherhood)],
			["NIN"] = [new("Mug", "NIN", StatusType.Debuff, StatusID.Mug), new("Dokumori", "NIN", StatusType.Debuff, StatusID.Dokumori, StatusID.Dokumori_4303)],
			["SCH"] = [new("Chain Stratagem", "SCH", StatusType.Debuff, StatusID.ChainStratagem, StatusID.ChainStratagem_1406)],
			["PCT"] = [new("Starry Muse", "PCT", StatusType.Buff, StatusID.StarryMuse)],
			["DNC"] = [new("Technical Finish", "DNC", StatusType.Buff, StatusID.TechnicalFinish)],
			["DRG"] = [new("Battle Litany", "DRG", StatusType.Buff, StatusID.BattleLitany)],
			["RPR"] = [new("Arcane Circle", "RPR", StatusType.Buff, StatusID.ArcaneCircle)],
			["RDM"] = [new("Embolden", "RDM", StatusType.Buff, StatusID.Embolden)],
			["SMN"] = [new("Searing Light", "SMN", StatusType.Buff, StatusID.SearingLight)],
		};

		// The party's JobBuffs (StatusList rebuilds them from the party; the sim's party is fixed).
		public static List<StatusInfo> Buffs => Game.PartyJobs.SelectMany(j => JobBuffs.GetValueOrDefault(j, [])).ToList();
		protected static int PartyBuffsVersion => 1;
		public static void StatusList() { }

		public static IBattleChara? CurrentTarget => Game.CurrentTargetOverride ?? Game.Target;
		public static IBattleChara Player => Game.Player;
		public static IEnumerable<IBattleChara> AllHostileTargets => Game.Hostiles;
		public static bool InCombat => Game.InCombat;
		public static bool IsBurst => Game.AutoBurst;
		protected static IBattleChara? HostileTarget => DataCenter.HostileTarget;
		public static bool IsDead => Game.Player.IsDead;
		public static bool IsMoving => DataCenter.IsMoving;
		public static int NumberOfAllHostilesInMaxRange => DataCenter.NumberOfAllHostilesInMaxRange;
		public static float LiveComboTime => DataCenter.ComboTime;
		public static bool IsLastComboAction(bool isAdjust, params IAction[] actions)
			=> DataCenter.LastComboAction is var last && last != ActionID.None && actions.Any(a => (uint)last == a.ID || isAdjust && (uint)last == a.AdjustedID);
		public static bool IsLastComboAction(params ActionID[] ids) => ids.Contains(DataCenter.LastComboAction);
		protected static float WeaponRemain => DataCenter.DefaultGCDRemain;
		protected static float WeaponTotal => DataCenter.DefaultGCDTotal;
		public static float LateWeaveWindow => WeaponTotal * 0.45f;
		public static bool EnoughWeaveTime => WeaponRemain < WeaponTotal && WeaponRemain > Math.Max(DataCenter.CalculatedActionAhead, Game.AnimationLock);
		public static bool CanWeave => EnoughWeaveTime && Game.AnimationLock <= 0f;
		public static bool CanLateWeave => WeaponRemain <= LateWeaveWindow && CanWeave;
		public static bool BMRActive => DataCenter.BMRHasActiveModule;
		public static float BMRVulnerableIn => DataCenter.BMRNextVulnerableIn;
		public static float BMRVulnerableEndIn => DataCenter.BMRNextVulnerableEndIn;
		public static bool BMRDowntimeWithin(float seconds) => BMRActive && BMRDowntimeIn is > 0f and < float.MaxValue && BMRDowntimeIn <= seconds;
		public static bool BMRVulnWithin(float seconds) => BMRActive && BMRVulnerableIn is > 0f and < float.MaxValue && BMRVulnerableIn <= seconds;
		public static float CombatTime => Game.Now;
		public static float BMRDowntimeIn => DataCenter.BMRNextDowntimeIn;
		public static float BMRDowntimeEndIn => DataCenter.BMRNextDowntimeEndIn;
		protected static IEnumerable<IBattleChara> AllTargets => DataCenter.AllTargets;
		public static bool HasHostilesInRange => DataCenter.HasHostilesInRange;
		public static bool HasHostilesInMaxRange => DataCenter.HasHostilesInMaxRange;
		public static float GCDTime(uint gcdCount = 0, float offset = 0) => DataCenter.GCDTime(gcdCount, offset);

		public static bool IsLastAction(bool isAdjust, params IAction[] actions) => Game.LastAction is { } a && a.IsTheSameTo(isAdjust, actions);
		public static bool IsLastGCD(bool isAdjust, params IAction[] actions) => Game.LastGcd is { } a && a.IsTheSameTo(isAdjust, actions);
		public static bool IsLastAbility(bool isAdjust, params IAction[] actions) => Game.LastAbility is { } a && a.IsTheSameTo(isAdjust, actions);
		public static bool IsLastAction(params ActionID[] ids) => Game.LastAction is { } a && ids.Any(i => (uint)i == a.ID || (uint)i == a.AdjustedID);
		public static bool IsLastAbility(params ActionID[] ids) => Game.LastAbility is { } a && ids.Any(i => (uint)i == a.ID || (uint)i == a.AdjustedID);

		public virtual MedicineType MedicineType => MedicineType.None;
		public virtual void DisplayBaseStatus() { }

		// CustomRotation_Items.UseBurstMedicine: a tincture of the job's main stat when its cooldown allows.
		public bool UseBurstMedicine(out IAction? act, bool clippingCheck = true)
		{
			act = Game.PotionReady && !Game.Locked && Game.AnimationLock <= 0 ? MedicineItem.Instance : null;
			return act != null;
		}

		protected virtual IAction? CountDownAction(float remainTime) => null;
		protected virtual bool EmergencyAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool DispelAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool HealSingleAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool DefenseAreaAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool DefenseSingleAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		public virtual void DisplayRotationStatus() { }
		protected virtual bool GeneralAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool AttackAbility(IAction nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool EmergencyGCD(IAction? nextGCD, out IAction? act) { act = null; return false; }
		protected virtual bool GeneralGCD(out IAction? act) { act = null; return false; }

		/// <summary>CustomRotation.GCD for a DPS: Emergency, then General.</summary>
		public IAction? NextGcd() => EmergencyGCD(null, out var act) ? act : GeneralGCD(out act) ? act : null;

		/// <summary>CustomRotation.Ability for a DPS with no heal/defense/interrupt calls: Emergency, Attack (hostiles in range), General.</summary>
		public IAction? NextAbility(IAction nextGcd)
		{
			if (Game.WeaponRemain is <= 0.5f and > 0f) return null; // CustomRotation_Ability: no weave in the GCD's last 0.5s
			if (EmergencyAbility(nextGcd, out var act)) return act;
			if (HasHostilesInRange && AttackAbility(nextGcd, out act)) return act;
			if (GeneralAbility(nextGcd, out act)) return act;
			return null;
		}
	}
}
