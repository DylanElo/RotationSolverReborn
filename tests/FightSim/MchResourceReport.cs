using System.Text.Json;
using RotationSolver.Basic.Data;

namespace FightSim;

// Read-only snapshots before selection: the same rotation carries its actual state across every phase.
internal sealed class MchResourceReport
{
	private readonly Queue<(string Name, float At)> _markers;
	private readonly int _seed;
	public MchResourceReport(Timeline timeline, int seed)
	{
		_seed = seed;
		var markers = new List<(string, float)>();
		foreach (var actor in timeline.Actors.Where(a => a.BaseId is 0x459B or 0x459F or 0x45A3))
		{
			markers.Add(($"{actor.BaseId:X}:{actor.Key}:entry", actor.Spans[0][0]));
			markers.Add(($"{actor.BaseId:X}:{actor.Key}:end", actor.Spans[^1][1]));
			if (actor.BaseId == 0x459F)
				foreach (var delay in new[] { 10f, 20f, 30f }) markers.Add(($"P2+{delay}", actor.Spans[0][0] + delay));
		}
		_markers = new(markers.OrderBy(m => m.Item2));
	}
	public void Step()
	{
		while (_markers.TryPeek(out var marker) && Game.Now >= marker.At)
		{
			_markers.Dequeue();
			var cooldowns = new[] { ActionID.BarrelStabilizerPvE, ActionID.WildfirePvE, ActionID.AirAnchorPvE, ActionID.ChainSawPvE, ActionID.DrillPvE, ActionID.ReassemblePvE }
				.ToDictionary(a => a.ToString(), a => Math.Max(0, Game.Cooldown(a).FullAt - (Game.Cooldown(a).MaxCharges - 1) * Game.Cooldown(a).OneCharge - Game.Now));
			Console.WriteLine("RESOURCE " + JsonSerializer.Serialize(new { seed = _seed, marker.Name, time = Game.Now,
				heat = Machinist.Gauge.Heat, battery = Machinist.Gauge.Battery, robot = Machinist.Gauge.IsRobotActive,
				statuses = Game.Player.Statuses, cooldowns,
				charges = cooldowns.Keys.ToDictionary(a => a, a => Game.Cooldown(Enum.Parse<ActionID>(a)).Charges),
				fullRecharge = cooldowns.Keys.ToDictionary(a => a, a => Math.Max(0, Game.Cooldown(Enum.Parse<ActionID>(a)).FullAt - Game.Now)),
				combo = ((Machinist)Game.Job).LastComboAction.ToString(), comboLeft = ((Machinist)Game.Job).ComboTime }));
		}
	}
}
