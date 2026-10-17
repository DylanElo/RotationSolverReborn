using Dalamud.Game.ClientState.Objects.Types;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Helpers;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>What MachinistRotation reads from the game's job gauge.</summary>
public sealed class MachinistGauge
{
	public byte Heat;
	public byte Battery;
	public byte LastSummonBatteryPower;
	public float OverheatEnd;
	public float SummonEnd;
	public bool IsOverheated => Game.Now < OverheatEnd;
	public bool IsRobotActive => Game.Now < SummonEnd;
	public short OverheatTimeRemaining => (short)Math.Max(0, (OverheatEnd - Game.Now) * 1000);
	public short SummonTimeRemaining => (short)Math.Max(0, (SummonEnd - Game.Now) * 1000);
}

/// <summary>
/// Level 100 Machinist (7.5x). Potencies are measured from the user's FRU P5 pull jJXkAtKaFQPyH4Wn#27 the way Bard's were: every hit
/// with its crit/DH removed and divided by the party buffs up at the time, scaled so Heated Split Shot = 220. The tools read 660
/// (tooltip 600), Full Metal Field ~980 (900) and Blazing Shot 260 (240 + Overheated). Auto-crit/DH hits (Reassembled, Full Metal
/// Field) take crit damage x 1.25, plus Battle Litany's conversion (1 + 0.1 x (crit damage - 1), +6.4% measured). Wildfire does not
/// crit, snapshots at cast and pays 240 per weaponskill (6 max). The Queen's run is fixed (Collider at +17.4s) and her potency is
/// ~24 per Battery, measured per summon against the Battery rebuilt from the log's casts.
/// </summary>
public sealed class Machinist : IJob
{
	public string Abbr => "MCH";
	public float AutoPotency => 58f; // measured
	public float AutoDelay => 2.67f; // measured
	public static MachinistGauge Gauge { get; private set; } = new();
	private readonly float _critRate, _critDamage, _dhRate;
	private ActionID _lastCombo;
	private float _comboEnd;
	private byte _overheatStacks;

	// Wildfire on one target: weaponskills that land before it detonates, and the multiplier it snapshotted at cast.
	private (IBattleChara Target, float End, int Stacks, float Snapshot)? _wildfire;
	private readonly List<(float At, IBattleChara? Target, float Potency, string Name)> _queenHits = [];

	public Machinist(float critRate, float dhRate)
	{
		Gauge = new MachinistGauge();
		_critRate = critRate;
		_dhRate = dhRate;
		_critDamage = 1.4f + critRate - 0.05f;
	}

	public ActionID LastComboAction => Game.Now < _comboEnd ? _lastCombo : None;
	public float ComboTime => Math.Max(0f, _comboEnd - Game.Now);

	private static readonly Dictionary<ActionID, ActionSpec> Specs = new ActionSpec[]
	{
		new(None, 0, false, Level: 999),
		new(SplitShotPvE, 2866, true), new(HeatedSplitShotPvE, 7411, true, 54),
		new(SlugShotPvE, 2868, true, 2), new(HeatedSlugShotPvE, 7412, true, 60),
		new(CleanShotPvE, 2873, true, 26), new(HeatedCleanShotPvE, 7413, true, 64),
		new(HotShotPvE, 2872, true, 4, 40, Group: AirAnchorPvE), new(AirAnchorPvE, 16500, true, 76, 40),
		new(DrillPvE, 16498, true, 58, 20, 2),
		new(BioblasterPvE, 16499, true, 72, 20, 2, AoeShape.Cone, 12, DrillPvE),
		new(ChainSawPvE, 25788, true, 90, 60, Shape: AoeShape.Line, Radius: 25),
		new(ExcavatorPvE, 36981, true, 96, Shape: AoeShape.CircleOnTarget, Radius: 5),
		new(FullMetalFieldPvE, 36982, true, 100, Shape: AoeShape.CircleOnTarget, Radius: 5),
		new(HeatBlastPvE, 7410, true, 35, GcdRecast: 1.5f), new(BlazingShotPvE, 36978, true, 68, GcdRecast: 1.5f),
		new(AutoCrossbowPvE, 16497, true, 52, Shape: AoeShape.Cone, Radius: 12, GcdRecast: 1.5f),
		new(SpreadShotPvE, 2870, true, 18, Shape: AoeShape.Cone, Radius: 12), new(ScattergunPvE, 25786, true, 82, Shape: AoeShape.Cone, Radius: 12),
		new(ReassemblePvE, 2876, false, 10, 55, 2, AoeShape.Self),
		new(GaussRoundPvE, 2874, false, 15, 30, 3, Group: DoubleCheckPvE),
		new(DoubleCheckPvE, 36979, false, 92, 30, 3, AoeShape.CircleOnTarget, 5),
		new(RicochetPvE, 2890, false, 50, 30, 3, Group: CheckmatePvE),
		new(CheckmatePvE, 36980, false, 92, 30, 3, AoeShape.CircleOnTarget, 5),
		new(HyperchargePvE, 17209, false, 30, 10, Shape: AoeShape.Self),
		new(WildfirePvE, 2878, false, 45, 120),
		new(DetonatorPvE, 16766, false, 45, 1, Shape: AoeShape.Self),
		new(BarrelStabilizerPvE, 7414, false, 66, 120, Shape: AoeShape.Self),
		new(RookAutoturretPvE, 2864, false, 40, 6, Shape: AoeShape.Self, Group: AutomatonQueenPvE),
		new(AutomatonQueenPvE, 16501, false, 80, 6, Shape: AoeShape.Self),
		new(RookOverdrivePvE, 7415, false, 40, 15, Shape: AoeShape.Self, Group: QueenOverdrivePvE),
		new(QueenOverdrivePvE, 16502, false, 80, 15, Shape: AoeShape.Self),
		new(TacticianPvE, 16889, false, 56, 90, Shape: AoeShape.Self),
		new(DismantlePvE, 2887, false, 62, 120),
		new(FlamethrowerPvE, 7418, false, 70, 60, Shape: AoeShape.Self),
	}.ToDictionary(s => s.Id);

