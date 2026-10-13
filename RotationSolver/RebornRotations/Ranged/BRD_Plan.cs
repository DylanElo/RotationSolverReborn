using RotationSolver.IPC;

namespace RotationSolver.RebornRotations.Ranged;

/// <summary>
/// BRD Reborn with the two-minute burst timed by an authored plan, like MCH Plan. Windows are entries on the "Burst" track of
/// BossMod Reborn's "Utility: BRD" planner module, anchored to boss states. BossMod reports them with no action, so nothing
/// auto-casts them; this rotation holds Battle Voice (and so Radiant Finale and Raging Strikes) until a window opens, and pots
/// only in potion windows. Without a plan for the fight it plays exactly like BRD Reborn.
/// </summary>
[Rotation("BRD Plan", CombatType.PvE, GameVersion = "7.55",
	Description = "BRD Reborn with burst windows read from your BossMod Reborn plan (Utility: BRD > Burst track). Without a plan it plays exactly like BRD Reborn.")]
[SourceCode(Path = "main/RebornRotations/Ranged/BRD_Plan.cs")]
public sealed class BRD_Plan : BRD_Reborn
{
	private const float PlanLookAhead = 150f;
	private const float PollInterval = 1f;

	/// <summary>Another job's raid buff this close to a planned start pulls the window in to it.</summary>
	internal const float WindowSlide = 8f;

	/// <summary>A window that closed before Battle Voice could go (no target, DoTs missing) still bursts, for this long. Same as MCH Plan.</summary>
	internal const float LateGrace = 30f;

	private List<BMRPlannedAction> _windows = [];
	private DateTime _polledAt = DateTime.MinValue;
	private float _lastCombatTime;
	private DateTime _windowEnd = DateTime.MinValue;
	private bool _windowPotion;

	/// <summary>
	/// A Burst window was seen this pull, so the plan is active. BossMod cannot see past a kill-gated phase end: from the last P4
	/// window until P5 starts it reports nothing, and treating that as "no plan" burst on cooldown at ~757s and missed the P5 window.
	/// </summary>
	private bool _planSeen;

	/// <summary>Planned Burst entries, refreshed once a second, with times corrected for the age of the poll.</summary>
	private BRDPlanWindow? CurrentWindow
	{
		get
		{
			var now = DateTime.Now;
			// A wipe (out of combat, or the combat timer restarting) drops last pull's windows and plan latch,
			// even if no out-of-combat poll ran in between.
			var combatTime = DataCenter.CombatTimeRaw;
			if (!InCombat || combatTime < _lastCombatTime)
			{
				_polledAt = DateTime.MinValue;
				_windows = [];
				_planSeen = false;
				_windowEnd = DateTime.MinValue;
			}
			_lastCombatTime = combatTime;
			if (now - _polledAt >= TimeSpan.FromSeconds(PollInterval))
			{
				_polledAt = now;
				_windows = InCombat && BMRPlan_IPCSubscriber.IsEnabled
					? BMRPlan_IPCSubscriber.GetUpcomingPlannedActions(PlanLookAhead)
						.Where(a => a.TrackName == "Burst" && a.ModuleType.EndsWith("ClassBRDUtility", StringComparison.Ordinal))
						.ToList()
					: [];
				_planSeen = InCombat && (_planSeen || _windows.Count > 0);
			}
			var age = (float)(now - _polledAt).TotalSeconds;
			var window = _windows.Select(a => new BRDPlanWindow(a.ActivationIn - age, a.WindowEndIn - age, a.OptionName == "WindowWithPotion"))
				.Where(w => w.EndIn > 0f)
				.OrderBy(w => w.StartIn)
				.Cast<BRDPlanWindow?>()
				.FirstOrDefault();
			if (window is { StartIn: <= 0f } open)
			{
				_windowEnd = now.AddSeconds(open.EndIn);
				_windowPotion = open.Potion;
			}
			return window;
		}
	}

	/// <summary>A window closed under LateGrace ago. If its burst went, Battle Voice is cooling and this changes nothing.</summary>
	private bool InLateGrace => CurrentWindow is not { StartIn: <= 0f } && DateTime.Now < _windowEnd.AddSeconds(LateGrace);

	/// <summary>No plan this pull plays like BRD Reborn; with one, nothing ahead means wait for the next window.</summary>
	private protected override bool HoldBurst => HoldsBurst(CurrentWindow?.StartIn, PartyRaidBuffUp, _planSeen, InLateGrace);

	/// <summary>No visible window (kill-gated phase end) is unknown timing, so it never counts as far off.</summary>
	private protected override float PlannedBurstIn => CurrentWindow?.StartIn ?? 0f;

	private protected override bool PotionNow => InLateGrace ? _windowPotion
		: CurrentWindow is { } w ? w.Potion && IsOpen(w.StartIn, PartyRaidBuffUp)
		: !_planSeen && base.PotionNow;

	internal static bool HoldsBurst(float? startIn, bool partyRaidBuffUp, bool planSeen, bool inLateGrace)
		=> !inLateGrace && (startIn is float s ? !IsOpen(s, partyRaidBuffUp) : planSeen);

	internal static bool IsOpen(float startIn, bool partyRaidBuffUp)
		=> startIn <= 0f || partyRaidBuffUp && startIn <= WindowSlide;
}

internal readonly record struct BRDPlanWindow(float StartIn, float EndIn, bool Potion);
