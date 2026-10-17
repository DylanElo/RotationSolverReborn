using ECommons.Logging;
using RotationSolver.IPC;

namespace RotationSolver.Updaters;

/// <summary>
/// Polls BossMod's Cooldown Planner IPC for upcoming planned actions and, when a planned action's
/// activation window becomes active, queues it via <see cref="DataCenter.AddCommandAction"/> so RSR
/// itself drives usage of the action through the normal command-action / interception pipeline
/// (<see cref="ActionQueueManager"/>), rather than passively waiting for something else to trigger it.
/// Refreshes immediately when BMR notifies that the plan changed, with a periodic fallback poll.
/// </summary>
internal static class BMRPlanUpdater
{
	private static bool _subscribed;
	private static volatile bool _dirty = true;
	private static DateTime _lastPoll = DateTime.MinValue;

	// Ids of planned actions already queued for the current activation window, so we don't re-queue every frame.
	private static readonly HashSet<uint> _queuedActionIds = [];

	// Ids of planned actions whose activation window is open this update.
	private static readonly HashSet<uint> _activeActionIds = [];

	private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(5);

	public static void Enable()
	{
		if (_subscribed)
		{
			return;
		}

		try
		{
			BMRPlan_IPCSubscriber.SubscribeActionsChanged(OnActionsChanged);
			_subscribed = true;
		}
		catch (Exception ex)
		{
			PluginLog.Error($"[BMRPlanUpdater] Failed to subscribe to Plan.ActionsChanged: {ex}");
		}
	}

	public static void Disable()
	{
		if (!_subscribed)
		{
			return;
		}

		try
		{
			BMRPlan_IPCSubscriber.UnsubscribeActionsChanged();
		}
		catch (Exception ex)
		{
			PluginLog.Error($"[BMRPlanUpdater] Failed to unsubscribe from Plan.ActionsChanged: {ex}");
		}
		finally
		{
			_subscribed = false;
			_queuedActionIds.Clear();
			DataCenter.BMRReservedUtilityActions.Clear();
			DataCenter.BMRUtilityGcdHold = false;
			DataCenter.BMRUtilityAbilityHold = false;
		}
	}

	private static void OnActionsChanged()
	{
		_dirty = true;
		_queuedActionIds.Clear();
	}

	public static void Update()
	{
		if (!Service.Config.UseBmrPlan)
		{
			DataCenter.BMRReservedUtilityActions.Clear();
			DataCenter.BMRUtilityGcdHold = false;
			DataCenter.BMRUtilityAbilityHold = false;
			if (DataCenter.BMRPlannedActions.Count > 0)
			{
				DataCenter.ResetBmrPlanData();
				_queuedActionIds.Clear();
			}

			return;
		}

		Enable();

		// The readiness check is cached, so polling it picks up BossMod loading after RSR.
		if (!BMRPlan_IPCSubscriber.IsEnabled)
		{
			DataCenter.ResetBmrPlanData();
			_queuedActionIds.Clear();
			_dirty = true;
			return;
		}

		var now = DateTime.Now;
		var pollInterval = DataCenter.InCombat && DataCenter.Job == ECommons.ExcelServices.Job.BRD
			? TimeSpan.FromSeconds(.25) : FallbackPollInterval;
		if (_dirty || now - _lastPoll >= pollInterval)
		{
			try
			{
				DataCenter.BMRPlannedActions = BMRPlan_IPCSubscriber.GetUpcomingPlannedActions(Service.Config.BMRPlanLookAheadSeconds);
				_lastPoll = now;
				_dirty = false;
			}
			catch (Exception ex)
			{
				PluginLog.Error($"[BMRPlanUpdater] Failed to poll Plan.GetUpcomingActions: {ex}");
				DataCenter.ResetBmrPlanData();
				return;
			}
		}

		QueueActiveActions(now);
	}

