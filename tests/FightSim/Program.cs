// FightSim: runs a real RSR rotation through a whole logged fight and reports its expected DPS next to the log's.
//
//   dotnet run --project tests/FightSim -- <timeline.json> [--seeds 20] [--config RotationSolver.json] [--trace]
//
// Timelines come from timeline.py (FFLogs kill -> enemies, party buffs, lockouts, plan windows, the player's hits).
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FightSim;
using RotationSolver.Basic.Configuration.RotationConfig;
using RotationSolver.RebornRotations.Ranged;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var path = args.FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("usage: FightSim <timeline.json> [--seeds N] [--config RotationSolver.json] [--trace]");
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var seeds = int.Parse(Opt("--seeds") ?? "20");
var trace = args.Contains("--trace");
var configPath = Opt("--config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "RotationSolver.json");

var timeline = Timeline.Load(path);
var crit = timeline.Stats.GetValueOrDefault("crit", 0.2f);
var dh = timeline.Stats.GetValueOrDefault("dh", 0.2f);
Console.WriteLine($"{timeline.Source} {timeline.Player}: {timeline.Duration:F0}s, crit {crit:P1} DH {dh:P1}, {timeline.PlanWindows.Count} plan windows");

var config = File.Exists(configPath) ? JsonDocument.Parse(File.ReadAllText(configPath)).RootElement : default;
Console.WriteLine(config.ValueKind == JsonValueKind.Object ? $"RSR config: {configPath}" : "RSR config: none, rotation defaults");

var phases = Phases(timeline);
var runs = new List<(double Total, Dictionary<string, double> ByAction, double[] ByPhase)>();
for (var seed = 0; seed < seeds; seed++)
{
	Game.Reset(timeline, new Bard(crit, dh), seed);
	var rotation = new BRD_Plan();
	ApplyConfig(rotation, "BRD", "RotationSolver.RebornRotations.Ranged.BRD_Plan");
	Run(rotation);
	var byPhase = phases.Select(p => Game.DamageEvents.Where(e => e.Time >= p.Start && e.Time < p.End).Sum(e => e.Damage)).ToArray();
	runs.Add((Game.DamageEvents.Sum(e => e.Damage), Game.DamageByAction.GroupBy(kv => Norm(kv.Key)).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)), byPhase));
	if (trace && seed == 0)
	{
		foreach (var (t, a, target, damage) in Game.Casts) Console.WriteLine($"  {t,7:F2} {a}{(target == null ? "" : " -> " + target)}{(damage > 0 ? $"  {damage:F0}" : "")}");
	}
}

// The log on the same scale: each hit with its crit/DH luck swapped for the expected value, then converted to potency
// with the factor that makes the log's Burst Shots average 220 under the same multipliers the sim uses.
var log = LogPotency(timeline, crit, dh);
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

// Steps the fight: the GCD when it is ready, otherwise a weave, the way CustomRotation.Invoke picks between them.
static void Run(BRD_Plan rotation)
{
	const float dt = 0.02f;
	var noGcd = BaseAction.Create(ActionID.None, (ref ActionSetting _) => { }); // CustomRotation passes AddlePvE when there is no GCD
	var weaves = 0; // DataCenter.Weaves: two oGCDs, then nothing but a GCD (reset only by a GCD)
	while (Game.Now < Game.Timeline.Duration)
	{
		Game.Step(dt);
		if (!Game.InCombat || Game.Locked || Game.AnimationLock > 0f) continue;
		var gcd = rotation.NextGcd();
		if (gcd != null && Game.GcdRemain <= 0f)
		{
			Game.Execute(gcd);
			weaves = 0;
			continue;
		}
		var ability = rotation.NextAbility(gcd ?? noGcd);
		if (ability != null && weaves < 2)
		{
			Game.Execute(ability);
			weaves++;
		}
	}
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
			object value = prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, text)
				: prop.PropertyType == typeof(bool) ? bool.Parse(text)
				: prop.PropertyType == typeof(float) ? float.Parse(text, CultureInfo.InvariantCulture)
				: text;
			prop.SetValue(rotation, value);
		}
	}
	if (config.GetProperty("_rotationActionConfigDict").TryGetProperty(job, out var actions))
	{
		foreach (var prop in typeof(BardRotation).GetProperties(BindingFlags.Static | BindingFlags.Public).Where(p => p.PropertyType == typeof(IBaseAction)))
		{
			var action = (BaseAction)prop.GetValue(null)!;
			if (!actions.TryGetProperty(Bard.GameId(action.ActionId).ToString(), out var c)) continue;
			action.Config.IsEnabled = c.GetProperty("IsEnabled").GetBoolean();
			action.Config.AoeCount = c.GetProperty("AoeCount").GetByte();
			action.Config.TimeToKill = c.GetProperty("TimeToKill").GetSingle();
			action.Config.StatusRefreshGcdCount = c.GetProperty("StatusRefreshGcdCount").GetByte();
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

static (double Total, Dictionary<string, double> ByAction, Func<float, float, double> ByPhase) LogPotency(Timeline t, float crit, float dh)
{
	var critDamage = 1.4f + crit - 0.05f;
	var casts = t.LoggedCasts.Select(c => (T: c[0].GetSingle(), Name: c[1].GetString()!)).ToList();
	bool Within(string name, float seconds, float at) => casts.Any(c => c.Name.Contains(name) && at >= c.T && at < c.T + seconds);
	// B: damage buffs, E: expected crit/DH. A hit is potency x k x B x luck; in sim units it is potency x B x E.
	double B(float at)
	{
		var m = 1.0;
		if (Within("Raging Strikes", 20, at)) m *= 1.15;
		if (Within("Radiant Finale", 20, at)) m *= 1.06; // ponytail: assumes 3 Codas, as nearly every logged Finale had
		if (Within("Gemdraught", 30, at)) m *= Game.PotionMultiplier;
		foreach (var b in t.PartyBuffs) if (at >= b.Start && at < b.End) m *= b.Mult;
		return m;
	}
	double E(float at) => (1 + crit * (critDamage - 1)) * (1 + 0.25 * (dh + (Within("Battle Voice", 20, at) ? 0.2 : 0)));
	var hits = t.LoggedHits.Select(h => (T: h[0].GetSingle(), Name: Norm(h[1].GetString()! + (h[5].GetBoolean() ? " (DoT)" : "")),
		Base: h[2].GetDouble() / (h[3].GetBoolean() ? critDamage : 1) / (h[4].GetBoolean() ? 1.25 : 1))).ToList();
	var k = hits.Where(h => h.Name == "BurstShot").Average(h => h.Base / (220 * B(h.T)));
	var potency = hits.Select(h => (h.T, h.Name, P: h.Base * E(h.T) / k)).ToList();
	return (potency.Sum(p => p.P), potency.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Sum(p => p.P)),
		(a, b) => potency.Where(p => p.T >= a && p.T < b).Sum(p => p.P));
}
