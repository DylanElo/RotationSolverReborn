using ECommons.Logging;
using RotationSolver.IPC;

namespace RotationSolver.Updaters;

internal static class BMRPlanUpdater
{
	private static bool _subscribed;
	private static volatile bool _dirty = true;
	private static DateTime _lastPoll = DateTime.MinValue;

	private static readonly HashSet<uint> _queuedActionIds = [];

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

		_activeActionIds.Clear();
		foreach (var planned in actions)
		{
			if (IsActive(planned, elapsedSincePoll, out _))
			{
				_ = _activeActionIds.Add(planned.ActionId);
			}
		}

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

		return planned.ActionId != 0
			&& planned.ActivationIn - elapsedSincePoll <= 0f
			&& windowEndIn > 0f;
	}
}
