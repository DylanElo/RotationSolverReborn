namespace RotationSolver.Updaters;

// BMR keeps plan conditions, targets and execution; this gate shares RSR's current weave budget.
internal static class PlannedMchUtilityWeave
{
	internal static bool HasNativeOwner(bool bmrReady, int version) => bmrReady && version >= 1;
	internal static bool HasNativeBrdOwner(bool bmrReady, int version) => bmrReady && version >= 2;

	internal static bool IsSupportedUtility(ECommons.ExcelServices.Job job, uint actionId)
		=> job switch
		{
			ECommons.ExcelServices.Job.MCH => actionId is (uint)ActionID.TacticianPvE or (uint)ActionID.DismantlePvE,
			ECommons.ExcelServices.Job.BRD => actionId is (uint)ActionID.TroubadourPvE or (uint)ActionID.NaturesMinnePvE or (uint)ActionID.TheWardensPaeanPvE,
			_ => false,
		};

	internal static bool CanReserveBrdUtility(bool nativeOwner, uint actionId, int target, int targetParam,
		string optionName, bool equivalentActive)
		=> nativeOwner && (actionId is (uint)ActionID.TroubadourPvE or (uint)ActionID.NaturesMinnePvE)
			&& target == 0 && targetParam == 0
			&& !(actionId == (uint)ActionID.TroubadourPvE
				&& optionName.EndsWith("IfNotActive", StringComparison.Ordinal) && equivalentActive);

	internal static bool ShouldReserveUtilityWindow(float activationIn, float windowEndIn, float gcdRemain)
		=> float.IsFinite(activationIn) && float.IsFinite(windowEndIn) && float.IsFinite(gcdRemain)
			&& windowEndIn > 0 && activationIn <= windowEndIn
			&& windowEndIn <= Math.Max(0, gcdRemain) + .7f;

	internal static bool ShouldHoldGcdForUtility(float activationIn, float windowEndIn, float gcdRemain)
		=> float.IsFinite(activationIn) && float.IsFinite(windowEndIn) && float.IsFinite(gcdRemain)
			&& windowEndIn > 0 && activationIn <= windowEndIn
			&& windowEndIn <= Math.Max(0, gcdRemain) + .7f
			&& gcdRemain <= Math.Max(0, activationIn);

	internal static bool ShouldHoldAbilityForUtility(float windowEndIn)
		=> float.IsFinite(windowEndIn) && windowEndIn > 0 && windowEndIn <= .7f;

	// 0 = native fallback, 1 = wait, 2 = safe now, 3 = deadline requires priority over DPS.
	internal static int Decide(bool enabled, bool supported, float animationLock, float actionLock,
		float deadline, float gcdRemain, float gcdAhead, bool hasGcd, bool protectedHandoff)
	{
		if (!enabled || !supported || !float.IsFinite(deadline) || deadline <= 0
			|| !float.IsFinite(actionLock) || actionLock <= 0
			|| !float.IsFinite(gcdRemain) || !float.IsFinite(animationLock))
			return 0;
		if (animationLock > 0)
			return 1; // The native executor still enforces its own animation lock.
		if (!protectedHandoff && (!hasGcd || gcdRemain > Math.Max(gcdAhead, actionLock)))
			return 2;
		// Earliest next ordinary slot follows the pending GCD's animation lock. Narrow plan
		// windows may require an intentional clip; never drop required mitigation for damage.
		return deadline <= Math.Max(0, gcdRemain) + actionLock ? 3 : 1;
	}
}
