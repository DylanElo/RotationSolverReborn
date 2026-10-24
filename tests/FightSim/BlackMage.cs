using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Types;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Helpers;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>What BlackMageRotation reads from the game's job gauge.</summary>
public sealed class BlackMageGauge
{
	public int Element; // -3 (Umbral Ice) .. 3 (Astral Fire), 0 for none
	public int AstralSoul;
	public byte Polyglot, UmbralHearts;
	public bool Paradox;
	public float PolyglotIn = 30f;
	public byte UmbralIceStacks => (byte)Math.Max(0, -Element);
	public byte AstralFireStacks => (byte)Math.Max(0, Element);
	public int AstralSoulStacks => AstralSoul;
	public byte PolyglotStacks => Polyglot;
	public byte UmbralHeartsCount => UmbralHearts;
	public bool IsParadoxActive => Paradox;
	public bool InUmbralIce => Element < 0;
	public bool InAstralFire => Element > 0;
	public bool IsEnochianActive => Element != 0;
	public short EnochianTimer => (short)Math.Clamp(PolyglotIn * 1000f, 0f, 30000f);
}

/// <summary>
/// Level 100 Black Mage (7.5x), ST only (AoE spells are refused). Rules are the 7.x kit as BossMod's BLM module and the top FRU logs
/// show them: Fire costs 800/1600 (hearts spend the doubling), Fire III is free from Firestarter or Umbral Ice, ice spells restore
/// 2500/5000/10000 MP at Umbral Ice 1/2/3 (by the stack when cast), Astral Fire stops the MP tick, Manafont refills MP and gives
/// 3 hearts and Paradox, Transpose flips AF3 -> UI1 / UI -> AF1, an element flip gives Paradox, six Astral Soul (Fire IV, Despair)
/// open Flare Star, Polyglot every 30s of Enochian, Ley Lines haste 0.85. Fire IV/Flare Star cast 2.0, Fire III 3.5 (x spell speed),
/// halved for fire in UI3 / ice in AF3.
/// Damage is not tooltip potency: each action's unit is the median of the logged hits on that action with luck and party buffs
/// divided out, so the sim and the log share one scale and differ only in what was cast, when, and under which buffs.
/// </summary>
public sealed class BlackMage : IJob
{
	public string Abbr => "BLM";
	public float AutoPotency => 0f;
	public float AutoDelay => 1000f;
	public static BlackMageGauge Gauge { get; private set; } = new();
	public static float Mp { get; private set; } = 10000f;
	private readonly float _critRate, _critDamage, _dhRate;
	private readonly Dictionary<string, double> _unit = [];
	private float _tickIn = 3f, _elementLeft, _nextThunderhead;
	private readonly Dictionary<IBattleChara, (float End, float Snapshot, float Next)> _dots = [];

	public BlackMage(float critRate, float dhRate, Timeline timeline)
	{
		Gauge = new BlackMageGauge();
		Mp = 10000f;
		_critRate = critRate;
		_dhRate = dhRate;
		_critDamage = 1.4f + critRate - 0.05f;
		foreach (var g in Hits(timeline).GroupBy(h => h.Key))
		{
			var xs = g.Select(h => h.Base / h.B).OrderBy(x => x).ToList();
			_unit[Norm(g.Key)] = xs[xs.Count / 2];
		}
	}

	private static readonly Dictionary<ActionID, ActionSpec> Specs = new ActionSpec[]
	{
		new(None, 0, false, Level: 999),
		new(FirePvE, 141, true, 2), new(FireIiiPvE, 152, true, 35), new(FireIvPvE, 3577, true, 60),
		new(BlizzardPvE, 142, true), new(BlizzardIiiPvE, 154, true, 35), new(BlizzardIvPvE, 3576, true, 58),
		new(DespairPvE, 16505, true, 72), new(FlareStarPvE, 36989, true, 100), new(ParadoxPvE, 25797, true, 90),
		new(ThunderPvE, 144, true, 6), new(ThunderIiiPvE, 153, true, 45), new(HighThunderPvE, 36986, true, 92), new(XenoglossyPvE, 16507, true, 80), new(FoulPvE, 7422, true, 70, Shape: AoeShape.CircleOnTarget, Radius: 5),
		new(ScathePvE, 156, true, 15), new(UmbralSoulPvE, 16506, true, 35, Shape: AoeShape.Self),
		new(TransposePvE, 149, false, 4, 5, Shape: AoeShape.Self), new(ManafontPvE, 158, false, 30, 100, Shape: AoeShape.Self),
		new(AmplifierPvE, 25796, false, 86, 120, Shape: AoeShape.Self), new(LeyLinesPvE, 3573, false, 52, 120, 2, AoeShape.Self),
		new(TriplecastPvE, 7421, false, 66, 60, 2, AoeShape.Self), new(SwiftcastPvE, 7561, false, 18, 40, Shape: AoeShape.Self),
		new(LucidDreamingPvE, 7562, false, 14, 60, Shape: AoeShape.Self),
	}.ToDictionary(s => s.Id);

