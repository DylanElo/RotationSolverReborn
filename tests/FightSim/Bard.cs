using Dalamud.Game.ClientState.JobGauge.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Helpers;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>What BardRotation reads from the game's job gauge.</summary>
public sealed class BardGauge
{
	public Song Song;
	public Song LastSong;
	public float SongEnd;
	public byte Repertoire;
	public byte SoulVoice;
	public Song[] Coda = [Song.None, Song.None, Song.None];
	public ushort SongTimer => (ushort)Math.Max(0, (SongEnd - Game.Now) * 1000);
}

/// <summary>
/// Level 100 Bard (7.x). Potencies are measured from a FRU kill (jD6z47YVBXrCbK9F#5): every hit with its crit/DH removed and
/// divided by the buffs up at the time, scaled so Burst Shot = 220. Where that differs from the 7.0 tooltips (Apex, Blast,
/// Resonant, Encore, DoT ticks, Shadowbite) the measured value is used. Procs are random (seeded): Hawk's Eye 35%, Repertoire 80%/3s.
/// </summary>
public sealed class Bard : IJob
{
	public string Abbr => "BRD";
	public static BardGauge Gauge { get; private set; } = new();
	private int _finaleCodas;
	private float _nextSongTick;
	private float _museHaste, _museEnd;
	private readonly float _critRate, _critDamage, _dhRate;

	/// <param name="critRate">From the log's combatantinfo (simulatedCrit).</param>
	public Bard(float critRate, float dhRate)
	{
		Gauge = new BardGauge();
		_critRate = critRate;
		_dhRate = dhRate;
		// Crit damage follows the crit rate at level 100: rate = (200 x (stat - 420) / 2780 + 50) / 1000, damage = 1.4 + rate - 0.05.
		_critDamage = 1.4f + critRate - 0.05f;
	}

	private static readonly Dictionary<ActionID, ActionSpec> Specs = new ActionSpec[]
	{
		new(None, 0, false, Level: 999),
		new(HeavyShotPvE, 97, true), new(BurstShotPvE, 16495, true, 76),
		new(StraightShotPvE, 98, true, 2), new(RefulgentArrowPvE, 7409, true, 70),
		new(VenomousBitePvE, 100, true, 6), new(CausticBitePvE, 7406, true, 64),
		new(WindbitePvE, 113, true, 30), new(StormbitePvE, 7407, true, 64),
		new(IronJawsPvE, 3560, true, 56),
		new(QuickNockPvE, 106, true, 18, Shape: AoeShape.Cone, Radius: 12), new(LadonsbitePvE, 25783, true, 82, Shape: AoeShape.Cone, Radius: 12),
		new(WideVolleyPvE, 36974, true, 25, Shape: AoeShape.CircleOnTarget, Radius: 5), new(ShadowbitePvE, 16494, true, 72, Shape: AoeShape.CircleOnTarget, Radius: 5),
		new(ApexArrowPvE, 16496, true, 80, Shape: AoeShape.CircleOnTarget, Radius: 2), new(BlastArrowPvE, 25784, true, 86, Shape: AoeShape.CircleOnTarget, Radius: 2),
		new(ResonantArrowPvE, 36976, true, 96, Shape: AoeShape.CircleOnTarget, Radius: 5), new(RadiantEncorePvE, 36977, true, 100, Shape: AoeShape.CircleOnTarget, Radius: 5),
		new(RagingStrikesPvE, 101, false, 4, 120, Shape: AoeShape.Self),
		new(BloodletterPvE, 110, false, 12, 15, 3, Group: HeartbreakShotPvE), new(HeartbreakShotPvE, 36975, false, 92, 15, 3),
		new(RainOfDeathPvE, 117, false, 45, 15, 3, AoeShape.CircleOnTarget, 8, HeartbreakShotPvE),
		new(BarragePvE, 107, false, 38, 120, Shape: AoeShape.Self),
		new(MagesBalladPvE, 114, false, 30, 120, Shape: AoeShape.Self),
		new(ArmysPaeonPvE, 116, false, 40, 120, Shape: AoeShape.Self),
		new(TheWanderersMinuetPvE, 3559, false, 52, 120, Shape: AoeShape.Self),
		new(BattleVoicePvE, 118, false, 50, 120, Shape: AoeShape.Self),
		new(RadiantFinalePvE, 25785, false, 90, 110, Shape: AoeShape.Self),
		new(PitchPerfectPvE, 7404, false, 52, 1),
		new(EmpyrealArrowPvE, 3558, false, 54, 15),
		new(SidewinderPvE, 3562, false, 60, 60),
		new(TroubadourPvE, 7405, false, 62, 90, Shape: AoeShape.Self),
		new(NaturesMinnePvE, 7408, false, 66, 120, Shape: AoeShape.Self),
		new(TheWardensPaeanPvE, 3561, false, 35, 45, Shape: AoeShape.Self),
	}.ToDictionary(s => s.Id);

