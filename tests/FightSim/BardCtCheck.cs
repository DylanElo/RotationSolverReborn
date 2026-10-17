namespace FightSim;

/// <summary>End-to-end outcomes for the two full BRD FRU clear replays.</summary>
internal static class BardCtCheck
{
	public static bool Validate(Timeline timeline, int seed)
	{
		void Require(bool condition, string message)
		{
			if (condition) return;
			foreach (var cast in Game.Casts.Where(c => c.Time > 600 &&
				(c.Action.Contains("Finale") || c.Action.Contains("Encore") || c.Action.Contains("Minuet") || c.Action.Contains("Ballad") || c.Action.Contains("Paeon"))))
				Console.Error.WriteLine($"  seed {seed} {cast.Time:F2} {cast.Action}");
			throw new InvalidOperationException($"BRD CT seed {seed}: {message}");
		}
		var usurper = timeline.Actors.Single(a => a.BaseId == 0x45A9);
		var oracle = timeline.Actors.Single(a => a.BaseId == 0x45AB);
		var p5 = timeline.Actors.Single(a => a.BaseId == 0x45AF).Spans[0][0];
		var ctStart = Math.Max(usurper.Spans[0][1], oracle.Spans[0][1]);
		var ctEnd = usurper.Spans[1][0];
		var p4End = Math.Max(usurper.Spans[^1][1], oracle.Spans[^1][1]);
		var ctFinales = Game.Casts.Count(c => c.Action.StartsWith("RadiantFinalePvE") && c.Time >= ctStart && c.Time < ctEnd);
		Require(ctFinales == 1, $"expected one standalone CT Finale, got {ctFinales}");
		if (ctFinales == 1)
			Require(Game.Casts.Any(c => c.Action == "RadiantEncorePvE" && c.Time >= ctEnd && c.Time < p4End),
				"CT Encore must land before the P4 kill");
		if (ctFinales == 1)
			Require(Game.Casts.Any(c => c.Action == "PitchPerfectPvE" && c.Time >= ctEnd && c.Time < p4End),
				"CT recovery must keep Minuet so Pitch Perfect lands when the pair returns");
		var finales = Game.Casts.Where(c => c.Action.StartsWith("RadiantFinalePvE") && c.Time >= p5).ToList();
		Require(finales.Count == 3 && finales.All(c => c.Action == "RadiantFinalePvE (3 Codas)"),
			"all three P5 Finales must retain three Codas");
		Require(Game.Casts.Count(c => c.Action == "BattleVoicePvE") == timeline.PlanWindows.Count,
			"a planned Battle Voice was lost");
		foreach (var window in timeline.PlanWindows)
		{
			Require(Game.Casts.Count(c => c.Action == "BattleVoicePvE" && c.Time >= window.Start - 8f && c.Time < window.End) == 1,
				$"Battle Voice missed its planned window at {window.Start:F2}s");
			if (window.Start >= p5)
				Require(finales.Count(c => c.Time >= window.Start - 8f && c.Time < window.End) == 1,
					$"three-Coda Finale missed its P5 window at {window.Start:F2}s");
		}

		// A recovered Coda must not create a cooldown hole later in P5's song cycle.
		var covered = p5;
		var firstSong = true;
		foreach (var song in Game.Casts.Where(c => c.Action is "TheWanderersMinuetPvE" or "MagesBalladPvE" or "ArmysPaeonPvE"))
		{
			if (song.Time + 45f <= p5) continue;
			// The first song may wait for its native cooldown plus two weaves; later gaps get no such allowance.
			Require(song.Time <= covered + (firstSong ? Game.BaseGcd + 1.3f : 2.5f), $"P5 song gap of {song.Time - covered:F2}s");
			firstSong = false;
			covered = song.Time + 45f;
		}
		Require(covered >= timeline.Duration, "song coverage ends before the clear");
		var firstP5Song = Game.Casts.First(c => c.Time >= p5 - 1f && (c.Action is "TheWanderersMinuetPvE" or "MagesBalladPvE" or "ArmysPaeonPvE"));
		if (ctFinales == 1) Require(firstP5Song.Action == "TheWanderersMinuetPvE", "CT recovery must preserve the P5 Minuet opener");
		return ctFinales == 1;
	}
}
