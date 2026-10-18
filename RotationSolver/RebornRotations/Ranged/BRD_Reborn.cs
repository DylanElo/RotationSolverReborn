namespace RotationSolver.RebornRotations.Ranged;

[Rotation("Reborn", CombatType.PvE, GameVersion = "7.57",
	Description = "Please make sure that the three song times add up to 120 seconds, Wanderers default first song for now.")]
[SourceCode(Path = "main/RebornRotations/Ranged/BRD_Reborn.cs")]

public class BRD_Reborn : BardRotation
{
	#region Config Options

	[Range(1, 5, ConfigUnitType.Seconds, 0.1f)]
	[RotationConfig(CombatType.PvE, Name = "Buff Alignment Timer (Experimental, do not touch if you don't understand it)")]
	public float BuffAlignment { get; set; } = 1;

	[RotationConfig(CombatType.PvE, Name = "Attempt to assign Raging Strikes, Battle Voice, and Radiant Finale to specific ogcd slots (Experimental)")]
	public bool OGCDTimers { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "FRU potion bursts: combat times in seconds, comma separated (replaces the countdown potion in FRU only)")]
	public string PotionTimes { get; set; } = "240,630,970";

	[RotationConfig(CombatType.PvE, Name = "FRU: pad Fatebreaker so it dies at the end of its enrage cast")]
	public bool PadFatebreaker { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Only use DOTs on targets with Boss Icon")]
	public bool DOTBoss { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Skip DOT check for Barrage usage")]
	public bool DOTBarrage { get; set; } = false;

	[Range(80, 100, ConfigUnitType.None, 5)]
	[RotationConfig(CombatType.PvE, Name = "Soul Voice Threshold for Apex Arrow")]
	public float SoulVoiceConfig { get; set; } = 100;

	[Range(1, 45, ConfigUnitType.Seconds, 1)]
	[RotationConfig(CombatType.PvE, Name = "Wanderer's Minuet Uptime")]
	public float WANDTime { get; set; } = 43;

	[Range(0, 45, ConfigUnitType.Seconds, 1)]
	[RotationConfig(CombatType.PvE, Name = "Mage's Ballad Uptime")]
	public float MAGETime { get; set; } = 43;

	[Range(0, 45, ConfigUnitType.Seconds, 1)]
	[RotationConfig(CombatType.PvE, Name = "Army's Paeon Uptime")]
	public float ARMYTime { get; set; } = 34;

	[RotationConfig(CombatType.PvE, Name = "First Song")]
	private protected SongTrack FirstSong { get; set; } = SongTrack.WanderersMinuet;

	[RotationConfig(CombatType.PvE, Name = "Second Song")]
	private protected SongTrack SecondSong { get; set; } = SongTrack.MagesBallad;

	[RotationConfig(CombatType.PvE, Name = "Third Song")]
	private protected SongTrack ThirdSong { get; set; } = SongTrack.ArmysPaeon;

	[RotationConfig(CombatType.PvE, Name = "Use Warden's Paean on other players")]
	public bool BRDEsuna { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Prevent the use of defense abilties during burst")]
	private protected bool BurstDefense { get; set; } = true;

	private float WANDRemainTime => 45 - WANDTime;
	private float MAGERemainTime => 45 - MAGETime;
	private float ARMYRemainTime => 45 - ARMYTime;

	private bool InBurstStatus => (!BattleVoicePvE.EnoughLevel && HasRagingStrikes)
		|| (BattleVoicePvE.EnoughLevel && !RadiantFinalePvE.EnoughLevel && HasRagingStrikes && HasBattleVoice)
		|| (MinstrelsCodaTrait.EnoughLevel && HasRagingStrikes && HasRadiantFinale && HasBattleVoice);

	private bool PaddingFatebreaker => PadFatebreaker && FRUPad.Padding(CurrentTarget, CombatTime);

	/// <summary>
	/// A DoT or Iron Jaws pays back the potency it gives up against a filler after ~8s of ticks (Stormbite: -120 vs Burst Shot,
	/// ~46 per tick with Hawk's Eye procs). Closer to a BossMod downtime it is a loss: top FRU Bards skip them on the CT return.
	/// </summary>
	// ponytail: one break-even for all DoTs (Caustic pays back sooner, ~5s); BossMod downtime only, not target deaths.
	internal const float DoTBreakEven = 8f;

	internal static bool DoTPaysOff(float downtimeIn) => downtimeIn >= DoTBreakEven;

	private static bool UptimeForDoT => DoTPaysOff(BMRDowntimeIn);

	private static bool TargetLivesForDoT(IBattleChara? target)
	{
		var ttk = target?.GetTTK() ?? float.NaN;
		return !(ttk > 0f) || DoTPaysOff(ttk);
	}

	/// <summary>Another job's raid buff is up (or its debuff is on the target): the party is bursting now.</summary>
	private protected static bool PartyRaidBuffUp => Buffs.Any(b => b.JobAbbr != "BRD" && IsUp(b));

	/// <summary>
	/// The first of our burst buffs or an up party raid buff ends within a GCD: the last GCD still under all four. Party buffs
	/// that start before ours also end first. (Two GCDs was tried: FightSim -0.16%, it only clips DoTs.)
	/// </summary>
	private bool BurstEndsNextGcd
		=> StatusHelper.PlayerWillStatusEndGCD(1, 1, true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes)
			|| Buffs.Any(b => b.JobAbbr != "BRD" && (b.Type == StatusType.Buff
				? StatusHelper.PlayerHasStatus(false, b.Ids) && StatusHelper.PlayerWillStatusEndGCD(1, 1, false, b.Ids)
				: CurrentTarget is { } t && t.HasStatus(false, b.Ids) && t.WillStatusEndGCD(1, 1, false, b.Ids)));

	private static bool IsUp(StatusInfo b) => b.Type == StatusType.Buff
		? StatusHelper.PlayerHasStatus(false, b.Ids)
		: CurrentTarget?.HasStatus(false, b.Ids) == true;

	/// <summary>
	/// The party's burst has started, not one early buff: BRD Plan slides its window in only on this. A lone Divination or Mug
	/// up to 8s early would otherwise drag Battle Voice (itself a raid buff) ahead of everyone else's.
	/// </summary>
	private protected static bool PartyBurstStarted
	{
		get
		{
			var others = Buffs.Where(b => b.JobAbbr != "BRD").ToList();
			return SlidesWindow(others.Where(IsUp).Select(b => b.JobAbbr).Distinct().Count(),
				others.Select(b => b.JobAbbr).Distinct().Count());
		}
	}

	/// <summary>Two jobs bursting, or every other buff job when the party has fewer.</summary>
	internal static bool SlidesWindow(int activeJobs, int otherBuffJobs) => activeJobs > 0 && activeJobs >= Math.Min(2, otherBuffJobs);

	/// <summary>BRD Plan: a planned burst window is open (or in its late grace).</summary>
	private protected virtual bool PlanWindowOpen => false;

	/// <summary>
	/// Battle Voice skips its 10s TTK check while the party bursts or a plan window is open: a fresh target (crystals, P4, P5)
	/// reads no TTK for 2.5s after its first HP loss, which averages to 0 and held Battle Voice behind the party's buffs.
	/// The check exists for dying trash, which a live party burst rules out.
	/// </summary>
	private bool BurstSkipsTTK => PartyRaidBuffUp || PlanWindowOpen;

	/// <summary>BRD Plan: Battle Voice (and so Finale and Raging Strikes) waits for the planned window.</summary>
	private protected virtual bool HoldBurst => false;

	/// <summary>Seconds until the planned burst window; only meaningful while HoldBurst.</summary>
	private protected virtual float PlannedBurstIn => 0f;

	/// <summary>
	/// A planned hold long enough that Sidewinder (60s recast) and refilled Soul Voice are back for the window,
	/// even if party buffs pull it in by BRD Plan's 8s slide.
	/// </summary>
	private bool LongPlannedHold => HoldBurst && PlannedBurstIn >= 60f + BRD_Plan.WindowSlide;

	/// <summary>BRD Plan: the potion goes ahead of the window, before Raging Strikes is due.</summary>
	private protected virtual bool PotionBeforeBurst => false;

	/// <summary>FRU potion burst due. BRD Plan reads it from the plan's potion windows instead of PotionTimes.</summary>
	private protected virtual bool PotionNow => DataCenter.IsInFRU && PotionDue(CombatTime, PotionTimes);

	#endregion

	#region Countdown logic
	// Defines logic for actions to take during the countdown before combat starts.
	protected override IAction? CountDownAction(float remainTime)
	{
		// tincture needs to be used on -0.7s exactly
		if (!DataCenter.IsInFRU && remainTime <= 0.7f && UseBurstMedicine(out var act))
		{
			return act;
		}
		return base.CountDownAction(remainTime);
	}
	#endregion

	#region oGCD Logic
	protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
	{
		if (StatusHelper.PlayerHasStatus(false, StatusID.Doom))
		{
			if (TheWardensPaeanPvE.CanUse(out act))
			{
				return true;
			}
		}

		// FRU only: top logs pot 1.5-4s before Raging Strikes in the listed bursts, never on the countdown.
		if (IsBurst && PotionNow && RagingStrikesPvE.EnoughLevel
			&& (RagingStrikesPvE.Cooldown.WillHaveOneChargeGCD(1) || HasRagingStrikes || PotionBeforeBurst) && UseBurstMedicine(out act))
		{
			return true;
		}

		// Earliest burst buff nearly over: Barrage goes now so its Refulgent and Resonant Arrow still land in it. The DoT-proc and
		// Empyreal waits below cost ~100 potency; they starved FRU P4's burst (fresh bosses, DoTs applied inside it) of
		// Barrage for all 20s, ~500 lost. Hawk's Eye still blocks: GeneralGCD spends that proc first under Raging Strikes.
		if (HasRagingStrikes && StatusHelper.PlayerWillStatusEnd(BarrageLastCall, true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes)
			&& !HasHawksEye && Repertoire != 3 && BarragePvE.CanUse(out act))
		{
			return true;
		}

		if (nextGCD.IsTheSameTo(true, StraightShotPvE, VenomousBitePvE, WindbitePvE, IronJawsPvE))
		{
			return base.EmergencyAbility(nextGCD, out act);
		}
		else if (!RagingStrikesPvE.EnoughLevel || HasRagingStrikes)
		{
			if ((EmpyrealArrowPvE.Cooldown.IsCoolingDown || !EmpyrealArrowPvE.EnoughLevel) && Repertoire != 3)
			{
				if (!StatusHelper.PlayerHasStatus(true, StatusID.HawksEye_3861) && BarragePvE.CanUse(out act))
				{
					return true;
				}
			}
		}

		return base.EmergencyAbility(nextGCD, out act);
	}

	// ponytail: fixed combat times drift with kill speed; ±20s plus the Raging Strikes gate absorbs normal variance.
	/// <summary>Drain two stacks before Empyreal and a coincident song tick can overflow Repertoire.</summary>
	internal static bool TwoStackPPBeforeEmpyreal(int repertoire, bool empyrealSoon)
		=> repertoire == 2 && empyrealSoon;

	internal static bool PotionDue(float combatTime, string times)
	{
		foreach (var entry in times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (float.TryParse(entry, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)
				&& Math.Abs(combatTime - t) <= 20f)
			{
				return true;
			}
		}
		return false;
	}

	[RotationDesc(ActionID.TheWardensPaeanPvE)]
	protected override bool DispelAbility(IAction nextGCD, out IAction? action)
	{
		if (BRDEsuna && TheWardensPaeanPvE.CanUse(out action))
		{
			return true;
		}
		return base.DispelAbility(nextGCD, out action);
	}

	[RotationDesc(ActionID.NaturesMinnePvE)]
	protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
	{
		if (NaturesMinnePvE.CanUse(out act))
		{
			return true;
		}
		return base.HealSingleAbility(nextGCD, out act);
	}

	[RotationDesc(ActionID.TroubadourPvE)]
	protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
	{
		if ((!BurstDefense || (BurstDefense && !InBurstStatus)) && TroubadourPvE.CanUse(out act))
		{
			return true;
		}
		return base.DefenseAreaAbility(nextGCD, out act);
	}

	private uint _lastTargetBaseId;
	private float _lastTargetHp;

	/// <summary>Per enemy (the FRU P4 pair each carry their own DoTs): last seen Stormbite time left and the multiplier it snapshotted.</summary>
	private readonly Dictionary<ulong, (float Remain, float Snapshot)> _dots = [];

	/// <summary>Codas the current (or next) own Radiant Finale consumed; 0 when unknown.</summary>
	private int _finaleCodas;

	/// <summary>Radiant Finale is 2% per Coda; unknown (another Bard's, or none seen yet) reads as 3 Codas.</summary>
	internal static float FinaleWeight(int codas) => 0.02f * (codas is >= 1 and <= 3 ? codas : 3);

	/// <summary>
	/// Approximate damage each tracked buff adds to a DoT tick. Crit and direct-hit buffs are converted at ~60% crit / 25% DH damage.
	/// </summary>
	// ponytail: fixed values; potion at ~6% from main stat. Tune here if logs disagree. Finale is weighted by Codas, see FinaleWeight.
	private static readonly Dictionary<string, float> BuffWeights = new()
	{
		["Divination"] = 0.06f, ["Battle Voice"] = 0.05f, ["Technical Finish"] = 0.05f,
		["Battle Litany"] = 0.06f, ["Brotherhood"] = 0.05f, ["Mug"] = 0.05f, ["Dokumori"] = 0.05f, ["Starry Muse"] = 0.05f,
		["Arcane Circle"] = 0.03f, ["Embolden"] = 0.05f, ["Chain Stratagem"] = 0.06f, ["Searing Light"] = 0.05f,
	};

	/// <summary>Burst buff time left that still fits Barrage's Refulgent and Resonant Arrow (3 GCDs).</summary>
	private const float BarrageLastCall = 7.5f;

	private const float RagingStrikesWeight = 0.15f;
	private const float MedicatedWeight = 0.06f;

	/// <summary>
	/// Damage multiplier a DoT applied on the target now would snapshot: tracked raid buffs (own Battle Voice and Finale included,
	/// target debuffs read on that target), Raging Strikes, potion.
	/// </summary>
	private float BuffSnapshotOn(IBattleChara? target)
	{
		var multiplier = 1f;
		foreach (var b in Buffs)
		{
			var active = b.Type == StatusType.Buff ? StatusHelper.PlayerHasStatus(false, b.Ids) : target?.HasStatus(false, b.Ids) == true;
			if (!active) continue;
			if (b.Name == "Radiant Finale") multiplier *= 1f + FinaleWeight(_finaleCodas);
			else if (BuffWeights.TryGetValue(b.Name, out var weight)) multiplier *= 1f + weight;
		}
		if (HasRagingStrikes) multiplier *= 1f + RagingStrikesWeight;
		if (StatusHelper.PlayerHasStatus(true, StatusID.Medicated)) multiplier *= 1f + MedicatedWeight;
		return multiplier;
	}

	/// <summary>
	/// Records, per enemy, the multiplier present when our Stormbite was last applied or refreshed (its timer jumped up).
	/// Every hostile is watched, so switching between the P4 pair keeps both snapshots. A DoT first seen already running reads 1.
	/// </summary>
	private void TrackDotSnapshots()
	{
		// Codas only reach 0 when Finale consumes them, so the last nonzero count before the buff is the count it used.
		var codas = Coda.Count(c => c != Song.None);
		if (!HasRadiantFinale && codas > 0) _finaleCodas = codas;

		if (!InCombat)
		{
			_dots.Clear();
			return;
		}

		foreach (var target in AllHostileTargets)
		{
			var remain = target.StatusTime(true, StatusID.Stormbite, StatusID.Windbite);
			_dots[target.GameObjectId] = (remain, NextDotSnapshot(
				_dots.TryGetValue(target.GameObjectId, out var dot) ? dot : null, remain, () => BuffSnapshotOn(target)));
		}
	}

	/// <summary>One enemy's snapshot after a frame: re-read on a refresh, kept otherwise, 1 when first seen.</summary>
	internal static float NextDotSnapshot((float Remain, float Snapshot)? seen, float remain, Func<float> current)
		=> seen is not { } dot ? 1f : DotRefreshed(dot.Remain, remain) ? current() : dot.Snapshot;

	private float DotSnapshotOn(IBattleChara? target)
		=> target != null && _dots.TryGetValue(target.GameObjectId, out var dot) ? dot.Snapshot : 1f;

	internal static bool DotRefreshed(float before, float now) => now > before + 2f;

	private const float DotPotencyPerSecond = 15f; // Stormbite 25 + Caustic Bite 20 every 3s
	private const float IronJawsLoss = 120f;       // Iron Jaws 100 against Burst Shot 220
	private const float DotDuration = 45f;

	/// <summary>
	/// The burst-end Iron Jaws re-applies both DoTs at the current multiplier for 45s. Gain: the remaining time at (now - old snapshot),
	/// plus the rest of the 45s at (now - 1), which without it would tick after an unbuffed refresh. Cost: the clipped remaining time
	/// brings the next Iron Jaws forward, ~IronJawsLoss * remain / 45. Unknown snapshot reads 1 (unbuffed).
	/// Both spans stop at target death or the next downtime, whichever comes first (unknown values keep the 45s horizon).
	/// If the old DoTs outlive the target, no later refresh is saved: pay the full buffed filler loss now.
	/// </summary>
	internal static bool BurstEndRefreshPays(float now, float dotSnapshot, float remain, float ttk = float.NaN, float downtimeIn = float.PositiveInfinity)
	{
		var horizon = Math.Min(ttk > 0f ? Math.Min(ttk, DotDuration) : DotDuration,
			downtimeIn >= 0f ? downtimeIn : DotDuration);
		var clipped = Math.Min(remain, horizon);
		var cost = horizon <= remain ? IronJawsLoss * now : IronJawsLoss * remain / DotDuration;
		return DotPotencyPerSecond * (clipped * (now - dotSnapshot) + (horizon - clipped) * (now - 1f)) > cost;
	}

	/// <summary>Cast before Return becomes Stun; the old 7-10s interval was entirely inside the lockout.</summary>
	internal const float StandaloneFinaleLead = 12.2f;

	/// <summary>Finale's 20s buff should still cover most of the return: no earlier than this before it.</summary>
	internal const float StandaloneFinaleLatest = 15f;

	/// <summary>
	/// FRU CT (P4 pair gone): a Finale without Battle Voice, then Encore on the return (top Bards: Xw 12:20, Aqualyy 12:22, ~13s
	/// before it, back for P5 at 14:13/14:16). Reserve Paeon before CT so its Coda can be rebuilt after Finale, before the P5 lockout.
	/// </summary>
	/// <summary>
	/// Seconds until the CT return, from our own debuffs (2 pulls): the bosses came back 19.1-19.3s after Return (7s) landed and
	/// 3.1-3.2s after the Stun that follows it ended. BossMod's estimate ran ~5.5s early, which closed the Finale window before the
	/// game allowed actions again.
	/// </summary>
	internal static float CtReturnIn(float returnLeft, float stunLeft, float bossModEstimate)
		=> returnLeft > 0f ? returnLeft + 12.2f : stunLeft > 0f ? stunLeft + 3.15f : bossModEstimate;

	internal static bool ShouldUseStandaloneFinale(bool fruCtDowntime, float downtimeLeft, int codas, bool battleVoiceCooling)
		=> fruCtDowntime && downtimeLeft is >= StandaloneFinaleLead and <= StandaloneFinaleLatest && codas >= 2 && battleVoiceCooling;

	private SongTrack GetNextSongTrack(SongTrack currentSong)
	{
		if (currentSong == FirstSong)
		{
			return SecondSong;
		}

		return currentSong == SecondSong ? ThirdSong : FirstSong;
	}

	private static Song SongTrackToSong(SongTrack track)
	{
		return track switch
		{
			SongTrack.WanderersMinuet => Song.WanderersMinuet,
			SongTrack.MagesBallad => Song.MagesBallad,
			SongTrack.ArmysPaeon => Song.ArmysPaeon,
			_ => Song.None,
		};
	}

	private bool _ctFinaleSelected;
	private bool _ctFinaleConfirmed;
	private bool _ctRecoveryPpDrained;
	private bool _p5MinuetReserved;
	private float _ctMinuetNotBefore;
	private bool DefaultCtSongOrder => FirstSong == SongTrack.WanderersMinuet
		&& SecondSong == SongTrack.MagesBallad && ThirdSong == SongTrack.ArmysPaeon;
	private bool AliveP4Pair => DataCenter.IsInFRU
		&& (CurrentTarget is { BaseId: 0x45A9 or 0x45AB } pair ? pair.GetHealthRatio() > 0.05f
			: CurrentTarget == null && _lastTargetBaseId is 0x45A9 or 0x45AB && _lastTargetHp > 0.05f);
	private bool WaitForP5Minuet => _p5MinuetReserved && DefaultCtSongOrder && DataCenter.IsInFRU
		&& (CombatTime < _ctMinuetNotBefore || (DataCenter.AllHostileTargets.Count == 0 && BMRDowntimeEndIn is > 0f and < float.MaxValue));
	private bool ReserveCtPaeon => !_ctFinaleSelected && !_p5MinuetReserved && ShouldReserveCtPaeon(IsBurst && RadiantFinalePvE.EnoughLevel && RadiantFinalePvE.Config.IsEnabled,
		DefaultCtSongOrder, AliveP4Pair, DataCenter.AllHostileTargets.Count == 0,
		BMRDowntimeIn, BMRDowntimeEndIn, RadiantFinalePvE.Cooldown.RecastTimeRemain, HasRadiantFinale)
		&& TheWanderersMinuetPvE.Cooldown.RecastTimeRemain <= BMRDowntimeEndIn;

	internal static bool ShouldReserveCtPaeon(bool enabled, bool defaultOrder, bool alivePair, bool noHostiles,
		float downtimeIn, float downtimeEndIn, float finaleRecast, bool finaleActive)
		=> enabled && defaultOrder && alivePair && !finaleActive
			// CT lasts under a minute; the longer P5 transition must release Paeon even with stale cached pair HP.
			&& (downtimeIn is >= 0f and <= 20f || noHostiles) && downtimeEndIn is > 0f and < float.MaxValue
			&& (!noHostiles || downtimeEndIn < 60f)
			&& finaleRecast <= downtimeEndIn;

	internal static bool CanRecoverCtPaeon(float recast, float returnIn)
		=> returnIn is > 0f and < float.MaxValue && recast <= returnIn;

	internal static bool ShouldReserveP4BurstWeave(bool fruPair, bool burst, bool hold, float windowIn, bool buffsReady)
		=> fruPair && burst && hold && windowIn is > 0f and <= 1f && buffsReady;

	internal static bool ShouldReserveSongWeave(bool p1Minuet, bool balladReady, float downtimeIn, float downtimeEndIn, float gcd)
		=> p1Minuet && balladReady && downtimeIn >= 0f && downtimeIn <= gcd
			&& downtimeEndIn is > 0f and < float.MaxValue;

	private bool ReserveFruSongWeave => DataCenter.IsInFRU && DefaultCtSongOrder
		&& CurrentTarget is { BaseId: 0x459B } && Song == Song.WanderersMinuet
		&& ShouldReserveSongWeave(true, MagesBalladPvE.CanUse(out _), BMRDowntimeIn, BMRDowntimeEndIn, GCDTime(1));

	internal static float CodaSuccessorStart(float minuetRecast, float downtimeLeft, bool waitForUptime)
	{
		if (!waitForUptime || downtimeLeft is not (> 0f and < float.MaxValue)) return minuetRecast;
		var start = Math.Max(minuetRecast, downtimeLeft);
		// Waiting at most one GCD for Minuet lets Ballad's full 120s recast finish after its 45s song.
		return start is >= 72.5f and < 75f ? 75f : start;
	}

	private bool ReserveCtCodaWeave => _ctFinaleConfirmed && DefaultCtSongOrder && AliveP4Pair
		&& Array.IndexOf(Coda, Song.MagesBallad) < 0
		&& MagesBalladPvE.Cooldown.RecastTimeRemain > 0f
		&& MagesBalladPvE.Cooldown.RecastTimeRemain <= GCDTime(2);
	private IBaseAction SongTrackAction(SongTrack track) => track switch
	{
		SongTrack.MagesBallad => MagesBalladPvE,
		SongTrack.ArmysPaeon => ArmysPaeonPvE,
		_ => TheWanderersMinuetPvE,
	};

	private bool TrySongTrackAction(SongTrack track, out IAction? act)
	{
		act = null;
		return track switch
		{
			SongTrack.WanderersMinuet => !WaitForP5Minuet && TheWanderersMinuetPvE.CanUse(out act),
			SongTrack.MagesBallad => MagesBalladPvE.CanUse(out act),
			SongTrack.ArmysPaeon => !ReserveCtPaeon && ArmysPaeonPvE.CanUse(out act),
			_ => false,
		};
	}

	private void UpdateCtRecovery()
	{
		if (!InCombat || HasBattleVoice)
		{
			_ctFinaleSelected = _ctFinaleConfirmed = _ctRecoveryPpDrained = _p5MinuetReserved = false;
			_ctMinuetNotBefore = 0f;
		}
		if (_ctFinaleSelected && HasRadiantFinale && !HasBattleVoice) _ctFinaleConfirmed = true;
		if (_ctFinaleConfirmed && IsLastAbility(ActionID.PitchPerfectPvE)) _ctRecoveryPpDrained = true;
	}

	protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
	{
		UpdateCtRecovery();
		if (CurrentTarget is { } target)
		{
			_lastTargetBaseId = target.BaseId;
			_lastTargetHp = target.GetHealthRatio();
		}

		// CT, not the P4 kill: both leave no hostiles, but in CT the pair vanished alive.
		var ctDowntime = DataCenter.IsInFRU && _lastTargetBaseId is 0x45A9 or 0x45AB && _lastTargetHp > 0.05f && DataCenter.AllHostileTargets.Count == 0;
		var returnLeft = StatusHelper.PlayerStatusTime(false, StatusID.Return);
		var stunLeft = StatusHelper.PlayerStatusTime(false, StatusID.Stun_4163);
		var ctReturnIn = CtReturnIn(returnLeft, stunLeft, BMRDowntimeEndIn);
		if (ActionTracer.Enabled && ActionTracer.Verbose && DataCenter.IsInFRU && DataCenter.AllHostileTargets.Count == 0)
		{
			// ponytail: diagnostic for the CT Finale (10-15 21:49: the debuff timing fell back to BossMod); drop once it fires.
			ActionTracer.Note($"CT ct={ctDowntime} last={_lastTargetBaseId:X} hp={_lastTargetHp:F2} return={returnLeft:F2} stun={stunLeft:F2} "
				+ $"bmr={BMRDowntimeEndIn:F2} est={ctReturnIn:F2} codas={Coda.Count(s => s != Song.None)} bvCd={BattleVoicePvE.Cooldown.IsCoolingDown} "
				+ $"statuses={string.Join(",", Player?.StatusList.Where(s => s.StatusId != 0).Select(s => $"{s.StatusId}:{s.RemainingTime:F1}") ?? [])}");
		}

		if (InCombat && IsBurst && ShouldUseStandaloneFinale(ctDowntime, ctReturnIn,
				Coda.Count(s => s != Song.None), BattleVoicePvE.Cooldown.IsCoolingDown)
			// No hostiles reads as 0s TTK, under Finale's 10s threshold: without the skip this never fired.
			&& RadiantFinalePvE.CanUse(out act, skipTTKCheck: true))
		{
			// Coda recovery needs Minuet in CT (10-17: Ballad/Paeon there blocked the Finale in both pulls); otherwise a plain
			// Finale as in the 10-16 kill, and CodaGrab refills Codas in the P4->P5 gap.
			_ctFinaleSelected = DefaultCtSongOrder && Song == Song.WanderersMinuet
				&& CanRecoverCtPaeon(ArmysPaeonPvE.Cooldown.RecastTimeRemain, ctReturnIn);
			_ctRecoveryPpDrained = false;
			return true;
		}

		if (InCombat && SongAbility(out act))
		{
			return true;
		}

		return base.GeneralAbility(nextGCD, out act);
	}

	/// <summary>
	/// Song switches. RotationSolver checks AttackAbility before GeneralAbility, so songs living only in GeneralAbility
	/// lost every contested weave: the FRU clear switched Army's to Wanderer's 8-11s late, burst in Army's Paeon, and
	/// the drift compounded until P5's first burst ran in Mage's Ballad (top logs: Wanderer's first, then buffs).
	/// </summary>
	private bool SongAbility(out IAction? act)
	{
		act = null;
		// Attack selection also reaches this path: confirmation must not wait for GeneralAbility to win a weave.
		UpdateCtRecovery();
		if (CodaGrab(out act))
		{
			return true;
		}
		// Rebuilding is allowed here; a filler song must not replace the Minuet reserved for Pandora.
		if (WaitForP5Minuet) return false;

		// Leaving Minuet discards Repertoire: drain a usable PP first. An unusable PP never delays the song.
		if (Song == Song.WanderersMinuet && Repertoire > 0 && SongEndAfter(WANDRemainTime)
			&& PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
		{
			return true;
		}

		if (Song != Song.None)
		{
			var currentSongTrack = Song switch
			{
				Song.WanderersMinuet => SongTrack.WanderersMinuet,
				Song.MagesBallad => SongTrack.MagesBallad,
				Song.ArmysPaeon => SongTrack.ArmysPaeon,
				_ => SongTrack.WanderersMinuet,
			};

			var nextSongTrack = GetNextSongTrack(currentSongTrack);
			var nextSong = SongTrackToSong(nextSongTrack);

			var remainTime = currentSongTrack switch
			{
				SongTrack.WanderersMinuet => WANDRemainTime,
				SongTrack.MagesBallad => MAGERemainTime,
				SongTrack.ArmysPaeon => ARMYRemainTime,
				_ => 0,
			};

			var currentSongAction = currentSongTrack switch
			{
				SongTrack.MagesBallad => MagesBalladPvE,
				SongTrack.ArmysPaeon => ArmysPaeonPvE,
				_ => TheWanderersMinuetPvE,
			};

			// Drifted order (10-17: Paeon skipped in P2 Diamond Dust -> Minuet, Paeon, Ballad): the fixed-order next song is on
			// cooldown, so nothing cuts this one, Minuet starts after the burst Finale (2 Codas) and is too late to play in CT.
			// Minuet comes back on the 2-min burst: take it as soon as it is ready.
			if (FirstSong == SongTrack.WanderersMinuet && currentSongTrack != SongTrack.WanderersMinuet
				&& nextSongTrack != SongTrack.WanderersMinuet && HasHostilesInMaxRange
				&& currentSongAction.Cooldown.ElapsedAfter(10) && !TrySongTrackAction(nextSongTrack, out _)
				// ...and only if that song is back when Minuet ends (one GCD of slack: the no-song wait covers it),
				// else the cut just moves the gap after Minuet.
				&& SongTrackAction(nextSongTrack).Cooldown.WillHaveOneCharge(45f + GCDTime(1))
				&& TrySongTrackAction(SongTrack.WanderersMinuet, out act))
			{
				return true;
			}

			// The gauge can report the new song with a 0 timer for a moment after the cast; two FRU openers
			// switched Wanderer's to Ballad 0.5s in. Never switch in a song's first 10s.
			if (currentSongAction.Cooldown.ElapsedAfter(10) && TrySongTrackAction(nextSongTrack, out act) && SongEndAfter(remainTime))
			{
				if (nextSong == Song.WanderersMinuet)
				{
					if (Song != Song.None || StatusHelper.PlayerHasStatus(true, StatusID.ArmysEthos))
					{
						if (!IsLastAbility(ActionID.ArmysPaeonPvE) && !IsLastAbility(ActionID.MagesBalladPvE))
						{
							return true;
						}
					}
				}
				else if (nextSong == Song.MagesBallad)
				{
					if (Song == Song.WanderersMinuet && (Repertoire == 0 || !HasHostilesInMaxRange || SongEndAfter(1)))
					{
						if (!IsLastAbility(ActionID.ArmysPaeonPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
						{
							return true;
						}
					}
					else if (Song == Song.ArmysPaeon && SongEndAfterGCD(2) && TheWanderersMinuetPvE.Cooldown.IsCoolingDown)
					{
						if (!IsLastAbility(ActionID.ArmysPaeonPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
						{
							return true;
						}
					}
				}
				else if (nextSong == Song.ArmysPaeon)
				{
					if (TheWanderersMinuetPvE.EnoughLevel)
					{
						if (SongEndAfter(remainTime))
						{
							if (!IsLastAbility(ActionID.MagesBalladPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
							{
								return true;
							}
						}

						if (SongEndAfter(2) && MagesBalladPvE.Cooldown.IsCoolingDown && Song == Song.WanderersMinuet)
						{
							if (!IsLastAbility(ActionID.MagesBalladPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
							{
								return true;
							}
						}
					}
					else if (SongEndAfter(2))
					{
						if (!IsLastAbility(ActionID.MagesBalladPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
						{
							return true;
						}
					}
				}
			}
		}

		if (Song == Song.None)
		{
			// After a downtime, a Minuet a few seconds out beats starting Paeon: Paeon there put every P5 burst in Army's.
			if (FirstSong == SongTrack.WanderersMinuet && TheWanderersMinuetPvE.EnoughLevel
				&& TheWanderersMinuetPvE.Cooldown.IsCoolingDown && TheWanderersMinuetPvE.Cooldown.RecastTimeRemain < 8f)
			{
				return false;
			}

			// The song just ended a moment before its successor is back: wait rather than start the wrong one. 10-17: Minuet
			// ended 1.4s before Ballad, Paeon went instead and the order stayed flipped all pull (later Finales at 2 Codas).
			var expected = LastSong switch
			{
				Song.WanderersMinuet => (IBaseAction?)SongTrackAction(GetNextSongTrack(SongTrack.WanderersMinuet)),
				Song.MagesBallad => SongTrackAction(GetNextSongTrack(SongTrack.MagesBallad)),
				Song.ArmysPaeon => SongTrackAction(GetNextSongTrack(SongTrack.ArmysPaeon)),
				_ => null,
			};
			if (HasHostilesInMaxRange && expected is { EnoughLevel: true } && expected.Cooldown.IsCoolingDown
				&& expected.Cooldown.RecastTimeRemain < GCDTime(1))
			{
				return false;
			}

			if (TrySongTrackAction(FirstSong, out act))
			{
				return true;
			}

			if (TrySongTrackAction(SecondSong, out act))
			{
				return true;
			}

			if (TrySongTrackAction(ThirdSong, out act))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Top FRU logs cast Mage's Ballad then Army's Paeon back to back as the P4->P5 downtime starts: both songs would
	/// expire in downtime anyway, and the P5 Radiant Finale then has all three Codas. Only when Wanderer's is back
	/// by the end of downtime; the P1->P2 gap is too short and would leave no song for a minute after it.
	/// </summary>
	private bool CodaGrab(out IAction? act)
	{
		act = null;
		// Only a confirmed standalone CT Finale permits rebuilding Codas while the pair is targetable.
		var coda = Coda;
		// Rebuild both Codas even after Finale expires or Minuet is a few seconds late for the P5 return.
		var ctPending = _ctFinaleConfirmed && DefaultCtSongOrder && !HasBattleVoice
			&& (Array.IndexOf(coda, Song.ArmysPaeon) < 0 || Array.IndexOf(coda, Song.MagesBallad) < 0);
		var ctRecovery = ctPending && AliveP4Pair;
		if (!RadiantFinalePvE.EnoughLevel || (DataCenter.AllHostileTargets.Count > 0 && !ctRecovery)) return false;
		var downtimeLeft = BMRDowntimeEndIn;
		// CT is shorter than 60s; latch P5's ~77s transition even if cached pair HP is stale or CT Finale was skipped.
		if (_lastTargetBaseId is 0x45A9 or 0x45AB && downtimeLeft is > 60f and < float.MaxValue)
			_p5MinuetReserved = true;
		if (!ctPending && !ShouldGrabCodas(downtimeLeft, TheWanderersMinuetPvE.Cooldown.IsCoolingDown ? TheWanderersMinuetPvE.Cooldown.RecastTimeRemain : 0f))
		{
			return false;
		}
		if (ctRecovery && !_ctRecoveryPpDrained && Song == Song.WanderersMinuet && Repertoire > 0
			&& PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true)) return true;
		// CT recovery only rebuilds the Codas: a running Minuet (and its Repertoire) is kept to its last GCD, as top logs do.
		// Switching at the Stun threw away ~20s of Minuet and its Pitch Perfects when the pair returned (FightSim zX9LjrWh3v1F8AMQ#12 P4 -3.4%).
		if (ctRecovery && Song == Song.WanderersMinuet && !SongEndAfterGCD(1)) return false;
		var minuetRecast = TheWanderersMinuetPvE.Cooldown.IsCoolingDown ? TheWanderersMinuetPvE.Cooldown.RecastTimeRemain : 0f;
		var waitForUptime = WaitForP5Minuet;
		var successorStart = CodaSuccessorStart(minuetRecast, downtimeLeft, waitForUptime);
		if (Array.IndexOf(coda, Song.MagesBallad) < 0 && SuccessorBackInTime(successorStart, 1) && MagesBalladPvE.CanUse(out act))
		{
			if (waitForUptime && successorStart > Math.Max(minuetRecast, downtimeLeft))
				_ctMinuetNotBefore = CombatTime + successorStart;
			return true;
		}
		return !ReserveCtPaeon && Array.IndexOf(coda, Song.ArmysPaeon) < 0
			&& SuccessorBackInTime(successorStart, 2) && ArmysPaeonPvE.CanUse(out act);
	}

	internal static bool ShouldGrabCodas(float downtimeLeft, float minuetRecast)
		=> downtimeLeft is > 0f and < float.MaxValue && minuetRecast <= downtimeLeft + 2.5f;

	/// <summary>
	/// A grabbed song is on its 120s recast; it must be back when its turn comes after Minuet (position 1 = Ballad,
	/// 2 = Paeon), or the songs run out in uptime. FRU P4->P5: Minuet back in 76s, ends 121s, Ballad back at 120s.
	/// </summary>
	// ponytail: assumes the default Minuet -> Ballad -> Paeon order and full 45s songs.
	// The successor must actually be ready; a GCD queue allowance would leave a song gap.
	internal static bool SuccessorBackInTime(float minuetRecast, int position)
		=> minuetRecast + 45f * position >= 120f;

	/// <summary>Last seconds of the fight's final kill: nothing carries past it, so nothing is kept for a later window.</summary>
	internal const float FinalKillDumpWindow = 12f;

	/// <summary>
	/// Final kill: buffs still worth a GCD under them go even outside the plan, then the damage oGCDs the burst logic
	/// holds (Sidewinder, Heartbreak charges, a partial Pitch Perfect on the last tick).
	/// </summary>
	private bool FinalKillDump(out IAction? act)
	{
		act = null;
		var left = FinalKillIn;
		if (!InCombat || !(left <= FinalKillDumpWindow)) return false;
		if (left >= GCDTime(1) && (BattleVoicePvE.CanUse(out act, skipTTKCheck: true)
			|| RadiantFinalePvE.CanUse(out act, skipTTKCheck: true)
			|| RagingStrikesPvE.CanUse(out act, skipTTKCheck: true)
			|| (!HasHawksEye && BarragePvE.CanUse(out act, skipTTKCheck: true)))) return true;
		// Biggest first: a capped (or last-tick) Pitch Perfect and Empyreal before Sidewinder and the shared charges.
		return ((Repertoire == 3 || (Repertoire > 0 && (left <= 3.5f
				|| TwoStackPPBeforeEmpyreal(Repertoire, EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD()))))
				&& PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
			|| EmpyrealArrowPvE.CanUse(out act, skipTTKCheck: true)
			|| SidewinderPvE.CanUse(out act, skipTTKCheck: true)
			|| RainOfDeathPvE.CanUse(out act, usedUp: true, skipTTKCheck: true)
			|| HeartbreakShotPvE.CanUse(out act, usedUp: true, skipTTKCheck: true)
			|| BloodletterPvE.CanUse(out act, usedUp: true, skipTTKCheck: true);
	}

	protected override bool AttackAbility(IAction nextGCD, out IAction? act)
	{
		if (InCombat && SongAbility(out act))
		{
			return true;
		}

		if (PaddingFatebreaker)
		{
			act = null;
			return false;
		}

		// Opener: Empyreal right after Minuet, before the buffs (party buffs land ~6-7s anyway). At ~7s its third use fell into
		// FRU P1's untargetable stretch; from ~2s it fits before it (top logs: 6 Empyreals in 0-120s, us 5).
		if (InCombat && CombatTime < 10f && Song == Song.WanderersMinuet && EmpyrealArrowPvE.CanUse(out act))
		{
			return true;
		}

		if (FinalKillDump(out act))
		{
			return true;
		}

		// A song starts the burst; Battle Voice already up commits the remaining buffs even during songless recovery.
		if (IsBurst && (Song != Song.None || HasBattleVoice) && MagesBalladPvE.EnoughLevel)
		{
			// DoTs on the target gate Battle Voice only while nobody else is bursting: on FRU's crystals (fresh ~20s adds)
			// waiting for Stormbite+Caustic put it 8s behind Arcane Circle and Battle Litany.
			if (!HoldBurst && ((!RadiantFinalePvE.EnoughLevel && !RagingStrikesPvE.Cooldown.IsCoolingDown)
					|| (RadiantFinalePvE.EnoughLevel && !RadiantFinalePvE.Cooldown.IsCoolingDown && RagingStrikesPvE.EnoughLevel && (!RagingStrikesPvE.Cooldown.IsCoolingDown || RagingStrikesPvE.Cooldown.WillHaveOneCharge(BuffAlignment))))
					&& ((CurrentTarget?.HasStatus(true, StatusID.Windbite, StatusID.Stormbite) == true && CurrentTarget?.HasStatus(true, StatusID.VenomousBite, StatusID.CausticBite) == true) || DOTBarrage || PartyRaidBuffUp) && BattleVoicePvE.CanUse(out act, skipTTKCheck: BurstSkipsTTK))
			{
				return true;
			}

			// Our Battle Voice is out, so the burst is committed: a TTK reject here would leave it running alone.
			if (StatusHelper.PlayerHasStatus(true, StatusID.BattleVoice) && RadiantFinalePvE.CanUse(out act, skipTTKCheck: true))
			{
				return true;
			}

			// A Finale cast this GCD counts: its status lags the cast ~0.5-1s, and at the 10-17 P5 opener Pitch Perfect took
			// that weave and pushed Raging Strikes 2 GCDs late.
			if (((RadiantFinalePvE.EnoughLevel && (HasRadiantFinale
					|| (RadiantFinalePvE.Cooldown.IsCoolingDown && !RadiantFinalePvE.Cooldown.ElapsedAfter(2.5f))) && HasBattleVoice)
				|| (!RadiantFinalePvE.EnoughLevel && BattleVoicePvE.EnoughLevel && HasBattleVoice)
				|| (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel))
				&& RagingStrikesPvE.CanUse(out act, skipTTKCheck: HasBattleVoice || BurstSkipsTTK))
			{
				return true;
			}
		}
		else if (!MagesBalladPvE.EnoughLevel)
		{
			if (!StraightShotPvE.EnoughLevel && RagingStrikesPvE.CanUse(out act))
			{
				return true;
			}

			if (nextGCD.IsTheSameTo(true, StraightShotPvE) && RagingStrikesPvE.CanUse(out act))
			{
				return true;
			}
		}

		// Reserve an imminent Coda rebuild or Minuet handoff. The shared budget refreshes during long CT downtime;
		// a Minuet still far from ready does not need to suppress the final damage weaves before CT.
		if (ReserveCtCodaWeave || (ReserveCtPaeon && BMRDowntimeIn >= 0f && BMRDowntimeIn <= GCDTime(2)
			&& TheWanderersMinuetPvE.Cooldown.RecastTimeRemain <= GCDTime(2)))
		{
			act = null;
			return false;
		}
		if (ShouldReserveP4BurstWeave(DataCenter.IsInFRU && CurrentTarget is { BaseId: 0x45A9 or 0x45AB },
			IsBurst, HoldBurst, PlannedBurstIn, !BattleVoicePvE.Cooldown.IsCoolingDown && !RadiantFinalePvE.Cooldown.IsCoolingDown))
		{
			act = null;
			return false;
		}
		// One filler may follow the last P1 GCD; leave the second weave for Ballad during downtime.
		// shortcut: last-action tracking excludes manual items; use the confirmed weave count if they disrupt a live pull.
		if (ReserveFruSongWeave && !IActionHelper.IsLastActionGCD())
		{
			act = null;
			return false;
		}
		// Capped Repertoire goes before the buff wait: a song tick during the wait is lost.
		if (Repertoire == 3 && PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
		{
			return true;
		}

		// Two-stack PP before Empyreal: its guaranteed stack plus a coincident song tick overflows the cap.
		if (PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
		{
			// Last chance only (Minuet ends before the next GCD's weave): the song swap already spends leftover stacks just before
			// switching, so a 3s drain split them into two one-stack PPs around the last song tick (top logs 3-5 one-stack PPs, us 7).
			if (SongEndAfter(0.7f) && Repertoire > 0)
			{
				return true;
			}

			if (TwoStackPPBeforeEmpyreal(Repertoire, EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD()))
			{
				return true;
			}
		}

		// Usable buffs already took priority above; a blocked burst must not hold Empyreal, including songless FRU returns.
		if ((Song != Song.None || InCombat) && EmpyrealArrowPvE.CanUse(out act))
		{
			return true;
		}

		if (SidewinderPvE.EnoughLevel)
		{
			if (!IsBurst || LongPlannedHold || ((BattleVoicePvE.Cooldown.IsCoolingDown && !BattleVoicePvE.Cooldown.WillHaveOneCharge(10))
			&& (!RadiantFinalePvE.EnoughLevel || (RadiantFinalePvE.EnoughLevel && RadiantFinalePvE.Cooldown.IsCoolingDown && !RadiantFinalePvE.Cooldown.WillHaveOneCharge(10)))
			&& RagingStrikesPvE.Cooldown.IsCoolingDown))
			{
				if (SidewinderPvE.CanUse(out act))
				{
					return true;
				}
			}
		}

		// Charges keep recharging through a known downtime (15s each): spend the ones that would sit at the cap in it. If a
		// burst follows, they are back by then anyway. 10-17 clear vs top logs: 146s at the cap (tops 50-76s), mostly P1.
		if (ChargesCapInDowntime && (RainOfDeathPvE.CanUse(out act, usedUp: true)
			|| HeartbreakShotPvE.CanUse(out act, usedUp: true) || BloodletterPvE.CanUse(out act, usedUp: true)))
		{
			return true;
		}

		// Bloodletter Overcap protection
		if (BloodletterPvE.Cooldown.WillHaveXCharges(BloodletterMax, 3f))
		{
			if (RainOfDeathPvE.CanUse(out act, usedUp: true))
			{
				return true;
			}

			if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
			{
				return true;
			}

			if (BloodletterPvE.CanUse(out act, usedUp: true))
			{
				return true;
			}
		}

		// Prevents Bloodletter bumpcapping when MAGE is the song due to Repetoire procs
		if (BloodletterPvE.Cooldown.WillHaveXCharges(BloodletterMax, 7.5f) && Song == Song.MagesBallad)
		{
			if (RainOfDeathPvE.CanUse(out act, usedUp: true))
			{
				return true;
			}

			if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
			{
				return true;
			}

			if (BloodletterPvE.CanUse(out act, usedUp: true))
			{
				return true;
			}
		}

		if (BetterBloodletterLogic(out act))
		{
			return true;
		}

		return base.AttackAbility(nextGCD, out act);
	}
	#endregion

	#region GCD Logic
	protected override bool GeneralGCD(out IAction? act)
	{
		if (PaddingFatebreaker)
		{
			act = null;
			return false;
		}

		TrackDotSnapshots();

		// Iron Jaws' own 35% Hawk's Eye proc overwrites one already up (60 potency). When the Iron Jaws can wait a GCD, spend the
		// proc first: the DoTs still outlast the next GCD, or the burst buffs still cover it. 10-15 kills lost both procs this way.
		// A long Hawk can block IJ's self-status check; only bypass it when the resolved target is actually due.
		if (UptimeForDoT && (CanUseDoT(IronJawsPvE, out act)
			|| (HasHawksEye && CanUseDoT(IronJawsPvE, out act, skipStatusProvideCheck: true,
				refreshWithin: DataCenter.GCDTime(IronJawsPvE.Config.StatusRefreshGcdCount)))))
		{
			var jawsTarget = IronJawsPvE.Target.Target;
			if (HasHawksEye && jawsTarget != null
				&& Math.Min(jawsTarget.StatusTime(true, StatusID.Stormbite, StatusID.Windbite), jawsTarget.StatusTime(true, StatusID.CausticBite, StatusID.VenomousBite)) > DataCenter.GCDTime(1, 0.5f)
				&& (ShadowbitePvE.CanUse(out var proc) || RefulgentArrowPvE.CanUse(out proc, skipComboCheck: true)))
			{
				act = proc;
			}
			return true;
		}

		// No "DoTs end within 30s" gate: an Iron Jaws just before the burst left 38s, which blocked the snapshot in it (10-17 clear:
		// 1 of 9 bursts had Iron Jaws under all four buffs, tops 3-5). BurstEndRefreshPays already weighs the clipped time.
		if (UptimeForDoT && CanUseDoT(IronJawsPvE, out act, skipStatusProvideCheck: true) && IronJawsPvE.Target.Target != null
			&& BurstEndRefreshPays(BuffSnapshotOn(IronJawsPvE.Target.Target), DotSnapshotOn(IronJawsPvE.Target.Target),
				IronJawsPvE.Target.Target?.StatusTime(true, StatusID.Stormbite, StatusID.Windbite) ?? 0f,
				IronJawsPvE.Target.Target?.GetTTK() ?? float.NaN, BMRDowntimeIn))
		{
			if (StatusHelper.PlayerHasStatus(true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes) && BurstEndsNextGcd)
			{
				if (HasHawksEye && !StatusHelper.PlayerWillStatusEndGCD(1, 0.3f, true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes)
					&& (ShadowbitePvE.CanUse(out var proc) || RefulgentArrowPvE.CanUse(out proc, skipComboCheck: true)))
				{
					act = proc;
				}
				return true;
			}
		}

		// Barrage's Refulgent goes first: Resonant, Apex and Encore keep for 30s (Blast's 10s only starts at Apex), Barrage only 10s.
		// Barraged Refulgent is 840 potency; Shadowbite only wins at three resolved targets (300 each).
		if (HasBarrage && ((ShadowbitePvE.CanUse(out act) && ShadowbitePvE.Target.AffectedTargets.Length >= 3)
			|| RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)))
		{
			return true;
		}

		// Blast Arrow Ready lasts 10s; Resonant, Encore and Apex keep 30s. Waiting on Barrage lost it twice today.
		if (BlastArrowPvE.CanUse(out act))
		{
			return true;
		}

		// Spend Hawk before payloads if Iron Jaws will enter its refresh window next GCD; the P4 pair lost a proc here.
		if (HasHawksEye && UptimeForDoT && CanUseDoT(IronJawsPvE, out _, skipStatusProvideCheck: true,
			refreshWithin: DataCenter.GCDTime((uint)IronJawsPvE.Config.StatusRefreshGcdCount + 1, 0.5f))
			&& IronJawsPvE.Target.Target is { } nextJawsTarget && TargetLivesForDoT(nextJawsTarget))
		{
			var dotLeft = Math.Min(nextJawsTarget.StatusTime(true, StatusID.Stormbite, StatusID.Windbite),
				nextJawsTarget.StatusTime(true, StatusID.CausticBite, StatusID.VenomousBite));
			if (dotLeft > DataCenter.GCDTime(1, 0.5f)
				&& dotLeft <= DataCenter.GCDTime((uint)IronJawsPvE.Config.StatusRefreshGcdCount + 1, 0.5f)
				&& (ShadowbitePvE.CanUse(out act) || RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)))
			{
				return true;
			}
		}

		// A Hawk's Eye proc blocks Barrage (its Refulgent would eat both). In burst, spend the proc first so Barrage lands in buffs:
		// at 8:33 the proc sat behind Apex and Encore and held Barrage 17s, out of the window.
		if (HasRagingStrikes && HasHawksEye && BarragePvE.EnoughLevel && !BarragePvE.Cooldown.IsCoolingDown
			&& (ShadowbitePvE.CanUse(out act) || RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)))
		{
			return true;
		}

		if (RadiantEncorePvE.CanUse(out act, skipComboCheck: true))
		{
			// A standalone Finale (CT) has no Battle Voice coming: Encore on the return's first GCDs, before P4 dies.
			// Spend Encore before the lower-potency payloads, including when the first burst buff has already faded.
			if (InBurstStatus || HasRagingStrikes || (HasRadiantFinale && !HasBattleVoice) || FinalKillIn <= FinalKillDumpWindow
				|| StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.RadiantEncoreReady))
			{
				return true;
			}
		}

		if (ResonantArrowPvE.CanUse(out act))
		{
			return true;
		}

		if (CanUseApexArrow(out act))
		{
			return true;
		}

		// DoTs before the AoE fillers: with Ladonsbite at 2 targets the P4 pair never got DoTs, and Battle Voice
		// waits for both DoTs on the target, so P4 never burst.
		if (TryApplyDoT(out act))
		{
			return true;
		}

		//aoe
		if (ShadowbitePvE.CanUse(out act))
		{
			return true;
		}

		if (WideVolleyPvE.CanUse(out act))
		{
			return true;
		}

		if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true))
		{
			return true;
		}

		if (!RefulgentArrowPvE.Info.EnoughLevelAndQuest() && StraightShotPvE.CanUse(out act))
		{
			return true;
		}

		// Proc consumers already took priority; let native status checks decide filler eligibility.
		if (LadonsbitePvE.CanUse(out act))
		{
			return true;
		}

		if (QuickNockPvE.CanUse(out act))
		{
			return true;
		}

		if (BurstShotPvE.CanUse(out act))
		{
			return true;
		}

		if (HeavyShotPvE.CanUse(out act))
		{
			return true;
		}

		return base.GeneralGCD(out act);
	}
	#endregion

	#region Extra Methods
	private bool TryApplyDoT(out IAction? act)
	{
		act = null;
		if (!UptimeForDoT) return false;
		return (StormbitePvE.EnoughLevel && CanUseDoT(StormbitePvE, out act))
			|| (CausticBitePvE.EnoughLevel && CanUseDoT(CausticBitePvE, out act))
			|| (!StormbitePvE.EnoughLevel && CanUseDoT(WindbitePvE, out act))
			|| (!CausticBitePvE.EnoughLevel && CanUseDoT(VenomousBitePvE, out act));
	}

	private bool CanUseDoT(IBaseAction action, out IAction? act, bool skipStatusProvideCheck = false, float? refreshWithin = null)
	{
		// Final kill: a fresh DoT or Iron Jaws refresh only beats Burst Shot with more ticks left than the dump window
		// (live 10-17: Iron Jaws 4.8s before Pandora died).
		if (FinalKillIn <= FinalKillDumpWindow)
		{
			act = null;
			return false;
		}
		var setting = action.Setting;
		var canTarget = setting.CanTarget;
		var jaws = action == IronJawsPvE;
		// Filter before native resolution: a dying or already-dotted first target must not hide another eligible enemy.
		setting.CanTarget = t => canTarget(t) && TargetLivesForDoT(t)
			&& (jaws || ((!DOTBoss || t.IsBossFromIcon())
				&& t.WillStatusEndGCD(0, 0, true, setting.TargetStatusProvide ?? [])))
			&& (refreshWithin == null || t.WillStatusEnd(refreshWithin.Value, true, setting.TargetStatusProvide ?? []));
		try
		{
			return action.CanUse(out act, skipStatusProvideCheck: skipStatusProvideCheck);
		}
		finally
		{
			setting.CanTarget = canTarget;
		}
	}

	private bool BurstBuffEndsWithin(float seconds)
		=> StatusHelper.PlayerWillStatusEnd(seconds, false, StatusID.RagingStrikes)
			|| (HasBattleVoice && StatusHelper.PlayerWillStatusEnd(seconds, false, StatusID.BattleVoice))
			|| (HasRadiantFinale && StatusHelper.PlayerWillStatusEnd(seconds, false, StatusID.RadiantFinale));

	private bool CanUseApexArrow(out IAction act)
	{
		if (!ApexArrowPvE.CanUse(out act))
		{
			return false;
		}

		// Final kill: Apex out-hits a Burst Shot from ~40 Soul Voice; at 80+ fire it while Blast Arrow still fits. Two GCDs, not one:
		// the next GCD is picked ahead of time, so a last-GCD-only window was never hit.
		if (SoulVoice >= 40 && FinalKillIn <= (SoulVoice >= 80 ? GCDTime(3) : GCDTime(2)) + 0.5f)
		{
			return true;
		}

		// Fresh P4 targets must not push the two-GCD Apex/Blast payload out of the active burst.
		if (!InBurstStatus && TryApplyDoT(out _))
		{
			return false;
		}

		// Auto Burst off: Raging Strikes / Battle Voice never go, so the alignment waits below would hold Apex forever.
		if (!IsBurst && SoulVoice >= SoulVoiceConfig)
		{
			return true;
		}

		// Capped gauge in a long planned hold: the buffs it waits for are held, and the gauge refills before the window.
		if (SoulVoice >= 100 && LongPlannedHold)
		{
			return true;
		}

		if (Song == Song.WanderersMinuet && SoulVoice >= 100 && !HasRagingStrikes && !RagingStrikesPvE.Cooldown.WillHaveOneCharge(10))
		{
			return true;
		}

		if (Song == Song.WanderersMinuet && SoulVoice >= 80 && !HasRagingStrikes)
		{
			return false;
		}

		if (SoulVoice >= SoulVoiceConfig && BattleVoicePvE.Cooldown.WillHaveOneCharge(25))
		{
			return false;
		}

		// AoE only after the burst holds: with two P4 bosses this fired Apex 15s before the burst.
		if ((QuickNockPvE.CanUse(out _) || LadonsbitePvE.CanUse(out _)) && SoulVoice >= SoulVoiceConfig)
		{
			return true;
		}

		// The first buff to end sets the deadline, not Raging Strikes: in the 10-17 clear Battle Voice and Finale went 1-4s
		// earlier, so an RS-timed Apex put Blast Arrow after them.
		if (SoulVoice >= 80 && HasRagingStrikes && BurstBuffEndsWithin(10f))
		{
			return true;
		}

		if (SoulVoice >= SoulVoiceConfig && HasRagingStrikes && HasBattleVoice)
		{
			return true;
		}

		if (Song == Song.MagesBallad && SoulVoice >= 80 && SongEndAfter(22))
		{
			return true;
		}

		if (!HasRagingStrikes && SoulVoice >= SoulVoiceConfig)
		{
			return true;
		}

		return false;
	}

	/// <summary>Downtime starts within three GCDs and the charges would reach the cap before it ends.</summary>
	private bool ChargesCapInDowntime => BMRDowntimeIn is > 0f and < float.MaxValue && BMRDowntimeIn <= GCDTime(3)
		&& BMRDowntimeEndIn is > 0f and < float.MaxValue
		&& BloodletterPvE.Cooldown.WillHaveXCharges(BloodletterMax, BMRDowntimeEndIn - GCDTime(1));

	private bool BetterBloodletterLogic(out IAction? act)
	{

		if (RainOfDeathPvE.CanUse(out act, usedUp: true))
		{
			if (InBurstStatus)
			{
				return true;
			}
		}

		if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
		{
			if (InBurstStatus)
			{
				return true;
			}
		}

		if (BloodletterPvE.CanUse(out act, usedUp: true))
		{
			if (InBurstStatus)
			{
				return true;
			}
		}
		return false;
	}
	#endregion
}