	/// <summary>
	/// Checks the cached planned actions against the actual elapsed time since they were polled and
	/// queues any that are now within their activation window so RSR uses them through the normal
	/// command-action pipeline.
	/// </summary>
	private static void QueueActiveActions(DateTime now)
	{
		var actions = DataCenter.BMRPlannedActions;
		var elapsedSincePoll = (float)(now - _lastPoll).TotalSeconds;
		var rotationActions = RotationUpdater.CurrentRotationActions ?? [];
		var dutyActions = DataCenter.CurrentDutyRotation?.AllActions ?? [];
		var nativeBrdWeaveOwner = BMRPlan_IPCSubscriber.HasNativeBrdWeaveOwner;
		var reservationHorizon = ActionUpdater.NextGCDAction == null ? DataCenter.DefaultGCDTotal : DataCenter.DefaultGCDRemain;
		DataCenter.BMRReservedUtilityActions.Clear();
		DataCenter.BMRUtilityGcdHold = false;
		DataCenter.BMRUtilityAbilityHold = false;
		if (DataCenter.InCombat && DataCenter.Job == ECommons.ExcelServices.Job.BRD)
		{
			foreach (var planned in actions)
			{
				// Lookahead cannot resolve dispel/target conditions; reserve only native self utilities.
				if (!PlannedMchUtilityWeave.CanReserveBrdUtility(nativeBrdWeaveOwner, planned.ActionId,
						planned.Target, planned.TargetParam, planned.OptionName,
						StatusHelper.PlayerHasStatus(false, StatusHelper.RangePhysicalDefense))
					|| !PlannedMchUtilityWeave.ShouldReserveUtilityWindow(planned.ActivationIn - elapsedSincePoll,
						planned.WindowEndIn - elapsedSincePoll, reservationHorizon)) continue;
				var utility = ((ActionID)planned.ActionId).GetActionFromID(false, rotationActions, dutyActions);
				if (utility != null && utility.EnoughLevel && utility.Cooldown.CurrentCharges > 0)
				{
					DataCenter.BMRReservedUtilityActions.Add(planned.ActionId);
					DataCenter.BMRUtilityAbilityHold |= PlannedMchUtilityWeave.ShouldHoldAbilityForUtility(planned.WindowEndIn - elapsedSincePoll);
					// A GCD just before a narrow mitigation window would lock it out entirely.
					DataCenter.BMRUtilityGcdHold |= !DataCenter.WeaveLimitReached && PlannedMchUtilityWeave.ShouldHoldGcdForUtility(
						planned.ActivationIn - elapsedSincePoll, planned.WindowEndIn - elapsedSincePoll, DataCenter.DefaultGCDRemain);
				}
			}
		}

		// The same action can appear several times in a plan (e.g. a cooldown at two timestamps), so
		// work out which ids are active before forgetting any; otherwise a later, inactive entry would
		// clear the queued flag of the active one and it would be re-queued every other frame.
		_activeActionIds.Clear();
		foreach (var planned in actions)
		{
			if (IsActive(planned, elapsedSincePoll, out _))
			{
				_ = _activeActionIds.Add(planned.ActionId);
			}
		}

		// Ids whose window has closed may be queued again the next time they come up.
		_queuedActionIds.IntersectWith(_activeActionIds);

		if (_activeActionIds.Count == 0)
		{
			return;
		}

		var nativeMchWeaveOwner = BMRPlan_IPCSubscriber.HasNativeMchWeaveOwner;

		foreach (var planned in actions)
		{
			if (!IsActive(planned, elapsedSincePoll, out var windowEndIn))
			{
				continue;
			}

			// Native BMR resolves these entries every frame and consults the shared weave gate.
			// Keep prediction data, but never create a second executor for the same mitigation.
			if (planned.ActionId is (uint)ActionID.TacticianPvE or (uint)ActionID.DismantlePvE
				&& nativeMchWeaveOwner)
				continue;
			if (planned.ActionId is (uint)ActionID.TroubadourPvE or (uint)ActionID.NaturesMinnePvE or (uint)ActionID.TheWardensPaeanPvE
				&& nativeBrdWeaveOwner)
				continue;

			// The command queue cannot carry BMR target overrides; native BMR must execute these entries.
			if (planned.Target != 0 || planned.TargetParam != 0)
			{
				continue;
			}

			if (planned.TrackName is "Tactician" or "Troubadour"
				&& planned.OptionName.EndsWith("IfNotActive", StringComparison.Ordinal)
				&& RotationSolver.Basic.Helpers.StatusHelper.PlayerHasStatus(false, RotationSolver.Basic.Helpers.StatusHelper.RangePhysicalDefense))
			{
				continue;
			}

			if (!_queuedActionIds.Add(planned.ActionId))
			{
				// Already queued for this activation window.
				continue;
			}

			var matchingAction = ((ActionID)planned.ActionId).GetActionFromID(false, rotationActions, dutyActions);
			if (matchingAction == null)
			{
				continue;
			}

			DataCenter.AddCommandAction(matchingAction, windowEndIn);
			PluginLog.Debug($"[BMRPlanUpdater] Queued BMR planned action: {matchingAction.Name} (ActionId: {planned.ActionId}) for {windowEndIn:F1}s");
		}
	}

	private static bool IsActive(in BMRPlannedAction planned, float elapsedSincePoll, out float windowEndIn)
	{
		windowEndIn = planned.WindowEndIn - elapsedSincePoll;

		// ActionId 0 means BMR couldn't resolve the plan entry to a concrete action.
		return planned.ActionId != 0
			&& planned.ActivationIn - elapsedSincePoll <= 0f
			&& windowEndIn > 0f;
	}
}
