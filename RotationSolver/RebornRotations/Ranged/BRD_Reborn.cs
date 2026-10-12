namespace RotationSolver.RebornRotations.Ranged;

[Rotation("Reborn", CombatType.PvE, GameVersion = "7.55",
	Description = "Please make sure that the three song times add up to 120 seconds, Wanderers default first song for now.")]
[SourceCode(Path = "main/RebornRotations/Ranged/BRD_Reborn.cs")]

public sealed class BRD_Reborn : BardRotation
{
	#region Config Options

	[Range(1, 5, ConfigUnitType.Seconds, 0.1f)]
	[RotationConfig(CombatType.PvE, Name = "Buff Alignment Timer (Experimental, do not touch if you don't understand it)")]
	public float BuffAlignment { get; set; } = 1;

	[RotationConfig(CombatType.PvE, Name = "Attempt to assign Raging Strikes, Battle Voice, and Radiant Finale to specific ogcd slots (Experimental)")]
	public bool OGCDTimers { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "FRU potion bursts: combat times in seconds, comma separated (replaces the countdown potion in FRU only)")]
	public string PotionTimes { get; set; } = "240,630,970";

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
	private SongTrack FirstSong { get; set; } = SongTrack.WanderersMinuet;

	[RotationConfig(CombatType.PvE, Name = "Second Song")]
	private SongTrack SecondSong { get; set; } = SongTrack.MagesBallad;

	[RotationConfig(CombatType.PvE, Name = "Third Song")]
	private SongTrack ThirdSong { get; set; } = SongTrack.ArmysPaeon;

	[RotationConfig(CombatType.PvE, Name = "Use Warden's Paean on other players")]
	public bool BRDEsuna { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Prevent the use of defense abilties during burst")]
	private bool BurstDefense { get; set; } = true;

	private float WANDRemainTime => 45 - WANDTime;
	private float MAGERemainTime => 45 - MAGETime;
	private float ARMYRemainTime => 45 - ARMYTime;

	private bool InBurstStatus => (!BattleVoicePvE.EnoughLevel && HasRagingStrikes)
		|| (BattleVoicePvE.EnoughLevel && !RadiantFinalePvE.EnoughLevel && HasRagingStrikes && HasBattleVoice)
		|| (MinstrelsCodaTrait.EnoughLevel && HasRagingStrikes && HasRadiantFinale && HasBattleVoice);

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
		if (IsBurst && DataCenter.IsInFRU && PotionDue(CombatTime, PotionTimes) && RagingStrikesPvE.EnoughLevel
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
	/// <summary>With burst on, a ready Finale means the stacks wait for the buffs; with it off Finale never goes.</summary>
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

		if (IsBurst && Song != Song.None && MagesBalladPvE.EnoughLevel)
		{
			if (((!RadiantFinalePvE.EnoughLevel && !RagingStrikesPvE.Cooldown.IsCoolingDown)
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

			if (TwoStackPPBeforeEmpyreal(Repertoire, EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(), IsBurst, RadiantFinalePvE.Cooldown.IsCoolingDown))
			{
				return true;
			}
		}

		// Waits for Raging Strikes only while burst is on; with Auto Burst off Raging Strikes never goes and Empyreal never would.
		if ((!IsBurst || RagingStrikesPvE.Cooldown.IsCoolingDown || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(15)) && Song != Song.None && EmpyrealArrowPvE.CanUse(out act))
		{
			return true;
		}

		if (SidewinderPvE.EnoughLevel)
		{
			if (!IsBurst || ((BattleVoicePvE.Cooldown.IsCoolingDown && !BattleVoicePvE.Cooldown.WillHaveOneCharge(10))
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
		if (IronJawsPvE.CanUse(out act))
		{
			return true;
		}

		if (IronJawsPvE.CanUse(out act, skipStatusProvideCheck: true) && (IronJawsPvE.Target.Target?.WillStatusEnd(30, true, IronJawsPvE.Setting.TargetStatusProvide ?? []) ?? false))
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
			if (InBurstStatus || StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.RadiantEncoreReady))
			{
				return true;
			}
		}

		if (BlastArrowPvE.CanUse(out act))
		{
			if (!HasRagingStrikes)
			{
				return true;
			}

			if (HasRagingStrikes && BarragePvE.Cooldown.IsCoolingDown)
			{
				return true;
			}
		}

		// DoTs before the AoE fillers: with Ladonsbite at 2 targets the P4 pair never got DoTs, and Battle Voice
		// waits for both DoTs on the target, so P4 never burst.
		if (StormbitePvE.EnoughLevel)
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
		if (CausticBitePvE.EnoughLevel)
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

		if (!StormbitePvE.EnoughLevel)
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
		if (!CausticBitePvE.EnoughLevel)
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

		if (QuickNockPvE.CanUse(out _) && SoulVoice >= SoulVoiceConfig)
		{
			return true;
		}

		if (LadonsbitePvE.CanUse(out _) && SoulVoice >= SoulVoiceConfig)
		{
			return true;
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