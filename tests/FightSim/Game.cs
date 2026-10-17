using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using RotationSolver.Basic.Helpers;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using SimDateTime = RotationSolver.RebornRotations.DateTime;

namespace FightSim;

public enum AoeShape { Single, Self, Cone, CircleOnTarget, Line }

/// <summary>Game facts for one action. Recast 0 on a GCD means it rolls the GCD only; GcdRecast is a fixed GCD of its own (Blazing Shot).</summary>
public sealed record ActionSpec(ActionID Id, uint GameId, bool IsGcd, int Level = 1, float Recast = 0, int MaxCharges = 1,
	AoeShape Shape = AoeShape.Single, float Radius = 0, ActionID? Group = null, float GcdRecast = 0);

/// <summary>A recast group as the game's RecastDetail reports it: zero while ready, Total = recast x charges while cooling.</summary>
public sealed class Cooldown
{
	public float OneCharge;
	public int MaxCharges = 1;
	public float FullAt = -1e9f;
	public bool IsCoolingDown => FullAt > Game.Now;
	public float Total => IsCoolingDown ? OneCharge * MaxCharges : 0f;
	public float Elapsed => IsCoolingDown ? Total - (FullAt - Game.Now) : 0f;
	public int Charges => IsCoolingDown ? (int)(Elapsed / OneCharge + 1e-4f) : MaxCharges;
	public void Use() => FullAt = Math.Max(Game.Now, FullAt) + OneCharge;
	public void Reduce(float seconds) { if (IsCoolingDown) FullAt = Math.Max(Game.Now, FullAt - seconds); }
}

public interface IJob
{
	string Abbr { get; }
	/// <summary>Auto-attack potency and delay, measured from the log's Shot hits.</summary>
	float AutoPotency { get; }
	float AutoDelay { get; }
	/// <summary>The log's hits on the sim's potency scale (expected crit/DH, same multipliers as the sim), for the Log column.</summary>
	List<(float T, string Name, double P)> LogPotency(Timeline t);
	ActionSpec Spec(ActionID id);
	ActionID Adjusted(ActionID id);
	bool GameAllows(ActionID id);
	float HasteFactor { get; }
	/// <summary>Song ticks and anything else the job does on its own clock.</summary>
	void Advance(float dt);
	void OnDeath();
	void Execute(ActionSpec spec, IBattleChara? target, IBattleChara[] affected);
	/// <summary>Damage multiplier from the job's own buffs (crit/DH expectation included), applied on top of the party's.</summary>
	float SelfMultiplier();
}

public sealed record Timeline(
	string Source, string Player, float Duration, List<TimelineActor> Actors, List<float[]> PlayerPos, List<JsonElement[]> PlayerTarget,
	List<PartyBuff> PartyBuffs, List<Debuff> PlayerDebuffs, List<PlanWindow> PlanWindows, List<JsonElement[]> LoggedCasts, double LoggedDamage,
	List<EnemyCast>? EnemyCasts, List<JsonElement[]> LoggedHits, Dictionary<string, float> Stats,
	string? Job = null, List<string>? PartyJobs = null, List<EnemyDebuff>? EnemyDebuffs = null, bool PlanHiddenAcrossKills = false)
{
	public static Timeline Load(string path) => JsonSerializer.Deserialize<Timeline>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}

public sealed record TimelineActor(string Key, uint BaseId, string Name, List<float[]> Spans, List<float[]> Hp, List<float[]> Pos, float? Death, float Taken = 1f, float? PresentFrom = null);
public sealed record PartyBuff(string Name, string Job, float Mult, float Start, float End);
public sealed record Debuff(string Name, float Start, float End);
public sealed record PlanWindow(float Start, float End, bool Potion);
public sealed record EnemyCast(string Key, uint Id, float Start, float Duration);
public sealed record EnemyDebuff(string Key, string Name, string Job, float Mult, float Start, float End);

/// <summary>An enemy from the timeline: targetable spans, HP and position replayed from the log.</summary>
public sealed class Enemy(TimelineActor actor, ulong objectId)
{
	public TimelineActor Actor { get; } = actor;
	public IBattleChara Chara { get; } = new() { GameObjectId = objectId, BaseId = actor.BaseId, Name = actor.Name, HealthRatio = () => Sample(actor.Hp, 1f) };
	public bool Present => Actor.PresentFrom is not { } since || Game.Now >= since;
	public bool Targetable => Present && Actor.Spans.Any(s => Game.Now >= s[0] && Game.Now < s[1]) && Chara.GetHealthRatio() > 0f;

