// FightSim: runs a real RSR rotation through a whole logged fight and reports its expected DPS next to the log's.
//
//   dotnet run --project tests/FightSim -- <timeline.json> [--seeds 20] [--config RotationSolver.json] [--rotation MCH_SyncPlus] [--trace]
//
// The job comes from the timeline (BRD when it does not say); the rotation defaults to BRD_Plan / MCH_Plan.
// Timelines come from timeline.py (FFLogs kill -> enemies, party buffs, lockouts, plan windows, the player's hits).
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FightSim;
using RotationSolver.Basic.Configuration.RotationConfig;
using RotationSolver.Basic.Rotations;
using RotationSolver.RebornRotations.Ranged;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Contains("--check-mch-preparation"))
{
	MchQueenDecisionCheck.RunPreparation();
	return 0;
}
if (args.Contains("--check-mch-queen") || args.Contains("--check-mch-phase-entry"))
{
	MchQueenDecisionCheck.Run();
	return 0;
}
if (args.Contains("--check-mch-queen-model"))
{
	MchQueenModelCheck.Run();
	return 0;
}

if (args.Contains("--check-final-kill"))
{
	FinalKillCheck.Run();
	return 0;
}
if (args.Contains("--check-downtime"))
{
	DowntimeForecastCheck.Run();
	return 0;
}