	public ActionSpec Spec(ActionID id) => Specs.TryGetValue(id, out var s) ? s : new ActionSpec(id, 0, false, Level: 999);

	public static uint GameId(ActionID id) => Specs.TryGetValue(id, out var s) ? s.GameId : 0;

	public ActionID Adjusted(ActionID id) => id switch
	{
		FirePvE or BlizzardPvE when Gauge.Paradox && Gauge.Element != 0 && (id == FirePvE) == (Gauge.Element > 0) => ParadoxPvE,
		ThunderPvE or ThunderIiiPvE => HighThunderPvE,
		_ => id,
	};

	private static int Hearts => Gauge.UmbralHearts;
	private static int Fire => Math.Max(0, Gauge.Element);
	private static int Ice => Math.Max(0, -Gauge.Element);
	private bool Has(StatusID s) => Game.Player.HasStatus(true, s);

	private int FireCost(int cost) => Ice > 0 ? 0 : Fire > 0 && Hearts == 0 ? cost * 2 : cost;

	/// <summary>The MP the game would charge now (ActionManager.GetActionCost): hearts spend the Astral Fire doubling.</summary>
	public uint MpNeed(ActionID id) => (uint)(Adjusted(id) switch
	{
		DespairPvE => 800,
		ParadoxPvE => Fire > 0 ? 1600 : 0,
		ScathePvE => 800,
		FireIiiPvE => Has(StatusID.Firestarter) ? 0 : FireCost(2000),
		FirePvE or FireIvPvE => FireCost(800),
		_ => 0,
	});

	public bool GameAllows(ActionID id)
	{
		if (id != DespairPvE && MpNeed(id) > Mp + 0.5f) return false;
		return id switch
		{
			FireIvPvE => Fire > 0,
			DespairPvE => Fire > 0 && Mp >= 800,
			FlareStarPvE => Fire > 0 && Gauge.AstralSoul >= 6,
			ParadoxPvE => Gauge.Paradox,
			BlizzardIvPvE => Ice > 0,
			UmbralSoulPvE => Ice > 0,
			TransposePvE => Gauge.Element != 0,
			AmplifierPvE => Gauge.Element != 0,
			XenoglossyPvE or FoulPvE => Gauge.Polyglot > 0,
			HighThunderPvE => Has(StatusID.Thunderhead),
			None => false,
			_ => Specs.ContainsKey(id), // everything else the sim models; AoE and utility actions are not in the table
		};
	}

	public float HasteFactor => Has(StatusID.CircleOfPower) ? 0.85f : 1f;

	private static float BaseCast(ActionID id) => id switch
	{
		FirePvE => 2.0f, FireIiiPvE => 3.5f, FireIvPvE => 2.0f, FlareStarPvE => 2.0f,
		BlizzardPvE => 2.0f, BlizzardIiiPvE => 3.5f, BlizzardIvPvE => 2.0f, ScathePvE => 1.0f,
		_ => 0f,
	};

	/// <summary>Cast time for this press; spends the Swiftcast / Triplecast that makes it instant.</summary>
	public float BeginCast(ActionSpec spec)
	{
		var cast = BaseCast(spec.Id);
		if (cast <= 0f) return 0f;
		if (spec.Id == FireIiiPvE && Has(StatusID.Firestarter)) return 0f;
		var p = Game.Player;
		if (p.Statuses.Remove(StatusID.Swiftcast)) return 0f;
		if (p.HasStatus(true, StatusID.Triplecast))
		{
			if (--p.Stacks[StatusID.Triplecast] == 0) p.Statuses.Remove(StatusID.Triplecast);
			return 0f;
		}
		var fire = spec.Id is FirePvE or FireIiiPvE or FireIvPvE;
		if (Gauge.Element == -3 && fire || Gauge.Element == 3 && spec.Id is BlizzardPvE or BlizzardIiiPvE or BlizzardIvPvE) cast *= 0.5f;
		return cast * Game.BaseGcd / 2.5f * HasteFactor;
	}