	public (float X, float Y) Position => (Sample(Actor.Pos, 1, 100f), Sample(Actor.Pos, 2, 100f));

	/// <summary>The last sample at or before now (column 1 by default).</summary>
	internal static float Sample(List<float[]> rows, float fallback) => Sample(rows, 1, fallback);

	internal static float Sample(List<float[]> rows, int column, float fallback)
	{
		var lo = 0;
		var hi = rows.Count - 1;
		var found = -1;
		while (lo <= hi)
		{
			var mid = (lo + hi) / 2;
			if (rows[mid][0] <= Game.Now) { found = mid; lo = mid + 1; } else hi = mid - 1;
		}
		return found < 0 ? (rows.Count > 0 ? rows[0][column] : fallback) : rows[found][column];
	}
}

/// <summary>
/// The fight as the rotation sees it, stepped in sim time: enemies, party buffs and lockouts replayed from the log,
/// the player's GCD, cooldowns and statuses simulated, damage added up as expected value (potency x multipliers).
/// </summary>
public static class Game
{
	public static int Level { get; set; } = 100;
	// Optional external-boundary inputs for selector regressions; whole-fight replay keeps its measured defaults.
	public static bool IsInFRU { get; set; } = true;
	public static (float Start, float End)? ForecastOverride { get; set; }
	public static float? TtkOverride { get; set; }
	public static Func<IBattleChara, float>? TargetTtkOverride { get; set; }
	public static float? AverageTtkOverride { get; set; }
	public static IBattleChara? CurrentTargetOverride { get; set; }
	public static Func<ulong, IBattleChara?>? ObjectLookupOverride { get; set; }
	public static IBattleChara? FindObject(ulong id) => ObjectLookupOverride is { } lookup
		? lookup(id) : CurrentObjects.FirstOrDefault(c => c.GameObjectId == id);
	public static bool AutoBurst { get; set; } = true;
	public const float AnimationLockSeconds = 0.65f; // 0.6s lock + ~50ms latency
	public const float ItemLockSeconds = 1.1f;
	public const float PotionRecast = 270f;
	public const float PotionMultiplier = 1.07f; // ponytail: assumed Gemdraught gain on ~BiS dex; both sim and log use it

	public static Timeline Timeline { get; private set; } = null!;
	public static IJob Job { get; private set; } = null!;
	public static float Now { get; private set; }
	public static Random Rng { get; private set; } = new(0);
	public static IBattleChara Player { get; private set; } = new();
	public static List<Enemy> Enemies { get; private set; } = [];
	public static IEnumerable<IBattleChara>? CurrentObjectsOverride { get; set; }
	public static IEnumerable<IBattleChara> CurrentObjects => CurrentObjectsOverride ?? Enemies.Where(e => e.Present).Select(e => e.Chara);
	public static List<IBattleChara> Hostiles { get; private set; } = [];
	public static IBattleChara? Target { get; private set; }
	public static List<string> PartyJobs { get; private set; } = [];

	public static float BaseGcd { get; set; } = 2.48f;
	/// <summary>RSR's Action6Head (per job in RotationSolver.json): how far into the GCD it commits the next one, as a fraction of it.</summary>
	public static float ActionAhead { get; set; } = 0.25f;
	private static float _gcdReadyAt, _gcdTotal, _lockUntil, _lastActionAt = -1e9f, _potionReadyAt;
	public static float GcdTotal => GcdRemain > 0 ? _gcdTotal : (float)Math.Round(BaseGcd * Job.HasteFactor, 2);
	public static float GcdRemain => Math.Max(0f, _gcdReadyAt - Now);
	public static float WeaponRemain => GcdRemain;
	public static float AnimationLock => Math.Max(0f, _lockUntil - Now);
	public static float TimeSinceLastAction => Now - _lastActionAt;
	public static bool PotionReady => Now >= _potionReadyAt;
	/// <summary>The player's first logged action: the log starts on the pull, the player acts a little later.</summary>
	public static float StartAt { get; private set; }
	public static bool InCombat => Now >= StartAt && Now < Timeline.Duration;
	public static bool Locked => Timeline.PlayerDebuffs.Any(d => d.Name is "Stun" or "Deep Freeze" or "Down for the Count" or "Dead" && Now >= d.Start && Now < d.End);