	public ActionSpec Spec(ActionID id) => Specs.TryGetValue(id, out var s) ? s : new ActionSpec(id, 0, false, Level: 999);

	public static uint GameId(ActionID id) => Specs.TryGetValue(id, out var s) ? s.GameId : 0;

	public ActionID Adjusted(ActionID id) => id switch
	{
		SplitShotPvE => HeatedSplitShotPvE,
		SlugShotPvE => HeatedSlugShotPvE,
		CleanShotPvE => HeatedCleanShotPvE,
		HotShotPvE => AirAnchorPvE,
		HeatBlastPvE => BlazingShotPvE,
		SpreadShotPvE => ScattergunPvE,
		GaussRoundPvE => DoubleCheckPvE,
		RicochetPvE => CheckmatePvE,
		RookAutoturretPvE => AutomatonQueenPvE,
		RookOverdrivePvE => QueenOverdrivePvE,
		ChainSawPvE when Game.Player.HasStatus(true, StatusID.ExcavatorReady) => ExcavatorPvE,
		WildfirePvE when Game.Player.HasStatus(true, StatusID.Wildfire_1946) => DetonatorPvE,
		_ => id,
	};

	// What ActionManager.GetActionStatus refuses for resource or proc reasons.
	public bool GameAllows(ActionID id) => id switch
	{
		BlazingShotPvE or AutoCrossbowPvE => Gauge.IsOverheated,
		HyperchargePvE => !Gauge.IsOverheated && (Gauge.Heat >= 50 || Game.Player.HasStatus(true, StatusID.Hypercharged)),
		ExcavatorPvE => Game.Player.HasStatus(true, StatusID.ExcavatorReady),
		FullMetalFieldPvE => Game.Player.HasStatus(true, StatusID.FullMetalMachinist),
		DetonatorPvE => _wildfire != null,
		AutomatonQueenPvE => Gauge.Battery >= 50 && !Gauge.IsRobotActive,
		QueenOverdrivePvE => Gauge.IsRobotActive,
		None or TacticianPvE or DismantlePvE or FlamethrowerPvE => false, // mitigation comes from BossMod plans / not DPS
		_ => true,
	};

	public float HasteFactor => 1f;

	private float Expected(float dhBonus = 0f) => (1f + _critRate * (_critDamage - 1f)) * (1f + 0.25f * (_dhRate + dhBonus));

	/// <summary>No damage buffs of its own: the crit/DH expectation only.</summary>
	public float SelfMultiplier() => Expected();

	/// <summary>An auto-crit, auto-DH hit relative to an ordinary one, with Battle Litany's crit-rate conversion.</summary>
	private float Guaranteed()
	{
		var litany = Game.Player.HasStatus(false, StatusID.BattleLitany) ? 1f + 0.1f * (_critDamage - 1f) : 1f;
		return _critDamage * 1.25f * litany / Expected();
	}

	public void OnDeath()
	{
		Gauge = new MachinistGauge();
		_wildfire = null;
		_queenHits.Clear();
		_comboEnd = 0f;
	}

