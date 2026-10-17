using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.Updaters;

namespace FightSim;

/// <summary>Recorded BRD utility casts become native queue intents, sharing the production weave budget.</summary>
internal sealed class UtilityWeaveReplay(Timeline timeline)
{
	private sealed record Intent(float At, ActionID Id, float Width)
	{
		public bool Executed;
		// Recorded casts anchor sensitivity windows of the configured native duration.
		// They do not establish the original state-machine activation timestamp.
		public float End => At + Width;
	}
	private readonly List<Intent> _intents = CreateIntents(timeline);
	private static List<Intent> CreateIntents(Timeline timeline)
	{
		// Current FRU BRD plan, in chronological order within each action track.
		float[] minne = [3, 3, 3, 2.5f, 3];
		float[] troub = [2, 2, 2, 2, 2, 1.5f, 2, 2, 2];
		int minneIndex = 0, troubIndex = 0;
		var intents = new List<Intent>();
		foreach (var c in timeline.LoggedCasts.OrderBy(c => c[0].GetSingle()))
		{
			var id = c[1].GetString() switch
		{
			"Nature's Minne" => ActionID.NaturesMinnePvE,
			"Troubadour" => ActionID.TroubadourPvE,
			"The Warden's Paean" => ActionID.TheWardensPaeanPvE,
			_ => ActionID.None,
			};
			if (id == ActionID.None) continue;
			var width = id switch
			{
				ActionID.NaturesMinnePvE => c[0].GetSingle() >= 850 ? .4f
					: c[0].GetSingle() >= 720 ? .5f : minne[minneIndex++],
				ActionID.TroubadourPvE => troub[troubIndex++],
				_ => 3f,
			};
			intents.Add(new Intent(c[0].GetSingle(), id, width));
		}
		if (minneIndex != minne.Length || troubIndex != troub.Length)
			throw new InvalidOperationException("Utility replay requires the complete current FRU plan cast tracks.");
		return intents;
	}
	public bool HasGcd { get; set; }
	public int Reserved => _intents.Any(i => !i.Executed && PlannedMchUtilityWeave.ShouldHoldAbilityForUtility(i.End - Game.Now))
		? 2 : Math.Min(2, _intents.Count(i => !i.Executed && PlannedMchUtilityWeave.ShouldReserveUtilityWindow(
			i.At - Game.Now, i.End - Game.Now, HasGcd ? Game.GcdRemain : Game.GcdTotal)));
	public bool HoldGcd => _intents.Any(i => !i.Executed && PlannedMchUtilityWeave.ShouldHoldGcdForUtility(
		i.At - Game.Now, i.End - Game.Now, Game.GcdRemain));
	public BaseAction? Due()
	{
		var intent = _intents.FirstOrDefault(i => !i.Executed && i.At <= Game.Now && i.End >= Game.Now);
		return intent == null ? null : BaseAction.Create(intent.Id, (ref ActionSetting _) => { });
	}
	public void Executed(ActionID id) => _intents.First(i => !i.Executed && i.Id == id && i.At <= Game.Now && i.End >= Game.Now).Executed = true;
	public void Validate(int seed)
	{
		var missing = _intents.Where(i => !i.Executed).ToList();
		foreach (var intent in missing)
			foreach (var cast in Game.Casts.Where(c => Math.Abs(c.Time - intent.At) < 4f))
				Console.Error.WriteLine($"  around {intent.At:F2}: {cast.Time:F2} {cast.Action}");
		if (missing.Count > 0) throw new InvalidOperationException($"Utility replay seed {seed}: missed "
			+ string.Join(", ", missing.Select(i => $"{i.Id}@{i.At:F2}")));
		// Audit the emitted cast stream independently of the budget implementation.
		var abilities = new List<(float Time, string Action)>();
		float? lastGcd = null;
		var windows = 0;
		foreach (var cast in Game.Casts)
		{
			var name = cast.Action.Split(' ')[0];
			var isGcd = Enum.TryParse<ActionID>(name, out var id) && Game.Job.Spec(id).IsGcd;
			if (!isGcd) { abilities.Add((cast.Time, cast.Action)); continue; }
			// Long forced downtime is a sequence of idle slots, not one rolling weave window.
			if (lastGcd is { } start && cast.Time - start <= Game.BaseGcd + 2f)
			{
				windows++;
				if (abilities.Count > 2) throw new InvalidOperationException($"Utility replay seed {seed}: triple weave after {start:F2}: "
					+ string.Join(", ", abilities.Select(a => $"{a.Action}@{a.Time:F2}")));
			}
			lastGcd = cast.Time;
			abilities.Clear();
		}
		Console.WriteLine($"UTILITY seed {seed}: {_intents.Count}/{_intents.Count} executed; {windows} rolling GCD windows audited, no triple weaves.");
	}
}