	private float Expected(float dhBonus = 0f) => (1f + _critRate * (_critDamage - 1f)) * (1f + 0.25f * (_dhRate + dhBonus));
	public float SelfMultiplier() => Expected();

	public void OnDeath()
	{
		Gauge = new BlackMageGauge();
		Mp = 10000f;
		_dots.Clear();
		_elementLeft = 0f;
	}

	public void Advance(float dt)
	{
		var p = Game.Player;
		if (p.Statuses.ContainsKey(StatusID.Triplecast)) { } else p.Stacks.Remove(StatusID.Triplecast);
		// Measured on the top log: Thunderhead is held until Thunder spends it, then comes back ~5s later (Manafont grants it too).
		if (!p.Statuses.ContainsKey(StatusID.Thunderhead) && Game.Now >= _nextThunderhead && Game.InCombat) p.Statuses[StatusID.Thunderhead] = 999f;
		_tickIn -= dt;
		while (_tickIn <= 0f)
		{
			_tickIn += 3f;
			if (Gauge.Element <= 0 && Game.InCombat) Mp = Math.Min(10000f, Mp + 200f + (Has(StatusID.LucidDreaming) ? 550f : 0f));
		}
		if (Gauge.Element != 0)
		{
			_elementLeft -= dt;
			if (_elementLeft <= 0f) { Gauge.Element = 0; Gauge.AstralSoul = 0; Gauge.PolyglotIn = 30f; }
			else if ((Gauge.PolyglotIn -= dt) <= 0f)
			{
				Gauge.Polyglot = (byte)Math.Min(3, Gauge.Polyglot + 1);
				Gauge.PolyglotIn += 30f;
			}
		}
		foreach (var (target, dot) in _dots.ToList())
		{
			var (end, snapshot, next) = dot;
			if (Game.Now >= end || !Game.Hostiles.Contains(target)) { _dots.Remove(target); continue; }
			while (Game.Now >= next && next < end)
			{
				Game.Hit(target, (float)_unit.GetValueOrDefault("HighThunder#tick"), "High Thunder", snapshot);
				next += 3f;
			}
			_dots[target] = (end, snapshot, next);
		}
	}

	private void Flip(int to)
	{
		if (Math.Sign(Gauge.Element) != Math.Sign(to) && Gauge.Element != 0) { Gauge.Paradox = true; } // Astral Soul survives a flip (the top log's 5.6 soul per Flare Star leaves no room for resets); only the element expiring clears it
		else if (Gauge.Element == 0) Gauge.PolyglotIn = Math.Min(Gauge.PolyglotIn, 30f);
		Gauge.Element = to;
		_elementLeft = 15f;
	}

	private void Restore()
	{
		if (Ice > 0) Mp = Math.Min(10000f, Mp + Ice switch { 1 => 2500f, 2 => 5000f, _ => 10000f });
	}

	private static string Norm(string name) => name.Replace(" ", "").Replace("'", "");

	private static string Pretty(ActionID id) => Regex.Replace(id.ToString().Replace("PvE", ""), "(Ii|Iii|Iv)$", m => m.Value.ToUpperInvariant());

	private void Hit(IBattleChara? target, ActionID id, float scale = 1f, string? name = null)
	{
		if (target == null) return;
		var key = name ?? Pretty(id);
		Game.Hit(target, (float)_unit.GetValueOrDefault(Norm(key)) * scale, key);
	}