	public void Advance(float dt)
	{
		var p = Game.Player;
		if (Gauge.IsOverheated) p.Statuses[StatusID.Overheated] = Gauge.OverheatEnd - Game.Now;
		else _overheatStacks = 0;
		p.Stacks[StatusID.Overheated] = _overheatStacks;
		if (_wildfire is { } w && Game.Now >= w.End) Detonate();
		for (var i = 0; i < _queenHits.Count; i++)
		{
			var h = _queenHits[i];
			if (Game.Now < h.At) continue;
			if (Game.Target != null) Game.Hit(Game.Target, h.Potency, h.Name); // the Queen hits whatever the player has targeted now
			_queenHits.RemoveAt(i--);
		}
	}

	private void Detonate()
	{
		var (target, _, stacks, snapshot) = _wildfire!.Value;
		_wildfire = null;
		Game.Player.Statuses.Remove(StatusID.Wildfire_1946);
		target.Statuses.Remove(StatusID.Wildfire);
		if (stacks > 0) Game.Hit(target, 240f * stacks / Expected(), "Wildfire", snapshot); // cannot crit or direct hit
	}

	private void Summon()
	{
		var b = Gauge.Battery;
		Game.Annotate($"{b} Battery");
		Gauge.LastSummonBatteryPower = b;
		Gauge.Battery = 0;
		Gauge.SummonEnd = Game.Now + 17.5f;
		_queenHits.Clear();
		foreach (var at in new[] { 6.4f, 7.9f, 9.5f, 11.0f, 12.6f }) _queenHits.Add((Game.Now + at, null, 2.15f * b, "Arm Punch"));
		_queenHits.Add((Game.Now + 14.7f, null, 6.2f * b, "Pile Bunker"));
		_queenHits.Add((Game.Now + 17.4f, null, 6.9f * b, "Crowned Collider"));
	}

	/// <summary>Overdrive: the remaining Arm Punches are lost, the finishers come now (logs: Collider 0.5-2.8s after).</summary>
	private void Overdrive()
	{
		// Pile Bunker starts the finishing sequence; a later order cannot replay that finisher.
		if (!_queenHits.Any(h => h.Name == "Pile Bunker")) return;
		var b = Gauge.LastSummonBatteryPower;
		_queenHits.Clear();
		_queenHits.Add((Game.Now + 0.3f, null, 6.2f * b, "Pile Bunker"));
		_queenHits.Add((Game.Now + 2.8f, null, 6.9f * b, "Crowned Collider"));
		Gauge.SummonEnd = Game.Now + 2.9f;
	}

	private void AddHeat(int n) => Gauge.Heat = (byte)Math.Min(100, Gauge.Heat + n);
	private void AddBattery(int n) => Gauge.Battery = (byte)Math.Min(100, Gauge.Battery + n);

