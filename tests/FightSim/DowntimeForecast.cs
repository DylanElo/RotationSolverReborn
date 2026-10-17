namespace FightSim;

/// <summary>FRU's Timeline.NextDowntimeIn/EndIn visibility: flags in the current BossMod phase only.</summary>
internal sealed class DowntimeForecast
{
	private sealed record Phase(float Enter, List<float> Starts, List<float> Ends);
	private readonly List<Phase> _phases = [];

	public DowntimeForecast(Timeline timeline)
	{
		// FRUStates has four phases: P1, P2, combined P3/P4, P5. HP-triggered phase changes
		// have no DowntimeStart flag. NextTransitionWithFlag never walks into the next phase.
		uint[][] ids = [[0x459B], [0x459F, 0x45A3, 0x45A5], [0x45A7, 0x45A9, 0x45AB], [0x45AF]];
		var enter = 0f;
		foreach (var group in ids)
		{
			var actors = timeline.Actors.Where(a => group.Contains(a.BaseId)).ToList();
			var spans = actors.SelectMany(a => a.Spans).OrderBy(s => s[0]).ToList();
			if (spans.Count == 0) continue;
			var phase = new Phase(enter, [], []);
			_phases.Add(phase);
			// shortcut: flag times are anchored to logged returns, not a full state-machine replay.
			// Upgrade to recorded IPC/state transitions when measuring sub-second scheduling accuracy.
			if (group[0] != 0x459B) phase.Ends.Add(spans[0][0]);
			var boss = actors.FirstOrDefault(a => a.BaseId == group[0]);
			if (group[0] is 0x459B or 0x459F && boss != null)
			{
				// Utopian Sky, Diamond Dust and Light Rampant. A final span ending at a
				// kill/wipe is not a flagged disappearance.
				for (var i = 1; i < boss.Spans.Count; i++)
				{
					phase.Starts.Add(boss.Spans[i - 1][1]);
					phase.Ends.Add(boss.Spans[i][0]);
				}
				if (group[0] == 0x459F && actors.Where(a => a.BaseId == 0x45A3).SelectMany(a => a.Spans).MinBy(s => s[0]) is { } crystal)
				{
					phase.Starts.Add(boss.Spans[^1][1]); // Absolute Zero
					phase.Ends.Add(crystal[0]);
				}
			}
			// Ultimate Relativity has downtime flags even though Oracle stays targetable.
			if (group[0] == 0x45A7 && actors.FirstOrDefault(a => a.BaseId == 0x45A7) is { } oracle)
			{
				foreach (var stun in timeline.PlayerDebuffs.Where(d => d.Name == "Stun"
					&& d.Start >= oracle.Spans[0][0] && d.End <= oracle.Spans[^1][1]))
				{
					phase.Starts.Add(stun.Start);
					// FRUStates ends this downtime at Shell Crusher's cast start, not stun expiry.
					var shell = timeline.EnemyCasts?.FirstOrDefault(c => c.Key == oracle.Key && c.Id == 40286 && c.Start >= stun.Start && c.Start <= stun.End + 1f);
					if (shell != null) phase.Ends.Add(shell.Start);
				}
				if (actors.FirstOrDefault(a => a.BaseId == 0x45A9) is { } usurper)
				{
					phase.Starts.Add(oracle.Spans[^1][1]);
					phase.Ends.Add(usurper.Spans[0][0]);
					if (usurper.Spans.Count > 1 && actors.FirstOrDefault(a => a.BaseId == 0x45AB) is { } p4Oracle)
					{
						phase.Starts.Add(p4Oracle.Spans[0][1]);
						phase.Ends.Add(usurper.Spans[1][0]);
					}
				}
			}
			phase.Starts.Sort();
			phase.Ends.Sort();
			// The last attackable span approximates the kill event; the next phase is entered
			// only when its actors exist in this replay. Never forecast the terminal wipe as downtime.
			enter = spans.Max(s => s[1]);
		}
	}

	public (float Start, float End) At(float now)
	{
		var phase = _phases.LastOrDefault(p => p.Enter <= now);
		return phase == null ? (float.MaxValue, float.MaxValue) : (Next(phase.Starts, now), Next(phase.Ends, now));
	}

	public bool IsInCurrentPhase(float now, float at)
	{
		var current = _phases.FindLastIndex(p => p.Enter <= now);
		return current >= 0 && current == _phases.FindLastIndex(p => p.Enter <= at);
	}

	private static float Next(List<float> flags, float now)
	{
		var next = flags.FirstOrDefault(t => t > now, float.MaxValue);
		return next == float.MaxValue ? next : next - now;
	}
}
