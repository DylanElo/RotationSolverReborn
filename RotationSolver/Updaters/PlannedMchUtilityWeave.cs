namespace RotationSolver.Updaters;

// BMR keeps plan conditions, targets and execution; this gate shares RSR's current weave budget.
internal static class PlannedMchUtilityWeave
{
	internal static bool HasNativeOwner(bool bmrReady, int version) => bmrReady && version >= 1;

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