	public static IAction? LastAction { get; private set; }
	public static IAction? LastGcd { get; private set; }
	public static IAction? LastAbility { get; private set; }

	private static readonly Dictionary<ActionID, Cooldown> Cooldowns = [];
	private static readonly Cooldown GcdGroup = new();
	private static readonly Queue<(SimDateTime, Dictionary<ulong, float>?)> HpRecord = new();
	private static float _hpRecordedAt = -1e9f;
	private static DowntimeForecast _downtimeForecast = null!;

	public static IEnumerable<(SimDateTime, Dictionary<ulong, float>?)> RecordedHP => HpRecord;

	// Results
	public static readonly List<(float Time, string Action, string? Target, double Damage)> Casts = [];
	public static readonly Dictionary<string, double> DamageByAction = [];
	public static readonly List<(float Time, double Damage)> DamageEvents = [];

	public static void Reset(Timeline timeline, IJob job, int seed)
	{
		Level = 100;
		IsInFRU = true;
		ForecastOverride = null;
		TtkOverride = null;
		TargetTtkOverride = null;
		AverageTtkOverride = null;
		CurrentTargetOverride = null;
		ObjectLookupOverride = null;
		CurrentObjectsOverride = null;
		AutoBurst = true;
		Timeline = timeline;
		Job = job;
		Rng = new Random(seed);
		Now = 0f;
		Player = new IBattleChara { GameObjectId = 1, Name = timeline.Player };
		ulong id = 100;
		Enemies = timeline.Actors.Select(a => new Enemy(a, id++)).ToList();
		// Older (BRD-only) timelines have no party list: the jobs seen buffing, plus the player's own.
		PartyJobs = timeline.PartyJobs ?? timeline.PartyBuffs.Select(b => b.Job).Distinct().Append(job.Abbr).Distinct().ToList();
		_gcdReadyAt = _gcdTotal = _lockUntil = _potionReadyAt = _nextAuto = 0f;
		_targetCursor = 0;
		_lastActionAt = -1e9f;
		LastAction = LastGcd = LastAbility = null;
		Cooldowns.Clear();
		HpRecord.Clear();
		_hpRecordedAt = -1e9f;
		Casts.Clear();
		DamageByAction.Clear();
		DamageEvents.Clear();
		_downtimeForecast = new DowntimeForecast(timeline);
		_castsByKey = (timeline.EnemyCasts ?? []).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.ToList());
		_ttkCache.Clear();
		StartAt = timeline.LoggedCasts.Count > 0 ? timeline.LoggedCasts[0][0].GetSingle() : 0f;
		foreach (var a in timeline.Actors) if (a.Spans[0][0] < StartAt + 2f) a.Spans[0][0] = 0f; // the opening boss is there from the pull
		Refresh();
	}

	public static float DowntimeIn => ForecastOverride?.Start ?? _downtimeForecast.At(Now).Start;
	public static float DowntimeEndIn => ForecastOverride?.End ?? _downtimeForecast.At(Now).End;

	public static Cooldown Cooldown(ActionID id)
	{
		var spec = Job.Spec(id);
		if (spec.IsGcd && spec.Recast == 0)
		{
			GcdGroup.OneCharge = _gcdTotal;
			GcdGroup.FullAt = _gcdReadyAt;
			return GcdGroup;
		}
		var key = spec.Group ?? id;
		if (!Cooldowns.TryGetValue(key, out var cd))
		{
			cd = Cooldowns[key] = new Cooldown { OneCharge = spec.Recast, MaxCharges = spec.MaxCharges };
		}
		return cd;
	}

	public static bool InRange(IBattleChara chara) => chara.GameObjectId == 1 || Hostiles.Contains(chara);

	// ponytail: hitboxes are not in the log; FRU bosses are ~5y, adds ~1y. Change here if a fight disagrees.
	private static float Hitbox(IBattleChara c) => c.BaseId is 0x459B or 0x459F or 0x45A7 or 0x45A9 or 0x45AB or 0x45AF ? 5f : 1f;

	/// <summary>Who an AoE reaches: circles and lines, or the production target resolver's 120 degree cone with hitbox adjustment.</summary>
	public static IBattleChara[] AffectedBy(ActionSpec spec, IBattleChara target)
	{
		var main = Enemies.FirstOrDefault(e => e.Chara == target);
		if (spec.Shape == AoeShape.Single || main == null) return [target];
		var (tx, ty) = main.Position;
		var (px, py) = (Enemy.Sample(Timeline.PlayerPos, 1, 100f), Enemy.Sample(Timeline.PlayerPos, 2, 100f));
		return Enemies.Where(e => Hostiles.Contains(e.Chara)).Where(e =>
		{
			if (e.Chara == target) return true;
			var (x, y) = e.Position;
			var r = Hitbox(e.Chara);
			if (spec.Shape == AoeShape.CircleOnTarget) return Math.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty)) <= spec.Radius + r;
			if (spec.Shape == AoeShape.Line)
			{
				var len = Math.Max(Math.Sqrt((tx - px) * (tx - px) + (ty - py) * (ty - py)), 0.1);
				var along = ((x - px) * (tx - px) + (y - py) * (ty - py)) / len;
				var across = Math.Abs((x - px) * (ty - py) - (y - py) * (tx - px)) / len;
				return along >= -r && along <= spec.Radius + r && across <= 2 + r;
			}
			var dx = tx - px;
			var dy = ty - py;
			var aimLength = Math.Sqrt(dx * dx + dy * dy);
			double ex = x - px;
			double ey = y - py;
			var distance = Math.Sqrt(ex * ex + ey * ey);
			if (distance - r > spec.Radius) return false;
			if (aimLength <= 0.001) return true;
			// ActionTargetInfo.GetCanTarget widens by the main target's hitbox along the aim direction.
			const double halfAngle = Math.PI / 3;
			var adjustment = Hitbox(target) / Math.Sin(halfAngle);
			ex += dx / aimLength * adjustment;
			ey += dy / aimLength * adjustment;
			var adjustedLength = Math.Sqrt(ex * ex + ey * ey);
			return adjustedLength <= 0.001 || (dx * ex + dy * ey) / (aimLength * adjustedLength) >= Math.Cos(halfAngle);
		}).Select(e => e.Chara).OrderByDescending(c => c == target).ToArray();
	}

	/// <summary>
	/// BossMod's plan entries in the look-ahead. Its timeline cannot see past a kill-gated phase end (Fatebreaker's death,
	/// the P4 pair's): a window in the next phase stays hidden until that phase is entered, at the preceding kill. Timelines
	/// written before this was modelled (the BRD ones validated without it) say nothing and see every window.
	/// </summary>
	public static List<BMRPlannedAction> PlannedActions(float lookAhead) => Timeline.PlanWindows
		.Where(w => w.End > Now && w.Start - Now <= lookAhead && (!Timeline.PlanHiddenAcrossKills || _downtimeForecast.IsInCurrentPhase(Now, w.Start)))
		.Select(w => new BMRPlannedAction($"BossMod.Autorotation.Class{Job.Abbr}Utility", "Burst", w.Potion ? "WindowWithPotion" : "Window", 0, 0, w.Start - Now, w.End - Now, 0, 0))
		.ToList();

	// ObjectHelper.GetTTK (cached per step: the record only changes once a second)
	public static float TimeToKill(IBattleChara chara)
	{
		if (TargetTtkOverride is { } targetTtk) return targetTtk(chara);
		if (TtkOverride is float ttk) return ttk;
		if (_ttkCachedAt != Now) { _ttkCache.Clear(); _ttkCachedAt = Now; }
		if (_ttkCache.TryGetValue(chara.GameObjectId, out var cached)) return cached;
		return _ttkCache[chara.GameObjectId] = ComputeTimeToKill(chara);
	}

	private static float ComputeTimeToKill(IBattleChara chara)
	{
		const int window = 5;
		var start = (SimDateTime?)null;
		var initial = 0f;
		var ratios = new List<float>();
		foreach (var (time, dict) in HpRecord)
		{
			if (dict != null && dict.TryGetValue(chara.GameObjectId, out var ratio) && ratio != 1f)
			{
				if (start == null) { start = time; initial = ratio; }
				ratios.Add(ratio);
				if (ratios.Count > window) ratios.RemoveAt(0);
			}
		}
		if (start == null || Now - start.Value.Seconds < 2.5f) return float.NaN;
		var current = chara.GetHealthRatio();
		var avg = ratios.Count > 0 ? ratios.Average() : 0f;
		var diff = initial - avg;
		if (diff <= 0) return float.NaN;
		return (float)(Now - start.Value.Seconds) / diff * current;
	}

	// DataCenter.AverageTTK
	public static float AverageTTK
	{
		get
		{
			if (AverageTtkOverride is float average) return average;
			var ttks = Hostiles.Select(TimeToKill).Where(t => !float.IsNaN(t)).ToList();
			return ttks.Count > 0 ? ttks.Average() : 0f;
		}
	}

	/// <summary>Advance the clock: statuses tick down, the log's world state is replayed, the job's own clock runs.</summary>
	public static void Step(float dt)
	{
		Now += dt;
		foreach (var chara in Enemies.Select(e => e.Chara).Append(Player))
		{
			foreach (var key in chara.Statuses.Keys.ToList())
			{
				if ((chara.Statuses[key] -= dt) <= 0f) chara.Statuses.Remove(key);
			}
		}
		if (Timeline.PlayerDebuffs.Any(d => d.Name == "Dead" && Now >= d.Start && Now - dt < d.Start))
		{
			Player.Statuses.Clear(); // death drops every buff and the job gauge
			Job.OnDeath();
		}
		Refresh();
		Job.Advance(dt);
		AutoAttack();
	}

	private static Dictionary<string, List<EnemyCast>> _castsByKey = [];
	private static readonly Dictionary<ulong, float> _ttkCache = [];
	private static float _ttkCachedAt = -1;
	private static float _nextAuto;
	private static int _targetCursor;
	private static Enemy? EnemyByKey(string? key) => key == null ? null : Enemies.Find(e => e.Actor.Key == key);

	private static void AutoAttack()
	{
		if (Now < _nextAuto || Target == null || Locked) return;
		_nextAuto = Now + Job.AutoDelay;
		Hit(Target, Job.AutoPotency, "Shot");
	}

	private static void Refresh()
	{
		var wasHostile = Hostiles.ToHashSet();
		Hostiles = Enemies.Where(e => e.Targetable).Select(e => e.Chara).ToList();
		foreach (var gone in wasHostile.Where(h => !Hostiles.Contains(h))) gone.Statuses.Clear(); // a jump or phase change drops our DoTs

		// The logged target if attackable now, else the next one the player picked, else any enemy.
		var rows = Timeline.PlayerTarget;
		while (_targetCursor < rows.Count && rows[_targetCursor][0].GetSingle() <= Now) _targetCursor++;
		Target = null;
		for (var i = Math.Max(0, _targetCursor - 1); i < rows.Count && Target == null && i < _targetCursor + 40; i++)
		{
			var e = EnemyByKey(rows[i][1].GetString());
			if (e != null && Hostiles.Contains(e.Chara)) Target = e.Chara;
		}
		Target ??= Hostiles.FirstOrDefault();

		foreach (var e in Enemies)
		{
			var cast = _castsByKey.GetValueOrDefault(e.Actor.Key)?.Find(c => Now >= c.Start && Now < c.Start + c.Duration);
			e.Chara.CastActionId = cast?.Id ?? 0;
			e.Chara.TotalCastTime = cast?.Duration ?? 0;
			e.Chara.CurrentCastTime = cast == null ? 0 : Now - cast.Start;
		}

		foreach (var b in Timeline.PartyBuffs)
		{
			if (Now >= b.Start && Now < b.End && Enum.TryParse<StatusID>(b.Name.Replace(" ", ""), out var id)) Player.Statuses[id] = b.End - Now;
		}
		foreach (var d in Timeline.PlayerDebuffs)
		{
			var id = d.Name switch { "Return" => StatusID.Return, "Stun" => StatusID.Stun_4163, "Doom" => StatusID.Doom, _ => StatusID.None };
			if (id != StatusID.None && Now >= d.Start && Now < d.End) Player.Statuses[id] = d.End - Now;
		}
		foreach (var d in Timeline.EnemyDebuffs ?? [])
		{
			if (Now >= d.Start && Now < d.End && EnemyByKey(d.Key) is { } e && Enum.TryParse<StatusID>(d.Name.Replace(" ", ""), out var id)) e.Chara.Statuses[id] = d.End - Now;
		}

		if (Now - _hpRecordedAt >= 1f && Hostiles.Count > 0) // TargetUpdater.UpdateTimeToKill: 1/s, 240 kept
		{
			_hpRecordedAt = Now;
			if (HpRecord.Count >= 240) HpRecord.Dequeue();
			HpRecord.Enqueue((new SimDateTime(Now), Hostiles.ToDictionary(h => h.GameObjectId, h => h.GetHealthRatio())));
		}
	}

	/// <summary>Press an action the rotation returned. GCDs roll the GCD; everything locks for the animation.</summary>
	public static void Execute(IAction action)
	{
		_lastActionAt = Now;
		LastAction = action;
		if (action is MedicineItem)
		{
			_potionReadyAt = Now + PotionRecast;
			_lockUntil = Now + ItemLockSeconds;
			Player.Statuses[StatusID.Medicated] = 30f;
			LastAbility = action;
			Casts.Add((Now, "Potion", null, 0));
			return;
		}
		var baseAction = (BaseAction)action;
		var spec = Job.Spec(Job.Adjusted(baseAction.ActionId));
		if (spec.IsGcd)
		{
			_gcdTotal = spec.GcdRecast > 0 ? spec.GcdRecast : (float)Math.Round(BaseGcd * Job.HasteFactor, 2);
			_gcdReadyAt = Now + _gcdTotal;
			LastGcd = action;
		}
		else
		{
			LastAbility = action;
		}
		if (!(spec.IsGcd && spec.Recast == 0)) Cooldown(spec.Id).Use();
		_lockUntil = Now + AnimationLockSeconds;
		var target = baseAction.Target.Target;
		Casts.Add((Now, spec.Id.ToString(), target == null || target == Player ? null : Enemies.First(e => e.Chara == target).Actor.Key, 0));
		Job.Execute(spec, target, baseAction.Target.AffectedTargets ?? []);
	}

	/// <summary>Expected damage now, in potency units: party buffs from the log times the job's own multiplier.</summary>
	public static float Multiplier(IBattleChara? target = null)
	{
		var m = Job.SelfMultiplier();
		foreach (var b in Timeline.PartyBuffs)
		{
			if (Now >= b.Start && Now < b.End) m *= b.Mult;
		}
		if (Player.HasStatus(true, StatusID.Medicated)) m *= PotionMultiplier;
		if (Timeline.PlayerDebuffs.Any(d => d.Name == "Weakness" && Now >= d.Start && Now < d.End)) m *= 0.75f; // measured: 0.76 after a raise
		if (target != null)
		{
			if (Job is Bard bard) m *= bard.TargetCritMultiplier(target);
			var enemy = Enemies.Find(e => e.Chara == target);
			foreach (var d in Timeline.EnemyDebuffs ?? [])
				if (d.Key == enemy?.Actor.Key && Now >= d.Start && Now < d.End) m *= d.Mult;
		}
		return m;
	}

	/// <summary>Adds a note to the last traced cast (e.g. Codas consumed).</summary>
	public static void Annotate(string note) => Casts[^1] = Casts[^1] with { Action = Casts[^1].Action + " (" + note + ")" };

	public static void Hit(IBattleChara target, float potency, string name, float? snapshot = null)
	{
		if (!Hostiles.Contains(target)) return;
		var enemy = Enemies.Find(e => e.Chara == target);
		var damage = potency * (snapshot ?? Multiplier(target)) * (enemy?.Actor.Taken ?? 1f);
		if (snapshot == null && name != "Shot" && Casts.Count > 0 && Casts[^1].Time == Now) Casts[^1] = Casts[^1] with { Damage = Casts[^1].Damage + damage };
		DamageByAction[name] = DamageByAction.GetValueOrDefault(name) + damage;
		DamageEvents.Add((Now, damage));
	}
}
