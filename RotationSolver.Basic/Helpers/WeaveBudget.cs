namespace RotationSolver.Basic.Helpers;

internal sealed class WeaveBudget
{
	private int _used;
	private readonly List<(uint ActionId, ushort Sequence)> _pending = [];
	private readonly Queue<uint> _recentEffects = new();
	private readonly Queue<ushort> _recentRequests = new();
	private double _lastGcdAt = double.NegativeInfinity;
	private double _lastAbilityAt = double.NegativeInfinity;
	private double _noGcdSince = double.NaN;
	internal bool Exhausted => _used >= 2;
	internal int Used => _used;
	internal bool IsRsrRequest { get; private set; }

	internal bool ShouldBlock(bool isGcd, bool exempt, int reservedSlots = 0, bool reservedUtility = false)
		=> !isGcd && !exempt && (Exhausted || (!reservedUtility && _used + reservedSlots >= 2));

	internal bool TryUse(bool enforce, bool isGcd, bool exempt, uint actionId, Func<bool> use,
		int reservedSlots = 0, bool reservedUtility = false, double now = 0)
	{
		if (!enforce || isGcd || exempt) return use();
		if (ShouldBlock(isGcd, exempt, reservedSlots, reservedUtility)
			|| _pending.Exists(p => p.ActionId == actionId)) return false;

		// Accepted requests spend a slot before their effects arrive, including queued requests.
		_pending.Add((actionId, 0));
		_used++;
		IsRsrRequest = true;
		try
		{
			if (use())
			{
				_lastAbilityAt = Math.Max(_lastAbilityAt, now);
				return true;
			}
		}
		catch
		{
			CancelRequest(actionId);
			throw;
		}
		finally { IsRsrRequest = false; }
		CancelRequest(actionId);
		return false;
	}

	private void CancelRequest(uint actionId)
	{
		var index = _pending.FindIndex(p => p.ActionId == actionId);
		if (index >= 0)
		{
			_pending.RemoveAt(index);
			_used--;
		}
	}

	internal bool RecordRequest(uint actionId, ushort sequence, double now = 0)
	{
		if (!RememberRequest(sequence)) return false;
		_lastAbilityAt = Math.Max(_lastAbilityAt, now);
		var reserved = _pending.FindIndex(p => p.ActionId == actionId && p.Sequence == 0);
		if (reserved >= 0) _pending[reserved] = (actionId, sequence);
		else
		{
			_pending.Add((actionId, sequence));
			_used++;
		}
		return true;
	}

	private bool RememberRequest(ushort sequence)
	{
		if (sequence == 0 || _recentRequests.Contains(sequence)) return false;
		_recentRequests.Enqueue(sequence);
		if (_recentRequests.Count > 64) _recentRequests.Dequeue();
		return true;
	}

	internal void Record(bool isGcd, uint actionId = 0, uint effectSequence = 0, ushort sourceSequence = 0, double now = 0)
	{
		// Multi-target/repeated effect callbacks must not reopen a spent GCD's budget.
		if (effectSequence != 0)
		{
			// A repeated counter is a multi-target callback only while it is that close to the last GCD; a GCD can't recur
			// within 1s, so a later one is new even if the counter reads stale.
			if (_recentEffects.Contains(effectSequence) && !(isGcd && now - _lastGcdAt >= 1d)) return;
			_recentEffects.Enqueue(effectSequence);
			if (_recentEffects.Count > 64) _recentEffects.Dequeue();
		}
		RememberRequest(sourceSequence);
		if (isGcd)
		{
			_lastGcdAt = now;
			_noGcdSince = double.NaN;
			_used = 0;
			_pending.Clear();
		}
		else
		{
			_lastAbilityAt = Math.Max(_lastAbilityAt, now);
			var request = _pending.FindIndex(p => p.ActionId == actionId
				&& (sourceSequence == 0 || p.Sequence == 0 || p.Sequence == sourceSequence));
			if (request >= 0) _pending.RemoveAt(request);
			else _used++;
		}
	}

	internal bool RefreshIdleWindow(bool hasGcd, float gcdDuration, double now)
	{
		if (!double.IsFinite(now)) return false;
		if (hasGcd)
		{
			_noGcdSince = double.NaN;
			return false;
		}
		if (double.IsNaN(_noGcdSince)) _noGcdSince = now;
		// shortcut: canceled queued requests stay reserved until the next GCD/reset; correlate cancellation if traces show this.
		if (_pending.Count != 0 || !float.IsFinite(gcdDuration) || gcdDuration <= 0
			|| now - _noGcdSince < gcdDuration || now - _lastGcdAt < gcdDuration || now - _lastAbilityAt < gcdDuration)
			return false;
		_used = 0;
		return true;
	}

	internal void Reset()
	{
		_used = 0;
		_pending.Clear();
		_recentEffects.Clear();
		_recentRequests.Clear();
		_lastGcdAt = _lastAbilityAt = double.NegativeInfinity;
		_noGcdSince = double.NaN;
	}
}