var path = args.FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("usage: FightSim <timeline.json> [--seeds N] [--config RotationSolver.json] [--rotation NAME] [--trace]");
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var seeds = int.Parse(Opt("--seeds") ?? "20");
var trace = args.Contains("--trace");
var checkCtCodas = args.Contains("--check-ct-codas");
var seedReport = args.Contains("--seed-report");
var replayUtilities = args.Contains("--replay-utilities");
var seedStart = int.Parse(Opt("--seed-start") ?? "0");
var configPath = Opt("--config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "RotationSolver.json");

var timeline = Timeline.Load(path);
if (args.Contains("--check-brd-apex-policy"))
{
	BardApexPolicyCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-application-timing"))
{
	ApplicationTimingCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-reference-calibration"))
{
	ReferenceCalibrationCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-return-positions"))
{
	ReturnPositionCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-geometry"))
{
	GeometryCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-dot-targeting"))
{
	DotTargetingCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-ct-recovery"))
{
	BardCtRecoveryCheck.Run(timeline);
	return 0;
}
if (args.Contains("--check-brd-model"))
{
	BardDamageCheck.Run(timeline);
	return 0;
}
var crit = timeline.Stats.GetValueOrDefault("crit", 0.2f);
var dh = timeline.Stats.GetValueOrDefault("dh", 0.2f);
var jobName = timeline.Job ?? "BRD";
var rotationType = typeof(BRD_Plan).Assembly.GetTypes().First(t => t.Name == (Opt("--rotation") ?? (jobName == "MCH" ? nameof(MCH_Plan) : nameof(BRD_Plan))));
IJob NewJob() => jobName == "MCH" ? new Machinist(crit, dh) : new Bard(crit, dh);
Game.BaseGcd = jobName == "MCH" ? 2.50f : 2.48f; // measured from the logs' GCD gaps
Console.WriteLine($"{timeline.Source} {timeline.Player} {jobName} {rotationType.Name}: {timeline.Duration:F0}s, crit {crit:P1} DH {dh:P1}, {timeline.PlanWindows.Count} plan windows");

var config = File.Exists(configPath) ? JsonDocument.Parse(File.ReadAllText(configPath)).RootElement : default;
if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("_action6headDict", out var aheadByJob) && aheadByJob.TryGetProperty(jobName, out var aheadValue))
	Game.ActionAhead = aheadValue.GetSingle();
Console.WriteLine(config.ValueKind == JsonValueKind.Object ? $"RSR config: {configPath}" : "RSR config: none, rotation defaults");

var phases = Phases(timeline);
var runs = new List<(double Total, Dictionary<string, double> ByAction, double[] ByPhase)>();
var ctFinaleRuns = 0;
for (var seed = seedStart; seed < seedStart + seeds; seed++)
{
	Game.Reset(timeline, NewJob(), seed);
	var rotation = (CustomRotation)Activator.CreateInstance(rotationType)!;
	ApplyConfig(rotation, jobName, rotationType.FullName!);
	Driver.Run(rotation, seed, replayUtilities && jobName == "BRD");
	if (checkCtCodas && BardCtCheck.Validate(timeline, seed)) ctFinaleRuns++;
	var byPhase = phases.Select(p => Game.DamageEvents.Where(e => e.Time >= p.Start && e.Time < p.End).Sum(e => e.Damage)).ToArray();
	runs.Add((Game.DamageEvents.Sum(e => e.Damage), Game.DamageByAction.GroupBy(kv => Norm(kv.Key)).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)), byPhase));
	if (seedReport)
		Console.WriteLine("SEED " + JsonSerializer.Serialize(new { seed, total = runs[^1].Total / timeline.Duration,
			phase = byPhase, byAction = runs[^1].ByAction,
			casts = Game.Casts.Select(c => new { time = c.Time, action = c.Action, damage = c.Damage }),
			applications = Game.DamageApplications }));
	if (trace && seed == seedStart)
	{
		foreach (var (t, a, target, damage) in Game.Casts) Console.WriteLine($"  {t,7:F2} {a}{(target == null ? "" : " -> " + target)}{(damage > 0 ? $"  {damage:F0}" : "")}");
	}
}

if (checkCtCodas)
{
	if (ctFinaleRuns == 0) throw new InvalidOperationException("No seed executed the corrected CT Finale path.");
	Console.WriteLine($"BRD CT outcomes: {seeds} seeds passed; {ctFinaleRuns} executed CT Finale/Encore (three P5 Codas, burst windows and song coverage preserved).");
}

// The log on the same scale: each hit with its crit/DH luck swapped for the expected value, in the job's potency units.
var log = LogTotals(NewJob().LogPotency(timeline));
if (seedReport)
	Console.WriteLine("REFERENCE " + JsonSerializer.Serialize(new { total = log.Total / timeline.Duration,
		phase = phases.Select(p => log.ByPhase(p.Start, p.End)) }));
var sim = runs.Average(r => r.Total);
Console.WriteLine();
Console.WriteLine($"{"",-14}{"Sim",10}{"Log",10}{"Diff",8}   (potency/s, expected crits; sim = mean of {seeds} seeds, sd {Sd(runs.Select(r => r.Total / timeline.Duration)):F1})");
Row("Whole fight", sim / timeline.Duration, log.Total / timeline.Duration);
for (var i = 0; i < phases.Count; i++)
{
	var (name, start, end) = phases[i];
	Row($"{name} {start:F0}-{end:F0}", runs.Average(r => r.ByPhase[i]) / (end - start), log.ByPhase(start, end) / (end - start));
}
Console.WriteLine();
Console.WriteLine($"{"Action",-22}{"Sim",10}{"Log",10}{"Diff",10}   (total potency, top 15)");
foreach (var name in runs.SelectMany(r => r.ByAction.Keys).Concat(log.ByAction.Keys).Distinct()
	.OrderByDescending(n => Math.Max(runs.Average(r => r.ByAction.GetValueOrDefault(n)), log.ByAction.GetValueOrDefault(n))).Take(15))
{
	var s = runs.Average(r => r.ByAction.GetValueOrDefault(name));
	var l = log.ByAction.GetValueOrDefault(name);
	Console.WriteLine($"{name,-22}{s,10:F0}{l,10:F0}{s - l,10:F0}");
}
return 0;

void Row(string name, double s, double l) => Console.WriteLine($"{name,-14}{s,10:F1}{l,10:F1}{(s - l) / l,8:P1}");

static double Sd(IEnumerable<double> xs)
{
	var list = xs.ToList();
	var mean = list.Average();
	return Math.Sqrt(list.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, list.Count - 1));
}

// Copies the user's RSR settings: the rotation's [RotationConfig] properties and the per-action configs (enabled, AoE count, TTK, refresh).
void ApplyConfig(object rotation, string job, string rotationType)
{
	if (config.ValueKind != JsonValueKind.Object) return;
	if (config.GetProperty("_rotationConfigurationsDict").TryGetProperty(job, out var byRotation)
		&& byRotation.TryGetProperty(rotationType, out var values))
	{
		foreach (var prop in rotation.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Where(p => p.GetCustomAttribute<RotationConfigAttribute>() != null && p.CanWrite))
		{
			if (!values.TryGetProperty(prop.Name, out var v)) continue;
			var text = v.ToString();
			// RotationConfigBase: an enum is saved by name or by its [Description].
			object value = prop.PropertyType.IsEnum ? (prop.PropertyType.GetFields(BindingFlags.Public | BindingFlags.Static)
					.FirstOrDefault(f => f.Name == text || f.GetCustomAttribute<DescriptionAttribute>()?.Description == text)?.GetValue(null)
					?? throw new InvalidDataException($"{prop.Name}: no {prop.PropertyType.Name} '{text}'"))
				: prop.PropertyType == typeof(bool) ? bool.Parse(text)
				: prop.PropertyType == typeof(float) ? float.Parse(text, CultureInfo.InvariantCulture)
				: text;
			prop.SetValue(rotation, value);
		}
	}
	if (config.GetProperty("_rotationActionConfigDict").TryGetProperty(job, out var actions))
	{
		var jobRotation = job == "MCH" ? typeof(MachinistRotation) : typeof(BardRotation);
		foreach (var prop in jobRotation.GetProperties(BindingFlags.Static | BindingFlags.Public).Where(p => p.PropertyType == typeof(IBaseAction)))
		{
			var action = (BaseAction)prop.GetValue(null)!;
			var gameId = job == "MCH" ? Machinist.GameId(action.ActionId) : Bard.GameId(action.ActionId);
			if (!actions.TryGetProperty(gameId.ToString(), out var c)) continue;
			// BaseAction.Config: a stored AoE or refresh count only stands while the rotation's default it was set against is unchanged.
			byte? Applied(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetByte() : null;
			var defaults = action.Setting.CreateConfig?.Invoke() ?? new ActionConfig();
			action.Config.IsEnabled = c.GetProperty("IsEnabled").GetBoolean();
			action.Config.TimeToKill = c.GetProperty("TimeToKill").GetSingle();
			action.Config.AoeCount = c.GetProperty("AoeResetDone").GetBoolean() && Applied("AppliedDefaultAoeCount") == defaults.AoeCount
				? c.GetProperty("AoeCount").GetByte() : defaults.AoeCount;
			action.Config.StatusRefreshGcdCount = Applied("AppliedDefaultStatusRefreshGcdCount") == defaults.StatusRefreshGcdCount
				? c.GetProperty("StatusRefreshGcdCount").GetByte() : defaults.StatusRefreshGcdCount;
		}
	}
}

// FRU phases by the first appearance of each phase's boss.
static List<(string Name, float Start, float End)> Phases(Timeline t)
{
	var starts = new (string, uint[])[] { ("P1", [0x459B]), ("P2", [0x459F]), ("P3", [0x45A7]), ("P4", [0x45A9, 0x45AB]), ("P5", [0x45AF]) }
		.Select(p => (p.Item1, Start: t.Actors.Where(a => p.Item2.Contains(a.BaseId)).Select(a => a.Spans[0][0]).DefaultIfEmpty(float.NaN).Min()))
		.Where(p => !float.IsNaN(p.Start)).ToList();
	return starts.Select((p, i) => (p.Item1, i == 0 ? 0f : p.Start, i + 1 < starts.Count ? starts[i + 1].Start : t.Duration)).ToList();
}

static string Norm(string name) => name.Replace(" ", "").Replace("'", "");

static (double Total, Dictionary<string, double> ByAction, Func<float, float, double> ByPhase) LogTotals(List<(float T, string Name, double P)> hits)
	=> (hits.Sum(h => h.P), hits.GroupBy(h => Norm(h.Name)).ToDictionary(g => g.Key, g => g.Sum(h => h.P)),
		(a, b) => hits.Where(h => h.T >= a && h.T < b).Sum(h => h.P));

internal static class Driver
{
	// Steps the fight: the GCD when it is ready, otherwise a weave, the way CustomRotation.Invoke picks between them.
	public static void Run(CustomRotation rotation, int seed, bool replayUtilities, Action? afterStep = null)
	{
		const float dt = 0.02f;
		var noGcd = BaseAction.Create(ActionID.None, (ref ActionSetting _) => { }); // CustomRotation passes AddlePvE when there is no GCD
		var weaves = new RotationSolver.Basic.Helpers.WeaveBudget();
		var utility = replayUtilities ? new UtilityWeaveReplay(Game.Timeline) : null;
		ushort sourceSequence = 0;
		uint effectSequence = 0;
		bool Use(IAction action, bool nativeUtility = false)
		{
			var isGcd = action is BaseAction a && Game.Job.Spec(Game.Job.Adjusted(a.ActionId)).IsGcd;
			var sequence = ++sourceSequence;
			var accepted = weaves.TryUse(true, isGcd, false, action.ID, () =>
			{
				Game.Execute(action);
				if (!isGcd) weaves.RecordRequest(action.ID, sequence, Game.Now);
				return true;
			}, utility?.Reserved ?? 0, nativeUtility, Game.Now);
			if (accepted) weaves.Record(isGcd, action.ID, ++effectSequence, sequence, Game.Now);
			return accepted;
		}
		IAction? queued = null; // ActionHelper.CanUseGCD: within the action-ahead window RSR presses the GCD and the game queues it
		while (Game.Now < Game.Timeline.Duration)
		{
			Game.Step(Math.Min(dt, Game.Timeline.Duration - Game.Now));
			afterStep?.Invoke();
			if (!Game.InCombat || Game.Locked) { queued = null; continue; }
			if (Game.AnimationLock > 0f) continue;
			var gcd = rotation.NextGcd();
			if (utility != null) utility.HasGcd = gcd != null;
			weaves.RefreshIdleWindow(gcd != null, Game.GcdTotal, Game.Now);
			if (utility?.Due() is { } nativeUtility && Use(nativeUtility, true))
			{
				utility.Executed(nativeUtility.ActionId);
				continue;
			}
			if (queued != null)
			{
				if (Game.GcdRemain > 0f || (utility?.HoldGcd == true && !weaves.Exhausted)) continue;
				// The game drops a queued action whose target is gone.
				if (queued is BaseAction q && (q.Target.Target is not { } t || Game.InRange(t))) Use(queued);
				queued = null;
				continue;
			}
			if (gcd != null && (utility?.HoldGcd != true || weaves.Exhausted) && Game.GcdRemain <= Game.GcdTotal * Game.ActionAhead)
			{
				queued = gcd;
				continue;
			}
			var ability = rotation.NextAbility(gcd ?? noGcd);
			if (ability != null) Use(ability);
		}
		utility?.Validate(seed);
	}
}
