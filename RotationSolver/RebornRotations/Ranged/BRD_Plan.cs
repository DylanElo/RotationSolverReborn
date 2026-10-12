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

	private List<BMRPlannedAction> _windows = [];
	private DateTime _polledAt = DateTime.MinValue;

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
			return _windows.Select(a => new BRDPlanWindow(a.ActivationIn - age, a.WindowEndIn - age, a.OptionName == "WindowWithPotion"))
				.Where(w => w.EndIn > 0f)
				.OrderBy(w => w.StartIn)
				.Cast<BRDPlanWindow?>()
				.FirstOrDefault();
		}
	}

	private static bool PartyRaidBuffUp => Buffs.Any(b => b.JobAbbr != "BRD" && (b.Type == StatusType.Buff
		? StatusHelper.PlayerHasStatus(false, b.Ids)
		: CurrentTarget?.HasStatus(false, b.Ids) == true));

	/// <summary>No plan this pull plays like BRD Reborn; with one, nothing ahead means wait for the next window.</summary>
	private protected override bool HoldBurst => CurrentWindow is { } w ? !IsOpen(w.StartIn, PartyRaidBuffUp) : _planSeen;

	private protected override bool PotionNow => CurrentWindow is { } w
		? w.Potion && IsOpen(w.StartIn, PartyRaidBuffUp)
		: !_planSeen && base.PotionNow;

	internal static bool IsOpen(float startIn, bool partyRaidBuffUp)
		=> startIn <= 0f || partyRaidBuffUp && startIn <= WindowSlide;
}

internal readonly record struct BRDPlanWindow(float StartIn, float EndIn, bool Potion);
