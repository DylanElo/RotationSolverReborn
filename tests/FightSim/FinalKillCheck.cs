using System.Reflection;
using Dalamud.Game.ClientState.JobGauge.Enums;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Rotations;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>
/// The final-kill dump (FinalKillIn): short synthetic fights where FRU Pandora dies at a known time with one resource
/// that the ordinary rules would hold. Everything else is on cooldown, so the resource is spent only if nothing holds it.
/// Each case runs again on the P4 pair (not the final kill), where FinalKillIn must stay NaN.
/// </summary>
internal static class FinalKillCheck
{
	private const float Death = 400f;
	private static readonly List<string> Holds = [];
	private static int _passed;

	public static void Run()
	{
		foreach (var heat in new byte[] { 50, 70, 95 })
		foreach (var left in new[] { 6f, 10f, 15f })
			Case("MCH", $"{heat} Heat, Wildfire back in 20s, {left}s left", left, _ =>
			{
				Machinist.Gauge.Heat = heat;
				Ready(HyperchargePvE);
				Game.Cooldown(WildfirePvE).FullAt = Game.Now + 20f;
			}, () => Cast(HyperchargePvE) > 0 ? null : $"Hypercharge never used; {Machinist.Gauge.Heat} Heat at death");

		foreach (var battery in new byte[] { 50, 70, 90 })
		foreach (var left in new[] { 7f, 8f, 12f, 18f })
			Case("MCH", $"{battery} Battery, {left}s left", left, _ =>
			{
				Machinist.Gauge.Battery = battery;
				Ready(AutomatonQueenPvE, QueenOverdrivePvE);
			}, () => Cast(AutomatonQueenPvE) == 0 ? $"Queen never summoned; {Machinist.Gauge.Battery} Battery at death"
				: Damage("Crowned Collider") > 0 ? null : "Queen summoned but Crowned Collider did not land before the kill");

		foreach (var left in new[] { 4f, 5f, 7f, 10f })
			Case("MCH", $"Barrel ready, {left}s left", left, _ => Ready(BarrelStabilizerPvE),
				() => Cast(BarrelStabilizerPvE) == 0 ? "Barrel Stabilizer never used"
					: Cast(FullMetalFieldPvE) > 0 ? null : "Barrel used but Full Metal Field did not land");

		foreach (var left in new[] { 5f, 10f, 15f })
			Case("MCH", $"Excavator ready, Battery 10 under the Queen target, {left}s left", left, rotation =>
			{
				var target = (int)rotation.GetType().BaseType!.GetProperty("QueenBatteryTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rotation)!;
				Machinist.Gauge.Battery = (byte)Math.Clamp(target - 10, 0, 100);
				Game.Player.Statuses[StatusID.ExcavatorReady] = 30f;
			}, () => Cast(ExcavatorPvE) > 0 ? null : "Excavator never used");

		foreach (var left in new[] { 5.5f, 10f, 15f })
			Case("MCH", $"Wildfire ready, 50 Heat, {left}s left", left, _ =>
			{
				Machinist.Gauge.Heat = 50; // Wildfire's ActionCheck: it goes with a Hypercharge
				Ready(WildfirePvE, HyperchargePvE);
			},
				() => Cast(WildfirePvE) == 0 ? "Wildfire never used"
					: Damage("Wildfire") > 0 ? null : "Wildfire used but never detonated with stored hits before the kill");

		// Barrel and Wildfire share a 120s recast, so at a kill they are usually ready together.
		foreach (var left in new[] { 5.5f, 10f, 15f })
			Case("MCH", $"Wildfire + Barrel ready, 50 Heat, {left}s left", left, _ =>
			{
				Machinist.Gauge.Heat = 50;
				Ready(WildfirePvE, HyperchargePvE, BarrelStabilizerPvE);
			}, () => Cast(WildfirePvE) == 0 ? "Wildfire never used"
				: Damage("Wildfire") > 0 ? null : "Wildfire used but never detonated with stored hits before the kill");

		foreach (var left in new[] { 6f, 9f, 12f })
			Case("BRD", $"Battle Voice + Finale ready, Raging Strikes on cooldown, {left}s left", left, _ =>
			{
				Bard.Gauge.Coda = [Song.WanderersMinuet, Song.MagesBallad, Song.ArmysPaeon];
				Ready(BattleVoicePvE, RadiantFinalePvE);
			}, () => Cast(BattleVoicePvE) == 0 ? "Battle Voice never used"
				: Cast(RadiantFinalePvE) == 0 ? "Radiant Finale never used"
				: Cast(RadiantEncorePvE) > 0 ? null : "Radiant Encore never spent");

		foreach (var sv in new byte[] { 40, 60, 80, 100 })
		foreach (var left in new[] { 2.5f, 5f, 7.5f })
			Case("BRD", $"{sv} Soul Voice, {left}s left", left, _ => Bard.Gauge.SoulVoice = sv,
				() => Cast(ApexArrowPvE) == 0 ? $"Apex Arrow never used; {Bard.Gauge.SoulVoice} Soul Voice at death"
					: sv < 80 || left < 5f || Cast(BlastArrowPvE) > 0 ? null : "Apex from 80+ but Blast Arrow never used");

		foreach (var stacks in new byte[] { 1, 2, 3 })
		foreach (var left in new[] { 3.5f, 6f })
		{
			var lastGain = 0f;
			byte seen = 0;
			Case("BRD", $"Pitch Perfect at {stacks}, {left}s left", left, _ => { Bard.Gauge.Repertoire = seen = stacks; lastGain = Game.Now; },
				() => Cast(PitchPerfectPvE) == 0 ? "Pitch Perfect never used"
					: Bard.Gauge.Repertoire == 0 || Death - lastGain < 1.3f ? null : $"{Bard.Gauge.Repertoire} Repertoire left at death, gained {Death - lastGain:F1}s before it",
				() => { if (Game.Now <= Death && Bard.Gauge.Repertoire > seen) lastGain = Game.Now; seen = Bard.Gauge.Repertoire; });
		}

		foreach (var left in new[] { 4f, 8f, 12f })
			Case("BRD", $"3 Heartbreak charges, {left}s left", left, _ => Ready(HeartbreakShotPvE),
				() => Cast(HeartbreakShotPvE) + Cast(BloodletterPvE) + Cast(RainOfDeathPvE) is var n && n >= 3 ? null : $"{3 - n} Heartbreak charge(s) unspent");

		foreach (var left in new[] { 3f, 8f })
			Case("BRD", $"Sidewinder ready, {left}s left", left, _ => Ready(SidewinderPvE),
				() => Cast(SidewinderPvE) > 0 ? null : "Sidewinder never used");

		// A fresh DoT or an Iron Jaws refresh only beats Burst Shot with more than ~12s of ticks left.
		foreach (var left in new[] { 4f, 8f, 11f })
			Case("BRD", $"DoTs falling off, {left}s left", left, _ =>
			{
				foreach (var h in Game.Hostiles) { h.Statuses[StatusID.Stormbite] = 2.5f; h.Statuses[StatusID.CausticBite] = 2.5f; }
			}, () => Cast(IronJawsPvE) + Cast(StormbitePvE) + Cast(CausticBitePvE) is var n && n == 0 ? null : $"{n} DoT/Iron Jaws cast(s) at the kill");

		Console.WriteLine($"Final-kill dump: {_passed} checks passed, {Holds.Count} holds.");
		if (Holds.Count != 0) throw new InvalidOperationException("Final-kill holds:" + Environment.NewLine + string.Join(Environment.NewLine, Holds));
	}

	private static float _start;

	/// <summary>Runs the case on Pandora (the resource must be spent) and on the P4 pair (FinalKillIn must stay NaN).</summary>
	private static void Case(string job, string name, float left, Action<CustomRotation> setup, Func<string?> spent, Action? afterStep = null)
	{
		foreach (var final in new[] { true, false })
		{
			var rotation = Start(job, final, left);
			setup(rotation);
			_start = Game.Now;
			var finalKillSeen = false;
			Driver.Run(rotation, 0, false, () =>
			{
				if (!float.IsNaN(CustomRotation.FinalKillIn)) finalKillSeen = true;
				afterStep?.Invoke();
			});
			var hold = final ? spent() : finalKillSeen ? "FinalKillIn was set on the P4 pair" : null;
			var label = $"{job} {(final ? "Pandora" : "P4 pair")}: {name}";
			if (hold == null) { _passed++; continue; }
			Holds.Add($"HOLD {label}: {hold}");
			Console.WriteLine(Holds[^1]);
			foreach (var c in Game.Casts.Where(c => c.Time >= _start)) Console.WriteLine($"    {c.Time - Death,6:F2} {c.Action}");
		}
	}

	private static CustomRotation Start(string job, bool final, float left)
	{
		List<TimelineActor> bosses = final ? [Boss("pandora", 0x45AF)] : [Boss("pair a", 0x45A9), Boss("pair b", 0x45AB)];
		var fixture = new Timeline("final kill", job, Death + 1f, bosses, [[0, 100, 100]], [], [], [], [], [], 0,
			[], [], [], job, [job], [], false);
		Game.Reset(fixture, job == "MCH" ? new Machinist(.2f, .2f) : new Bard(.2f, .2f), 0);
		Game.BaseGcd = job == "MCH" ? 2.50f : 2.48f;
		Game.TargetTtkOverride = c => c.BaseId is 0x45AF or 0x45A9 or 0x45AB ? Death - Game.Now : float.NaN;
		if (job == "BRD")
		{
			// A running Minuet: its own Coda, Repertoire procs on the song clock.
			Game.Step(Death - left - .7f);
			if (!BardRotation.TheWanderersMinuetPvE.CanUse(out var minuet, skipTTKCheck: true)) throw new InvalidOperationException("Minuet setup unavailable");
			Game.Execute(minuet);
			Game.Step(.7f);
		}
		else Game.Step(Death - left);
		// Every cooldown the job has is fully drained (a real recast, so charge maths stays consistent); the case readies the one it tests.
		var actions = (job == "MCH" ? typeof(MachinistRotation) : typeof(BardRotation)).GetProperties(BindingFlags.Public | BindingFlags.Static)
			.Select(p => p.GetValue(null)).OfType<BaseAction>();
		foreach (var action in actions)
		{
			var spec = Game.Job.Spec(action.ActionId);
			if (spec.Level > 100 || spec.IsGcd && spec.Recast == 0) continue;
			var cooldown = Game.Cooldown(spec.Id);
			cooldown.FullAt = Game.Now + cooldown.OneCharge * cooldown.MaxCharges - .1f;
		}
		CustomRotation rotation = job == "MCH" ? new MCH_Plan() : new BRD_Plan();
		foreach (var p in rotation.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Where(p => p.PropertyType == typeof(bool) && p.CanWrite && (p.Name.Contains("Medicine") || p.Name.Contains("Meds") || p.Name == "PadFatebreaker")))
			p.SetValue(rotation, false);
		return rotation;
	}

	private static TimelineActor Boss(string key, uint id) => new(key, id, key, [[0, Death]],
		// HP falls steadily (a party's hits), sampled every 0.1s so the recent-HP TTK does not see a staircase.
		Enumerable.Range(0, (int)Death * 10 + 1).Select(i => new[] { i / 10f, (Death - i / 10f) / Death }).ToList(), [[0, 100, 100]], null);

	private static void Ready(params ActionID[] ids)
	{
		foreach (var id in ids) Game.Cooldown(id).FullAt = -1e9f;
	}

	private static int Cast(ActionID id) => Game.Casts.Count(c => c.Time >= _start && c.Time < Death && c.Action.StartsWith(id.ToString()));
	private static double Damage(string name) => Game.DamageByAction.GetValueOrDefault(name);
}