	public void Execute(ActionSpec spec, IBattleChara? target, IBattleChara[] affected)
	{
		var p = Game.Player;
		switch (spec.Id)
		{
			case FirePvE:
				Mp -= MpNeed(FirePvE); if (Hearts > 0 && Fire > 0) Gauge.UmbralHearts--;
				Flip(Gauge.Element > 0 ? Math.Min(3, Gauge.Element + 1) : 1); Hit(target, spec.Id); break;
			case FireIiiPvE:
				Mp -= MpNeed(FireIiiPvE); if (Fire > 0 && Hearts > 0 && !p.Statuses.ContainsKey(StatusID.Firestarter)) Gauge.UmbralHearts--;
				p.Statuses.Remove(StatusID.Firestarter); Flip(3); Hit(target, spec.Id); break;
			case FireIvPvE:
				Mp -= MpNeed(FireIvPvE); if (Hearts > 0) Gauge.UmbralHearts--;
				Gauge.AstralSoul = Math.Min(6, Gauge.AstralSoul + 1); _elementLeft = 15f; Hit(target, spec.Id); break;
			case DespairPvE:
				Mp = 0f; Gauge.AstralSoul = Math.Min(6, Gauge.AstralSoul + 1); _elementLeft = 15f; Hit(target, spec.Id); break;
			case FlareStarPvE:
				Gauge.AstralSoul = 0; _elementLeft = 15f; Hit(target, spec.Id); break;
			case ScathePvE: Mp -= 800f; Hit(target, spec.Id); break;
			case ParadoxPvE:
				if (Fire > 0) Mp -= 1600f;
				Gauge.Paradox = false; p.Statuses[StatusID.Firestarter] = 30f; _elementLeft = 15f; Hit(target, spec.Id); break;
			case BlizzardPvE or BlizzardIiiPvE:
				Restore(); Flip(spec.Id == BlizzardIiiPvE ? -3 : Gauge.Element < 0 ? Math.Max(-3, Gauge.Element - 1) : -1); Hit(target, spec.Id); break;
			case BlizzardIvPvE: Restore(); Gauge.UmbralHearts = 3; _elementLeft = 15f; Hit(target, spec.Id); break;
			case UmbralSoulPvE:
				Restore(); Gauge.Element = Math.Max(-3, Gauge.Element - 1); Gauge.UmbralHearts = (byte)Math.Min(3, Gauge.UmbralHearts + 1); _elementLeft = 15f; break;
			case XenoglossyPvE or FoulPvE: Gauge.Polyglot--; Hit(target, spec.Id); break;
			case HighThunderPvE:
				p.Statuses.Remove(StatusID.Thunderhead);
				_nextThunderhead = Game.Now + 5f;
				Hit(target, spec.Id);
				if (target != null)
				{
					var next = _dots.TryGetValue(target, out var old) && Game.Now < old.End ? old.Next : Game.Now + 3f;
					_dots[target] = (Game.Now + 30f, Game.Multiplier(target), next);
					target.Statuses[StatusID.HighThunder] = 30f;
				}
				break;
			case TransposePvE: Flip(Gauge.Element > 0 ? -1 : 1); break;
			case ManafontPvE:
				Mp = 10000f; Gauge.UmbralHearts = 3; Gauge.Paradox = true; p.Statuses[StatusID.Thunderhead] = 999f; break;
			case AmplifierPvE: Gauge.Polyglot = (byte)Math.Min(3, Gauge.Polyglot + 1); break;
			case LeyLinesPvE: p.Statuses[StatusID.LeyLines] = 30f; p.Statuses[StatusID.CircleOfPower] = 30f; break;
			case TriplecastPvE: p.Statuses[StatusID.Triplecast] = 15f; p.Stacks[StatusID.Triplecast] = 3; break;
			case SwiftcastPvE: p.Statuses[StatusID.Swiftcast] = 10f; break;
			case LucidDreamingPvE: p.Statuses[StatusID.LucidDreaming] = 21f; break;
		}
	}

	private IEnumerable<(string Key, float T, double Base, double B)> Hits(Timeline t)
	{
		var casts = t.LoggedCasts.Select(c => (T: c[0].GetSingle(), Name: c[1].GetString()!)).ToList();
		double B(float at)
		{
			var m = 1.0;
			if (casts.Any(c => c.Name.Contains("Gemdraught") && at >= c.T && at < c.T + 30)) m *= Game.PotionMultiplier;
			foreach (var b in t.PartyBuffs) if (at >= b.Start && at < b.End) m *= b.Mult;
			return m;
		}
		foreach (var h in t.LoggedHits)
		{
			var tick = h[5].GetBoolean();
			var name = h[1].GetString()!;
			var at = h[0].GetSingle();
			var baseDamage = h[2].GetDouble() / (h[3].GetBoolean() ? _critDamage : 1) / (h[4].GetBoolean() ? 1.25 : 1);
			yield return (tick ? name + "#tick" : name, at, baseDamage, B(at));
		}
	}

	/// <summary>The log on the sim's scale: each hit with its luck swapped for the expected value.</summary>
	public List<(float T, string Name, double P)> LogPotency(Timeline t)
		=> Hits(t).Where(h => h.Key is not ("Flare" or "Foul")) // AoE-only: the sim is single-target, so the log column leaves them out
			.Select(h => (h.T, h.Key.Replace("#tick", ""), h.Base * Expected())).ToList();
}
