namespace RotationSolver.RebornRotations.Melee;

internal enum SAMCycleGcd { Combo, Higanbana, Ogi, KaeshiNamikiri, Tendo, Midare, Kaeshi, TendoKaeshi }

internal readonly record struct SAMCycleState(
	int Sen, bool Meikyo, bool Tendo, bool BuffsReady, float Dot,
	float Gcd, float KaeshiRemaining, bool LiveBuffs, bool Bank,
	bool ChargeCapping, bool ComboStarted, bool Opener, bool Ending, float OgiRemaining = 0);

internal static class SAMSyncCycle
{
	internal static bool UseMeikyo(SAMCycleState s)
	{
		if (s.Meikyo || s.Tendo) return false;
		// Finish a prepared DoT before creating another Tendo obligation.
		if (HiganbanaDue(s)) return false;
		if (!s.BuffsReady || s.ChargeCapping || s.Ending) return true;
		// Second opener charge follows the first Tendo/Kaeshi pair, before Higanbana.
		if (s.Opener && s.Sen == 0 && s.Dot <= 0) return true;
		if (s.LiveBuffs) return true;
		// Preserve a charge for an imminent/late raid window, without sacrificing the DoT.
		if (s.Bank && s.Dot > 2 * s.Gcd) return false;
		// Finish the current combo, then use Meikyo to make the missing Sen in time.
		var setup = (6 - s.Sen + (s.KaeshiRemaining > 0 ? 1 : 0)) * s.Gcd;
		return !s.ComboStarted && s.Sen > 0 && s.Dot <= System.MathF.Min(15, setup);
	}

	internal static bool HoldKaeshi(SAMCycleState s)
		=> !s.LiveBuffs && !s.Ending && !s.Tendo && s.Sen < 3
			&& s.KaeshiRemaining > 2 * s.Gcd;

	internal static bool HiganbanaDue(SAMCycleState s)
		=> !s.Ending && s.Sen == 1 && s.BuffsReady && (!s.Tendo || s.Opener) && s.Dot <= 2 * s.Gcd;

	internal static bool OgiUrgent(SAMCycleState s)
		=> s.OgiRemaining > 0 && (s.Ending || s.OgiRemaining <= s.Gcd * (s.Tendo
			? (3 - s.Sen) * (s.Meikyo ? 1 : 3) + 3 : 2));

	internal static bool RequiredStatusCovers(float remaining, float executionDelay, float castTime)
		=> remaining > System.MathF.Max(0, executionDelay) + castTime + 0.1f;

	// Availability comes from the real CanUse checks, including movement, target and cooldowns.
	internal static SAMCycleGcd Select(SAMCycleState s, bool higan, bool ogi,
		bool namikiri, bool tendo, bool midare, bool kaeshi, bool tendoKaeshi)
	{
		if (ogi && s.BuffsReady && OgiUrgent(s))
			return kaeshi && s.KaeshiRemaining <= s.OgiRemaining ? SAMCycleGcd.Kaeshi : SAMCycleGcd.Ogi;
		if (kaeshi && s.KaeshiRemaining <= 2 * s.Gcd) return SAMCycleGcd.Kaeshi;
		if (higan && HiganbanaDue(s)) return SAMCycleGcd.Higanbana;
		if (namikiri) return SAMCycleGcd.KaeshiNamikiri;
		if (kaeshi && !HoldKaeshi(s)) return SAMCycleGcd.Kaeshi;
		if (tendoKaeshi) return SAMCycleGcd.TendoKaeshi;
		// Keep the opener's first Ogi behind Higanbana, with an expiry/ending escape.
		if (ogi && s.BuffsReady && (!s.Tendo || s.Opener || s.Ending)
			&& (s.Dot > 0 || s.Ending || !s.Opener)) return SAMCycleGcd.Ogi;
		if (tendo) return SAMCycleGcd.Tendo;
		if (midare) return SAMCycleGcd.Midare;
		return SAMCycleGcd.Combo;
	}
}