	public ActionSpec Spec(ActionID id) => Specs.TryGetValue(id, out var s) ? s : new ActionSpec(id, 0, false, Level: 999);

	public static uint GameId(ActionID id) => Specs.TryGetValue(id, out var s) ? s.GameId : 0;

	public ActionID Adjusted(ActionID id) => id switch
	{
		HeavyShotPvE => BurstShotPvE,
		StraightShotPvE => RefulgentArrowPvE,
		VenomousBitePvE => CausticBitePvE,
		WindbitePvE => StormbitePvE,
		BloodletterPvE => HeartbreakShotPvE,
		QuickNockPvE => LadonsbitePvE,
		WideVolleyPvE => ShadowbitePvE,
		ApexArrowPvE when Game.Player.HasStatus(true, StatusID.BlastArrowReady) => BlastArrowPvE,
		_ => id,
	};

	// What ActionManager.GetActionStatus refuses for resource or proc reasons.
	public bool GameAllows(ActionID id) => id switch
	{
		RefulgentArrowPvE or ShadowbitePvE => Game.Player.HasStatus(true, StatusID.HawksEye_3861, StatusID.Barrage),
		PitchPerfectPvE => Gauge.Song == Song.WanderersMinuet && Gauge.Repertoire > 0,
		ApexArrowPvE => Gauge.SoulVoice >= 20,
		BlastArrowPvE => Game.Player.HasStatus(true, StatusID.BlastArrowReady),
		ResonantArrowPvE => Game.Player.HasStatus(true, StatusID.ResonantArrowReady),
		RadiantEncorePvE => Game.Player.HasStatus(true, StatusID.RadiantEncoreReady),
		RadiantFinalePvE => Gauge.Coda.Any(c => c != Song.None),
		None or TroubadourPvE or NaturesMinnePvE or TheWardensPaeanPvE => false, // disabled in the user's config / not DPS
		_ => true,
	};

	/// <summary>Army's Paeon: 4% haste per Repertoire; Army's Muse after it, by the stacks it ended on.</summary>
	public float HasteFactor
	{
		get
		{
			if (Gauge.Song == Song.ArmysPaeon) return 1f - 0.04f * Gauge.Repertoire;
			return Game.Now < _museEnd ? 1f - _museHaste : 1f;
		}
	}

	public float SelfMultiplier()
	{
		var p = Game.Player;
		var m = 1f;
		if (p.HasStatus(true, StatusID.RagingStrikes)) m *= 1.15f;
		if (p.HasStatus(true, StatusID.RadiantFinale)) m *= 1f + 0.02f * _finaleCodas;
		var dh = _dhRate + (p.HasStatus(true, StatusID.BattleVoice) ? 0.2f : 0f);
		return m * (1f + _critRate * (_critDamage - 1f)) * (1f + 0.25f * dh);
	}

	public void OnDeath()
	{
		Gauge = new BardGauge();
		_museEnd = 0f;
	}

