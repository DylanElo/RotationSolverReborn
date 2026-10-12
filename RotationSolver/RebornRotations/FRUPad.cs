namespace RotationSolver.RebornRotations;

/// <summary>
/// FRU P1 has to end on Fatebreaker's last cast, its 10s enrage (20 pulls: 150.7-151.1s), not before it. Under 5% the
/// rotation stops while the rest of the party would kill it 5s+ early anyway, and keeps hitting when they are slower.
/// Shared by BRD Reborn and MCH Sync.
/// </summary>
internal static class FRUPad
{
	private const uint FatebreakerId = 0x459B;
	private const uint FatebreakerEnrage = 40128;

	// ponytail: before the cast bar shows, the phase end is assumed at 160s of combat; measured 158.2-160.9.
	internal static bool Padding(IBattleChara? target, float combatTime)
	{
		if (!DataCenter.IsInFRU || target is not { BaseId: FatebreakerId } fb || fb.GetHealthRatio() > 0.05f) return false;
		var endIn = fb.IsCasting && fb.CastActionId == FatebreakerEnrage
			? fb.TotalCastTime - fb.CurrentCastTime
			: Math.Max(160f - combatTime, 9.7f);
		return ShouldPad(endIn, RecentTtk(fb));
	}

	/// <summary>
	/// The enrage is a wipe, so this errs late-safe: hold only while the party alone kills it 5s+ early, always hit in the last 5s,
	/// and hit when HP is not dropping (NaN: nobody is pushing the kill).
	/// </summary>
	internal static bool ShouldPad(float phaseEndIn, float partyTtk)
		=> phaseEndIn > 5f && !float.IsNaN(partyTtk) && partyTtk < phaseEndIn - 5f;

	/// <summary>
	/// Kill time from the last 4s of HP. GetTTK averages since the pull, P1 downtime included, and read the party at about
	/// half speed: replaying 20 P1 ends, padding on it killed after the enrage in 12; on this, 4 (by hand: 1).
	/// </summary>
	private static float RecentTtk(IBattleChara target)
	{
		foreach (var (time, hps) in DataCenter.RecordedHP) // oldest first
		{
			var seconds = (float)(DateTime.Now - time).TotalSeconds;
			if (seconds <= 4f && hps != null && hps.TryGetValue(target.GameObjectId, out var then))
			{
				var now = target.GetHealthRatio();
				return seconds >= 2f && then > now ? now * seconds / (then - now) : float.NaN;
			}
		}
		return float.NaN;
	}
}