	public void Execute(ActionSpec spec, IBattleChara? target, IBattleChara[] affected)
	{
		var p = Game.Player;
		var name = spec.Id.ToString().Replace("PvE", "");
		var weaponskill = spec.IsGcd;
		var crit = 1f;
		if (weaponskill && (p.Statuses.Remove(StatusID.Reassembled) || spec.Id == FullMetalFieldPvE))
		{
			crit = Guaranteed();
			Game.Annotate("crit/DH");
		}
		void HitAll(float potency, float falloff = 0f)
		{
			for (var i = 0; i < affected.Length; i++) Game.Hit(affected[i], (i == 0 ? potency : potency * (1f - falloff)) * crit, name);
		}
		void Combo(ActionID id)
		{
			_lastCombo = id;
			_comboEnd = Game.Now + 30f;
		}

		switch (spec.Id)
		{
			case HeatedSplitShotPvE: HitAll(220); AddHeat(5); Combo(spec.Id); break;
			case HeatedSlugShotPvE:
				var slug = LastComboAction == HeatedSplitShotPvE;
				HitAll(slug ? 320 : 160); AddHeat(5); if (slug) Combo(spec.Id); else _comboEnd = 0f;
				break;
			case HeatedCleanShotPvE:
				var clean = LastComboAction == HeatedSlugShotPvE;
				HitAll(clean ? 420 : 160); AddHeat(5); if (clean) AddBattery(10);
				_comboEnd = 0f;
				break;
			case DrillPvE: HitAll(660); break;
			case AirAnchorPvE: HitAll(660); AddBattery(20); break;
			case ChainSawPvE: HitAll(660, 0.65f); AddBattery(20); p.Statuses[StatusID.ExcavatorReady] = 30f; break;
			case ExcavatorPvE: HitAll(660, 0.65f); AddBattery(20); p.Statuses.Remove(StatusID.ExcavatorReady); break;
			case FullMetalFieldPvE: HitAll(980, 0.5f); p.Statuses.Remove(StatusID.FullMetalMachinist); break;
			case BioblasterPvE: HitAll(50); break; // ponytail: its DoT is not modelled; FRU never has 3+ targets for it
			case ScattergunPvE: HitAll(160); AddHeat(10); break;
			case BlazingShotPvE or AutoCrossbowPvE:
				HitAll(spec.Id == BlazingShotPvE ? 260 : 180);
				Game.Cooldown(DoubleCheckPvE).Reduce(15f);
				Game.Cooldown(CheckmatePvE).Reduce(15f);
				if (--_overheatStacks == 0) Gauge.OverheatEnd = 0f;
				break;
			case DoubleCheckPvE or CheckmatePvE: HitAll(180, 0.5f); break;
			case ReassemblePvE: p.Statuses[StatusID.Reassembled] = 5f; break;
			case HyperchargePvE:
				if (!p.Statuses.Remove(StatusID.Hypercharged)) Gauge.Heat -= 50;
				Gauge.OverheatEnd = Game.Now + 10f;
				_overheatStacks = 5;
				p.Statuses[StatusID.Overheated] = 10f;
				break;
			case WildfirePvE:
				_wildfire = (target!, Game.Now + 10f, 0, Game.Multiplier());
				target!.Statuses[StatusID.Wildfire] = 10f;
				p.Statuses[StatusID.Wildfire_1946] = 10f;
				break;
			case DetonatorPvE: Detonate(); break;
			case BarrelStabilizerPvE:
				p.Statuses[StatusID.Hypercharged] = 30f;
				p.Statuses[StatusID.FullMetalMachinist] = 30f;
				break;
			case AutomatonQueenPvE: Summon(); break;
			case QueenOverdrivePvE: Overdrive(); break;
		}

		// Wildfire counts the weaponskills that land on its target.
		if (weaponskill && _wildfire is { } w && affected.Contains(w.Target)) _wildfire = w with { Stacks = Math.Min(6, w.Stacks + 1) };
	}

	/// <summary>
	/// The log on the sim's scale (Heated Split Shot = 220). A hit is potency x k x B x luck; in sim units an ordinary hit is
	/// potency x B x E, an auto-crit/DH one potency x B x crit x 1.25 (its log hit already carries Litany's conversion) and
	/// Wildfire potency x B at its cast.
	/// </summary>
	public List<(float T, string Name, double P)> LogPotency(Timeline t)
	{
		var casts = t.LoggedCasts.Select(c => (T: c[0].GetSingle(), Name: c[1].GetString()!)).ToList();
		double B(float at)
		{
			var m = 1.0;
			if (casts.Any(c => c.Name.Contains("Gemdraught") && at >= c.T && at < c.T + 30)) m *= Game.PotionMultiplier;
			foreach (var b in t.PartyBuffs) if (at >= b.Start && at < b.End) m *= b.Mult;
			return m;
		}
		// Reassembled: the first weaponskill cast within 5s of a Reassemble. Full Metal Field always.
		string[] weaponskills = ["Heated Split Shot", "Heated Slug Shot", "Heated Clean Shot", "Drill", "Air Anchor", "Chain Saw", "Excavator", "Full Metal Field", "Blazing Shot", "Scattergun", "Auto Crossbow"];
		var guaranteed = casts.Select((c, i) => (c, i)).Where(x => x.c.Name == "Reassemble")
			.Select(x => casts.Skip(x.i + 1).FirstOrDefault(n => weaponskills.Contains(n.Name) && n.T - x.c.T < 5))
			.Where(n => n.Name != null).ToList();
		var hits = t.LoggedHits.Select(h => (T: h[0].GetSingle(), Name: h[1].GetString()!,
			Base: h[2].GetDouble() / (h[3].GetBoolean() ? _critDamage : 1) / (h[4].GetBoolean() ? 1.25 : 1))).ToList();
		bool Auto(float at, string name) => name == "Full Metal Field" || guaranteed.Any(g => g.Name == name && at >= g.T && at < g.T + 1.5f);
		var k = hits.Where(h => h.Name == "Heated Split Shot" && !Auto(h.T, h.Name)).Average(h => h.Base / (220 * B(h.T)));
		return hits.Select(h => (h.T, h.Name, h.Name == "Wildfire" ? h.Base / k
			: Auto(h.T, h.Name) ? h.Base * _critDamage * 1.25 / k : h.Base * Expected() / k)).ToList();
	}
}