	public void Advance(float dt)
	{
		if (Gauge.Song != Song.None && Game.Now >= Gauge.SongEnd) EndSong();
		if (Gauge.Song != Song.None && Game.Now >= _nextSongTick)
		{
			_nextSongTick += 3f;
			if (Game.Rng.NextDouble() < 0.8) Repertoire();
		}
		// DoTs tick on the server's 3s clock with the multiplier they snapshotted.
		if ((int)(Game.Now / 3f) != (int)((Game.Now - dt) / 3f))
		{
			foreach (var e in Game.Enemies)
			{
				if (e.Chara.HasStatus(true, StatusID.CausticBite)) Game.Hit(e.Chara, 24, "Caustic Bite (DoT)", _snapshots.GetValueOrDefault((e.Chara.GameObjectId, StatusID.CausticBite), 1f));
				if (e.Chara.HasStatus(true, StatusID.Stormbite)) Game.Hit(e.Chara, 30, "Stormbite (DoT)", _snapshots.GetValueOrDefault((e.Chara.GameObjectId, StatusID.Stormbite), 1f));
			}
		}
	}

	private readonly Dictionary<(ulong, StatusID), float> _snapshots = [];

	private void ApplyDot(IBattleChara target, StatusID dot)
	{
		target.Statuses[dot] = 45f;
		_snapshots[(target.GameObjectId, dot)] = Game.Multiplier();
	}

	private void Repertoire()
	{
		switch (Gauge.Song)
		{
			case Song.WanderersMinuet: Gauge.Repertoire = (byte)Math.Min(3, Gauge.Repertoire + 1); break;
			case Song.MagesBallad: Game.Cooldown(HeartbreakShotPvE).Reduce(7.5f); break;
			case Song.ArmysPaeon: Gauge.Repertoire = (byte)Math.Min(4, Gauge.Repertoire + 1); break;
		}
		Gauge.SoulVoice = (byte)Math.Min(100, Gauge.SoulVoice + 5);
	}

	private void EndSong()
	{
		if (Gauge.Song == Song.ArmysPaeon)
		{
			_museHaste = Gauge.Repertoire switch { 1 => 0.01f, 2 => 0.02f, 3 => 0.04f, 4 => 0.12f, _ => 0f };
			Game.Player.Statuses[StatusID.ArmysEthos] = 30f;
		}
		Game.Player.Statuses.Remove(SongStatus(Gauge.Song));
		Gauge.LastSong = Gauge.Song;
		Gauge.Song = Song.None;
		Gauge.Repertoire = 0;
	}

	private static StatusID SongStatus(Song s) => s switch
	{
		Song.WanderersMinuet => StatusID.TheWanderersMinuet_2216,
		Song.MagesBallad => StatusID.MagesBallad_2217,
		Song.ArmysPaeon => StatusID.ArmysPaeon_2218,
		_ => StatusID.None,
	};

	private void StartSong(Song song)
	{
		if (Gauge.Song != Song.None) EndSong();
		if (Game.Player.Statuses.Remove(StatusID.ArmysEthos)) _museEnd = Game.Now + 10f;
		Gauge.Song = song;
		Gauge.SongEnd = Game.Now + 45f;
		Gauge.Coda[(int)song - 1] = song; // Minstrel's Coda comes with the song
		_nextSongTick = Game.Now + 3f;
		Game.Player.Statuses[SongStatus(song)] = 45f;
	}

	private void HawksEyeChance()
	{
		if (Game.Rng.NextDouble() < 0.35) Game.Player.Statuses[StatusID.HawksEye_3861] = 30f;
	}

