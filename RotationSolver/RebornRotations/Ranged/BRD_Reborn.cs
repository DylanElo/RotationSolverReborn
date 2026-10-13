namespace RotationSolver.RebornRotations.Ranged;

[Rotation("Reborn", CombatType.PvE, GameVersion = "7.55",
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

	/// <summary>BRD Plan: Battle Voice (and so Finale and Raging Strikes) waits for the planned window.</summary>
	private protected virtual bool HoldBurst => false;

	/// <summary>Seconds until the planned burst window; only meaningful while HoldBurst.</summary>
	private protected virtual float PlannedBurstIn => 0f;

	/// <summary>
	/// A planned hold long enough that Sidewinder (60s recast) and refilled Soul Voice are back for the window,
	/// even if party buffs pull it in by BRD Plan's 8s slide.
	/// </summary>
	private bool LongPlannedHold => HoldBurst && PlannedBurstIn >= 60f + BRD_Plan.WindowSlide;

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
			&& (RagingStrikesPvE.Cooldown.WillHaveOneChargeGCD(1) || HasRagingStrikes) && UseBurstMedicine(out act))
		{
			return true;
		}

		if (nextGCD.IsTheSameTo(true, StraightShotPvE, VenomousBitePvE, WindbitePvE, IronJawsPvE))
		{
			return base.EmergencyAbility(nextGCD, out act);
		}
		else if (!RagingStrikesPvE.EnoughLevel || HasRagingStrikes)
		{
			if (((EmpyrealArrowPvE.Cooldown.IsCoolingDown && !EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1)) || !EmpyrealArrowPvE.EnoughLevel) && Repertoire != 3)
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
	/// <summary>With burst on, a ready Finale means the stacks wait for the buffs (unless a planned hold keeps it ready); with it off Finale never goes.</summary>
	internal static bool TwoStackPPBeforeEmpyreal(int repertoire, bool empyrealSoon, bool autoBurst, bool finaleCooling)
		=> repertoire == 2 && empyrealSoon && (!autoBurst || finaleCooling);

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
	/// </summary>
	internal static bool BurstEndRefreshPays(float now, float dotSnapshot, float remain)
		=> DotPotencyPerSecond * (remain * (now - dotSnapshot) + (DotDuration - remain) * (now - 1f)) > IronJawsLoss * remain / DotDuration;

	/// <summary>Finale's least lead on the CT return to be back (110s) for P5's burst, which comes ~102-108s after the return.</summary>
	internal const float StandaloneFinaleLead = 8f;

	/// <summary>Finale's 20s buff should still cover most of the return: no earlier than this before it.</summary>
	internal const float StandaloneFinaleLatest = 10f;

	/// <summary>
	/// FRU CT (P4 pair gone): a Finale without Battle Voice, then Encore on the return (top Bards: Xw 12:20, Aqualyy 12:22, ~13s
	/// before it, back for P5 at 14:13/14:16). Only 8-10s before the return (back for P5, buff still up on return), at least 2 Codas,
	/// and Battle Voice still cooling, so it is never the normal burst. CodaGrab rebuilds the Codas in the P4->P5 gap.
	/// </summary>
	// ponytail: the 8s lead assumes P5's burst ~102s+ after the CT return (Mana group 102s, Xw 108s); a faster P4 kill shrinks it.
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

	private bool TrySongTrackAction(SongTrack track, out IAction? act)
	{
		act = null;
		return track switch
		{
			SongTrack.WanderersMinuet => TheWanderersMinuetPvE.CanUse(out act),
			SongTrack.MagesBallad => MagesBalladPvE.CanUse(out act),
			SongTrack.ArmysPaeon => ArmysPaeonPvE.CanUse(out act),
			_ => false,
		};
	}

	protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
	{
		if (CurrentTarget is { } target)
		{
			_lastTargetBaseId = target.BaseId;
		}

		if (InCombat && IsBurst && ShouldUseStandaloneFinale(
				DataCenter.IsInFRU && _lastTargetBaseId is 0x45A9 or 0x45AB && DataCenter.AllHostileTargets.Count == 0,
				BMRDowntimeEndIn, Coda.Count(s => s != Song.None), BattleVoicePvE.Cooldown.IsCoolingDown)
			&& RadiantFinalePvE.CanUse(out act))
		{
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
		if (CodaGrab(out act))
		{
			return true;
		}

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
		// No hostiles at all, not just out of range: a knockback out of 25y must not swap songs mid-uptime.
		if (!RadiantFinalePvE.EnoughLevel || DataCenter.AllHostileTargets.Count > 0) return false;
		var downtimeLeft = BMRDowntimeEndIn;
		if (!ShouldGrabCodas(downtimeLeft, TheWanderersMinuetPvE.Cooldown.IsCoolingDown ? TheWanderersMinuetPvE.Cooldown.RecastTimeRemain : 0f))
		{
			return false;
		}
		var coda = Coda;
		var minuetRecast = TheWanderersMinuetPvE.Cooldown.IsCoolingDown ? TheWanderersMinuetPvE.Cooldown.RecastTimeRemain : 0f;
		return (Array.IndexOf(coda, Song.MagesBallad) < 0 && SuccessorBackInTime(minuetRecast, 1) && MagesBalladPvE.CanUse(out act))
			|| (Array.IndexOf(coda, Song.ArmysPaeon) < 0 && SuccessorBackInTime(minuetRecast, 2) && ArmysPaeonPvE.CanUse(out act));
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

		if (IsBurst && Song != Song.None && MagesBalladPvE.EnoughLevel)
		{
			if (!HoldBurst && ((!RadiantFinalePvE.EnoughLevel && !RagingStrikesPvE.Cooldown.IsCoolingDown)
					|| (RadiantFinalePvE.EnoughLevel && !RadiantFinalePvE.Cooldown.IsCoolingDown && RagingStrikesPvE.EnoughLevel && (!RagingStrikesPvE.Cooldown.IsCoolingDown || RagingStrikesPvE.Cooldown.WillHaveOneCharge(BuffAlignment))))
					&& ((CurrentTarget?.HasStatus(true, StatusID.Windbite, StatusID.Stormbite) == true && CurrentTarget?.HasStatus(true, StatusID.VenomousBite, StatusID.CausticBite) == true) || DOTBarrage) && BattleVoicePvE.CanUse(out act))
			{
				return true;
			}

			if (StatusHelper.PlayerHasStatus(true, StatusID.BattleVoice) && RadiantFinalePvE.CanUse(out act))
			{
				return true;
			}

			if (((RadiantFinalePvE.EnoughLevel && HasRadiantFinale && HasBattleVoice)
				|| (!RadiantFinalePvE.EnoughLevel && BattleVoicePvE.EnoughLevel && HasBattleVoice)
				|| (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel))
				&& RagingStrikesPvE.CanUse(out act))
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

		// Capped Repertoire goes before the buff wait: a song tick during the wait is lost.
		if (Repertoire == 3 && PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
		{
			return true;
		}

		if (RadiantFinalePvE.EnoughLevel && RadiantFinalePvE.Cooldown.IsCoolingDown && RadiantFinalePvE.Cooldown.WillHaveOneChargeGCD(1)
			&& BattleVoicePvE.EnoughLevel && !BattleVoicePvE.Cooldown.IsCoolingDown)
		{
			return base.AttackAbility(nextGCD, out act);
		}

		// Two-stack PP before Empyreal: its guaranteed stack plus a coincident song tick overflows the cap.
		if (PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
		{
			if (SongEndAfter(3) && Repertoire > 0)
			{
				return true;
			}

			if (TwoStackPPBeforeEmpyreal(Repertoire, EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(), IsBurst, RadiantFinalePvE.Cooldown.IsCoolingDown || HoldBurst))
			{
				return true;
			}
		}

		// Waits for Raging Strikes only while burst is on; with Auto Burst off Raging Strikes never goes and Empyreal never would.
		// Same while the burst waits for its planned window: Empyreal would sit for the whole wait.
		if ((!IsBurst || HoldBurst || RagingStrikesPvE.Cooldown.IsCoolingDown || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(15)) && Song != Song.None && EmpyrealArrowPvE.CanUse(out act))
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

		if (UptimeForDoT && IronJawsPvE.CanUse(out act))
		{
			return true;
		}

		if (UptimeForDoT && IronJawsPvE.CanUse(out act, skipStatusProvideCheck: true) && (IronJawsPvE.Target.Target?.WillStatusEnd(30, true, IronJawsPvE.Setting.TargetStatusProvide ?? []) ?? false)
			&& BurstEndRefreshPays(BuffSnapshotOn(IronJawsPvE.Target.Target), DotSnapshotOn(IronJawsPvE.Target.Target),
				IronJawsPvE.Target.Target?.StatusTime(true, StatusID.Stormbite, StatusID.Windbite) ?? 0f))
		{
			if (StatusHelper.PlayerHasStatus(true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes) && StatusHelper.PlayerWillStatusEndGCD(1, 1, true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes))
			{
				return true;
			}
		}

		// Barrage's Refulgent goes first: Resonant, Apex and Encore keep for 30s (Blast's 10s only starts at Apex), Barrage only 10s.
		// Top FRU logs fire Refulgent as the next GCD after Barrage in 20 of 27 bursts.
		if (HasBarrage && (ShadowbitePvE.CanUse(out act) || RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)))
		{
			return true;
		}

		// Blast Arrow Ready lasts 10s; Resonant, Encore and Apex keep 30s. Waiting on Barrage lost it twice today.
		if (BlastArrowPvE.CanUse(out act))
		{
			return true;
		}

		// A Hawk's Eye proc blocks Barrage (its Refulgent would eat both). In burst, spend the proc first so Barrage lands in buffs:
		// at 8:33 the proc sat behind Apex and Encore and held Barrage 17s, out of the window.
		if (HasRagingStrikes && HasHawksEye && BarragePvE.EnoughLevel && !BarragePvE.Cooldown.IsCoolingDown
			&& (ShadowbitePvE.CanUse(out act) || RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)))
		{
			return true;
		}

		if (ResonantArrowPvE.CanUse(out act))
		{
			return true;
		}

		if (CanUseApexArrow(out act))
		{
			return true;
		}

		if (RadiantEncorePvE.CanUse(out act, skipComboCheck: true))
		{
			// A standalone Finale (CT) has no Battle Voice coming: Encore on the return's first GCDs, before P4 dies.
			if (InBurstStatus || (HasRadiantFinale && !HasBattleVoice) || StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.RadiantEncoreReady))
			{
				return true;
			}
		}

		// DoTs before the AoE fillers: with Ladonsbite at 2 targets the P4 pair never got DoTs, and Battle Voice
		// waits for both DoTs on the target, so P4 never burst.
		if (StormbitePvE.EnoughLevel && UptimeForDoT)
		{
			if (StormbitePvE.CanUse(out act))
			{
				if ((DOTBoss && StormbitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
				{
					if (StormbitePvE.Target.Target.HasStatus(true, StatusID.Stormbite) == false)
					{
						return true;
					}
				}
			}
		}
		if (CausticBitePvE.EnoughLevel && UptimeForDoT)
		{
			if (CausticBitePvE.CanUse(out act))
			{
				if ((DOTBoss && CausticBitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
				{
					if (CausticBitePvE.Target.Target.HasStatus(true, StatusID.CausticBite) == false)
					{
						return true;
					}
				}
			}
		}

		if (!StormbitePvE.EnoughLevel && UptimeForDoT)
		{
			if (WindbitePvE.CanUse(out act))
			{
				if ((DOTBoss && WindbitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
				{
					if (IronJawsPvE.EnoughLevel && WindbitePvE.Target.Target.HasStatus(true, StatusID.Windbite) == false)
					{
						return true;
					}
					if (!IronJawsPvE.EnoughLevel)
					{
						return true;
					}
				}
			}
		}
		if (!CausticBitePvE.EnoughLevel && UptimeForDoT)
		{
			if (VenomousBitePvE.CanUse(out act))
			{
				if ((DOTBoss && VenomousBitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
				{
					if (IronJawsPvE.EnoughLevel && VenomousBitePvE.Target.Target.HasStatus(true, StatusID.VenomousBite) == false)
					{
						return true;
					}
					if (!IronJawsPvE.EnoughLevel)
					{
						return true;
					}
				}
			}
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

		if (LadonsbitePvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
		{
			return true;
		}

		if (QuickNockPvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
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

		if (BurstShotPvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
		{
			return true;
		}

		if (HeavyShotPvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
		{
			return true;
		}

		return base.GeneralGCD(out act);
	}
	#endregion

	#region Extra Methods
	private bool CanUseApexArrow(out IAction act)
	{
		if (!ApexArrowPvE.CanUse(out act))
		{
			return false;
		}

		if (CurrentTarget?.WillStatusEndGCD(1, 1, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false)
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

		if (SoulVoice >= 80 && HasRagingStrikes && StatusHelper.PlayerWillStatusEnd(10, false, StatusID.RagingStrikes))
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