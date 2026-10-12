namespace RotationSolver.Updaters;

internal static class NoCastingStatusWindow
{
	// Lindwurm's facing-dependent knockbacks/cones resolve at expiry. Match
	// DoAction's three-second stop window, rather than cancelling for all 32 seconds.
	// Other configured statuses retain their existing immediate cancellation.
	internal static bool IsActive(uint statusId, float remainingTime) => statusId switch
	{
		4747 or 4748 or 4759 or 4760 or 4751 or 4976 => remainingTime < 3f,
		_ => true,
	};
}
