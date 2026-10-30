using RotationSolver.Basic.Data;

namespace RotationSolver.Basic.Helpers;

internal static class TargetAvailability
{
	internal static bool CanTarget(ActionID action, uint baseId, bool inFru, bool manual, float downtimeIn, float downtimeEndIn)
	{
		if (action != ActionID.EmpyrealArrowPvE || !inFru || manual || baseId is not (0x45A9 or 0x45AB)
			|| !(downtimeIn >= 0f && downtimeIn < downtimeEndIn && downtimeEndIn <= 60f)) return true;

		// FRUStates.P4CrystallizeTime: Frost disappears 1.1s before Oracle's global downtime flag.
		var disappearanceLead = baseId == 0x45A9 ? 1.1f : 0f;
		// shortcut: measured packet median; replace with native impact timing if exposed (EA executes immediately here).
		return downtimeIn > disappearanceLead + 1.025f;
	}
}
