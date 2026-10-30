namespace RotationSolver.Basic.Helpers;

internal static class TimeToKillTracker
{
	private const long WindowMs = 30_000;
	private const int MinSamples = 3;
	private const float HpIncreaseTolerance = 0.01f;

	private sealed class History
	{
		public readonly Queue<(long Tick, float Ratio)> Samples = new();
		public long LastTick;
		public float LastRatio;
		public float Rate = float.NaN;
	}

	private static readonly Dictionary<ulong, History> _histories = [];

	internal static void Record(List<IBattleChara> hostiles, long now)
	{
		for (var i = 0; i < hostiles.Count; i++)
		{
			var target = hostiles[i];
			if (target == null || target.CurrentHp == 0)
			{
				continue;
			}

			var id = target.GameObjectId;
			var ratio = target.GetHealthRatio();
			if (ratio >= 1f)
			{
				_ = _histories.Remove(id);
				continue;
			}

			if (!_histories.TryGetValue(id, out var history))
			{
				history = new History();
				_histories[id] = history;
			}
			else if (ratio > history.LastRatio + HpIncreaseTolerance)
			{
				history.Samples.Clear();
			}

			history.Samples.Enqueue((now, ratio));
			history.LastTick = now;
			history.LastRatio = ratio;
			while (now - history.Samples.Peek().Tick > WindowMs)
			{
				_ = history.Samples.Dequeue();
			}

			history.Rate = FitRate(history.Samples);
		}

		foreach ((var id, var history) in _histories)
		{
			if (now - history.LastTick > WindowMs)
			{
				_ = _histories.Remove(id);
			}
		}
	}

	internal static float GetRate(ulong id)
	{
		return _histories.TryGetValue(id, out var history) ? history.Rate : float.NaN;
	}

	/// <summary>Oldest recorded HP ratio of the target within the last <paramref name="maxSeconds"/>.</summary>
	internal static bool TryGetRecent(ulong id, long now, float maxSeconds, out float ratio, out float seconds)
	{
		if (_histories.TryGetValue(id, out var history))
		{
			foreach ((var tick, var r) in history.Samples) // oldest first
			{
				seconds = (now - tick) / 1000f;
				if (seconds <= maxSeconds)
				{
					ratio = r;
					return true;
				}
			}
		}

		ratio = seconds = 0f;
		return false;
	}

	internal static void Clear()
	{
		_histories.Clear();
	}

	private static float FitRate(Queue<(long Tick, float Ratio)> samples)
	{
		var count = samples.Count;
		if (count < MinSamples)
		{
			return float.NaN;
		}

		var origin = samples.Peek().Tick;
		double meanT = 0, meanR = 0;
		foreach ((var tick, var ratio) in samples)
		{
			meanT += (tick - origin) / 1000.0;
			meanR += ratio;
		}

		meanT /= count;
		meanR /= count;

		double covariance = 0, variance = 0;
		foreach ((var tick, var ratio) in samples)
		{
			var dt = ((tick - origin) / 1000.0) - meanT;
			covariance += dt * (ratio - meanR);
			variance += dt * dt;
		}

		var rate = variance > 0 ? -covariance / variance : 0;
		return rate > 0 ? (float)rate : float.NaN;
	}
}