	public void Execute(ActionSpec spec, IBattleChara? target, IBattleChara[] affected)
	{
		var p = Game.Player;
		var name = spec.Id.ToString().Replace("PvE", "");
		void HitAll(float potency, float falloff = 0f)
		{
			for (var i = 0; i < affected.Length; i++) Game.Hit(affected[i], i == 0 ? potency : potency * (1f - falloff), name);
		}

		switch (spec.Id)
		{
			case BurstShotPvE: Game.Hit(target!, 220, name); HawksEyeChance(); break;
			case RefulgentArrowPvE:
				var barraged = p.Statuses.Remove(StatusID.Barrage);
				p.Statuses.Remove(StatusID.HawksEye_3861);
				Game.Hit(target!, barraged ? 840 : 280, name);
				break;
			case ShadowbitePvE:
				p.Statuses.Remove(StatusID.Barrage);
				p.Statuses.Remove(StatusID.HawksEye_3861);
				HitAll(220);
				break;
			case LadonsbitePvE: HitAll(140); HawksEyeChance(); break;
			case CausticBitePvE: Game.Hit(target!, 150, name); ApplyDot(target!, StatusID.CausticBite); HawksEyeChance(); break;
			case StormbitePvE: Game.Hit(target!, 100, name); ApplyDot(target!, StatusID.Stormbite); HawksEyeChance(); break;
			case IronJawsPvE:
				Game.Hit(target!, 100, name);
				if (target!.HasStatus(true, StatusID.CausticBite)) ApplyDot(target, StatusID.CausticBite);
				if (target.HasStatus(true, StatusID.Stormbite)) ApplyDot(target, StatusID.Stormbite);
				HawksEyeChance();
				break;
			case ApexArrowPvE:
				Game.Annotate($"{Gauge.SoulVoice} SV");
				HitAll(7.2f * Gauge.SoulVoice); // measured: ~720 at 100 Soul Voice, ~590 at 80-85
				if (Gauge.SoulVoice >= 80) p.Statuses[StatusID.BlastArrowReady] = 10f;
				Gauge.SoulVoice = 0;
				break;
			case BlastArrowPvE: HitAll(700, 0.5f); p.Statuses.Remove(StatusID.BlastArrowReady); break;
			case ResonantArrowPvE: HitAll(660, 0.5f); p.Statuses.Remove(StatusID.ResonantArrowReady); break;
			case RadiantEncorePvE:
				HitAll(_finaleCodas switch { 1 => 700, 2 => 900, _ => 1100 }, 0.5f); // 2 Codas not seen in the log: interpolated
				p.Statuses.Remove(StatusID.RadiantEncoreReady);
				break;
			case RagingStrikesPvE: p.Statuses[StatusID.RagingStrikes] = 20f; break;
			case BattleVoicePvE: p.Statuses[StatusID.BattleVoice] = 20f; break;
			case RadiantFinalePvE:
				_finaleCodas = Gauge.Coda.Count(c => c != Song.None);
				Game.Annotate($"{_finaleCodas} Codas");
				Gauge.Coda = [Song.None, Song.None, Song.None];
				p.Statuses[StatusID.RadiantFinale] = 20f;
				p.Statuses[StatusID.RadiantFinale_2964] = 20f;
				p.Statuses[StatusID.RadiantEncoreReady] = 30f;
				break;
			case BarragePvE:
				p.Statuses[StatusID.Barrage] = 10f;
				p.Statuses[StatusID.ResonantArrowReady] = 30f;
				break;
			case TheWanderersMinuetPvE: StartSong(Song.WanderersMinuet); break;
			case MagesBalladPvE: StartSong(Song.MagesBallad); break;
			case ArmysPaeonPvE: StartSong(Song.ArmysPaeon); break;
			case PitchPerfectPvE:
				Game.Hit(target!, Gauge.Repertoire switch { 1 => 100, 2 => 220, _ => 360 }, name);
				Gauge.Repertoire = 0;
				break;
			case EmpyrealArrowPvE: Game.Hit(target!, 260, name); if (Gauge.Song != Song.None) Repertoire(); break;
			case SidewinderPvE: Game.Hit(target!, 400, name); break;
			case HeartbreakShotPvE: Game.Hit(target!, 180, name); break;
			case RainOfDeathPvE: HitAll(100); break;
		}
	}
}
