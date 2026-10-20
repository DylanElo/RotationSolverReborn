using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Rotations;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>Production selectors with only native game/IPC facts supplied by the FightSim boundary.</summary>
internal static class MchQueenDecisionCheck
{
	private static int _checks;
	private static readonly List<string> Failures = [];
	private static readonly IBaseAction NoGcd = BaseAction.Create(None, (ref ActionSetting _) => { });

	public static void Run()
	{
		PrintSources();
		Case("object creation precedes target visibility", () =>
		{
			var actor = new TimelineActor("future", 0x459F, "P2 body", [[0, 2000]], [[0, 1]], [[0, 100, 100]], null, PresentFrom: 102);
			var fixture = new Timeline("native object creation", "MCH", 2000, [actor], [[0, 100, 100]], [], [], [], [], [], 0, [], [], [], "MCH", ["MCH"], []);
			Reset(0x459F, 100, timeline: fixture);
			var id = Game.Enemies[0].Chara.GameObjectId;
			Require(Game.FindObject(id) == null && !RotationSolver.Basic.DataCenter.AllTargets.Any() && !Game.Hostiles.Any(), "future timeline actors are absent from lookup, targetable AllTargets and hostile selection");
			Game.Step(2);
			Require(Game.FindObject(id)?.BaseId == 0x459F && RotationSolver.Basic.DataCenter.AllTargets.Single().GameObjectId == id && Game.Hostiles.Single().GameObjectId == id, "the same actor becomes visible to all native object boundaries at its observed creation time");
		});
		Case("terminal Detonator wins the pet weave", () =>
		{
			var rotation = ActiveQueenWildfire();
			Game.TtkOverride = 1;
			Require(MCH_Reborn.DetonatorPvE.CanUse(out _, skipTTKCheck: true)
				&& MCH_Reborn.QueenOverdrivePvE.CanUse(out _, skipTTKCheck: true, targetOverride: TargetType.Self),
				"the native boundary makes both terminal Detonator and Queen Overdrive executable in the same weave");
			var first = Select(rotation, "EmergencyAbility");
			Require(first?.ID == (uint)DetonatorPvE,
				"terminal Wildfire damage takes the last available weave before an optional Queen Overdrive");
			if (first?.ID == (uint)DetonatorPvE)
			{
				Game.Execute(first);
				Require(Math.Abs(Game.DamageByAction.GetValueOrDefault("Wildfire") - 4 * 240) < .1,
					"the selected Detonator actually saves the four accumulated Wildfire hits");
				Game.Step(.7f);
				Require(Select(rotation, "EmergencyAbility")?.ID == (uint)QueenOverdrivePvE,
					"the next available pet weave remains executable after terminal Wildfire damage is confirmed");
			}
		});
		Case("terminal Detonator unavailable permits pet fallback", () =>
		{
			var rotation = ActiveQueenWildfire();
			Game.TtkOverride = 1;
			MCH_Reborn.DetonatorPvE.Config.IsEnabled = false;
			Require(Select(rotation, "EmergencyAbility")?.ID == (uint)QueenOverdrivePvE,
				"an unavailable native Detonator does not suppress an otherwise executable emergency Overdrive");
		});
		Case("nonterminal Wildfire does not consume pet priority", () =>
		{
			var rotation = ActiveQueenWildfire();
			Game.TtkOverride = 5;
			Require(Select(rotation, "EmergencyAbility")?.ID == (uint)QueenOverdrivePvE,
				"a nonterminal Wildfire leaves the existing valid Overdrive decision unchanged");
		});
		foreach (var fullMetal in new[] { true, false })
		Case("nonterminal pet weave preserves burst handoff " + fullMetal, () =>
		{
			var rotation = ActiveQueenWildfire();
			Game.TtkOverride = 5;
			if (fullMetal) Game.Player.Statuses[StatusID.FullMetalMachinist] = 20;
			else
			{
				Game.Step(.8f);
				MCH_Reborn.BlazingShotPvE.CanUse(out var fifth);
				Game.Execute(fifth);
				Game.Step(.7f);
			}
			Require(Select(rotation, "EmergencyAbility")?.ID != (uint)QueenOverdrivePvE,
				"a nonterminal pet weave cannot displace the protected Full Metal or pending Hypercharge handoff");
		});
		Case("fresh P2 finite runway Queen", () =>
		{
			var rotation = Reset(0x459F, 170, hp: .998f);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (35, 60);
			Machinist.Gauge.Battery = 100;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)AutomatonQueenPvE,
				"fresh 99.8% P2 with a real 35s runway must not strand a full Queen waiting for HP-history TTK");
		});
		Case("fresh P2 generator waits one Queen weave", () =>
		{
			var rotation = Reset(0x459F, 170, hp: .998f);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (35, 60);
			Machinist.Gauge.Battery = 90;
			Game.Cooldown(AirAnchorPvE).FullAt = Game.Cooldown(DrillPvE).FullAt = 0;
			var first = rotation.NextGcd();
			Require(first?.ID == (uint)DrillPvE,
				"at 90 Battery a native-ready non-generating Drill leaves one weave for Queen before Air Anchor overcaps");
			if (first?.ID != (uint)DrillPvE) return;
			Game.Execute(first);
			Game.Step(.7f);
			var queen = rotation.NextAbility(rotation.NextGcd() ?? NoGcd);
			Require(queen?.ID == (uint)AutomatonQueenPvE, "the actual next weave executes Queen at the retained 90 Battery");
			if (queen?.ID != (uint)AutomatonQueenPvE) return;
			Game.Execute(queen);
			Require(Machinist.Gauge.Battery == 0, "confirmed Queen consumes the real 90 Battery before the generator");
			Game.Step(2);
			var anchor = rotation.NextGcd();
			Require(anchor?.ID == (uint)AirAnchorPvE, "Air Anchor is released immediately after the confirmed Queen");
			if (anchor?.ID == (uint)AirAnchorPvE) Game.Execute(anchor);
			Require(Machinist.Gauge.Battery == 20, "the executed Air Anchor generates all 20 Battery without overcap");
		});
		foreach (var reason in new[] { "Queen unavailable", "measured short lifetime", "short extra-GCD runway" })
		Case("generator does not wait for " + reason, () =>
		{
			var rotation = Reset(0x459F, 170, hp: .998f);
			Game.TtkOverride = reason == "measured short lifetime" ? 5 : float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (reason == "short extra-GCD runway" ? 24 : 35, 60);
			Machinist.Gauge.Battery = 90;
			Game.Cooldown(AirAnchorPvE).FullAt = Game.Cooldown(DrillPvE).FullAt = 0;
			if (reason == "Queen unavailable") MCH_Reborn.AutomatonQueenPvE.Config.IsEnabled = false;
			Require(rotation.NextGcd()?.ID == (uint)AirAnchorPvE,
				reason + " releases the native-ready generator instead of drifting it behind an unavailable Queen opportunity");
		});
		Case("uncast Queen preview cannot indefinitely defer generator", () =>
		{
			var rotation = Reset(0x459F, 170, hp: .998f);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (35, 60);
			Machinist.Gauge.Battery = 90;
			Game.Cooldown(AirAnchorPvE).FullAt = Game.Cooldown(DrillPvE).FullAt = 0;
			var first = rotation.NextGcd();
			Require(first?.ID == (uint)DrillPvE, "watchdog fixture first opens its actual non-Battery GCD weave");
			if (first?.ID != (uint)DrillPvE) return;
			Game.Execute(first);
			Game.Step(.7f);
			rotation.NextAbility(rotation.NextGcd() ?? NoGcd); // A recommendation without execution is not a confirmed Queen.
			Game.Step(1.9f);
			Require(rotation.NextGcd()?.ID == (uint)AirAnchorPvE && Machinist.Gauge.Battery == 90,
				"one elapsed GCD releases Air Anchor when the Queen weave never executed");
		});
		foreach (var forecast in new[] { 999f, float.MaxValue })
		Case("fresh Queen rejects unknown runway " + forecast, () =>
		{
			var rotation = Reset(0x459F, 170, hp: .998f);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (forecast, float.MaxValue);
			Machinist.Gauge.Battery = 100;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"an unknown/sentinel runway is not lifetime proof that bypasses the native Queen TTK veto");
		});
		Case("recent HP burn vetoes timeline-only Queen lifetime", () =>
		{
			var rotation = Reset(0x459F, 170, hp: .998f);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (35, 60);
			Game.Timeline.Actors[0].Hp.Add([171, .55f]);
			Game.Timeline.Actors[0].Hp.Add([173, .21f]);
			Game.Step(1);
			Game.Step(2);
			Machinist.Gauge.Battery = 100;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"actual recent HP burn toward the P2 check outranks a 35s timeline forecast when long-history TTK is unknown");
		});
		foreach (var scope in new[] { "timeline disabled", "outside FRU", "Ice Veil" })
		Case("timeline Queen fallback excludes " + scope, () =>
		{
			var rotation = Reset(scope == "Ice Veil" ? 0x45A5u : 0x459Fu, 170, hp: .998f);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (35, 60);
			Machinist.Gauge.Battery = 100;
			if (scope == "timeline disabled") Property(rotation, "UseEncounterTimeline")!.SetValue(rotation, false);
			if (scope == "outside FRU") Game.IsInFRU = false;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				scope + " cannot use an unrelated finite forecast to bypass the native unknown-TTK Queen veto");
		});
		Case("mature target preserves a second Air Anchor", () =>
		{
			var actor = new TimelineActor("Pandora", 0x45AF, "Pandora", [[0, 211.1f]], [[0, .5f]], [[0, 100, 100]], null);
			var fixture = new Timeline("known 41s tool lifetime", "MCH", 211.1f, [actor], [[0, 100, 100]], [], [], [], [], [], 0,
				[], [], [], "MCH", ["MCH", "PCT"], []);
			var rotation = Reset(0x45AF, 170, timeline: fixture);
			Game.TargetTtkOverride = _ => Math.Max(.01f, 211.1f - Game.Now);
			Machinist.Gauge.Battery = 90;
			Game.Cooldown(AirAnchorPvE).FullAt = Game.Cooldown(DrillPvE).FullAt = 0;
			Require(rotation.NextGcd()?.ID == (uint)AirAnchorPvE,
				"a mature measured 41s lifetime keeps the first Air Anchor on time instead of losing its second 40s use to Queen setup");
			RunProductionFor(rotation, 41.05f);
			Require(Game.Casts.Count(c => c.Action.StartsWith(nameof(AirAnchorPvE))) == 2,
				"actual production selectors execute both Air Anchors inside the mature target's measured lifetime");
		});
		Case("a new phase gets a fresh bounded Queen weave", () =>
		{
			TimelineActor Actor(string key, uint id, float start, float end) => new(key, id, key, [[start, end]], [[0, .998f]], [[0, 100, 100]], null);
			var fixture = new Timeline("bounded hold across phase change", "MCH", 2000,
				[Actor("P2", 0x459F, 0, 172.7f), Actor("P3", 0x45A7, 173, 2000)], [[0, 100, 100]], [], [], [], [], [], 0,
				[], [], [], "MCH", ["MCH", "PCT"], []);
			var rotation = Reset(0x459F, 170, timeline: fixture);
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 0;
			Game.ForecastOverride = (35, 60);
			Machinist.Gauge.Battery = 90;
			Game.Cooldown(AirAnchorPvE).FullAt = Game.Cooldown(DrillPvE).FullAt = 0;
			var first = rotation.NextGcd();
			Require(first?.ID == (uint)DrillPvE, "the departing phase first receives one real non-generating GCD opportunity");
			if (first?.ID != (uint)DrillPvE) return;
			Game.Execute(first);
			Game.Step(2.6f);
			Require(rotation.NextGcd()?.ID == (uint)AirAnchorPvE, "the failed departing-phase weave expires after one GCD");
			Game.Step(.5f);
			Require(Game.Target?.BaseId == 0x45A7 && rotation.NextGcd() is { } next && next.ID != (uint)AirAnchorPvE,
				"a genuinely new observed phase resets the expired watchdog and opens its own finite Queen weave opportunity");
		});
		Case("P2 false return", () =>
		{
			var rotation = Reset(0x459F, 350, active: false);
			Machinist.Gauge.Battery = 100;
			Game.ForecastOverride = (float.MaxValue, 3f);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				"100 Battery must not authorize a blind Queen at a phantom P2 return");
		});
		foreach (var (name, baseId, hp) in new[] { ("valid CT", 0x45A9u, .5f), ("unfinished P2 return", 0x459Fu, .4f) })
		Case(name, () =>
		{
			var rotation = PrimePresummon(baseId, hp);
			var selected = Select(rotation, "GeneralAbility");
			Require(selected?.ID == (uint)AutomatonQueenPvE, name + " permits a Queen for a finite return inside the same unfinished observed phase");
			Require(Select(rotation, "GeneralAbility")?.ID == (uint)AutomatonQueenPvE,
				"a presummon preview does not consume its phase context or Battery");
			if (selected?.ID == (uint)AutomatonQueenPvE)
			{
				Game.Execute(selected);
				Game.Step(.8f);
				Require(Select(rotation, "EmergencyAbility")?.ID != (uint)QueenOverdrivePvE,
					"a confirmed preparation Queen is not Overdriven while the old phase boss is absent");
				Game.Step(354.1f - Game.Now);
				Require(Game.Target?.BaseId == baseId && Machinist.Gauge.IsRobotActive,
					"the prepared Queen survives into the actual same-phase target return");
			}
		});
		Case("present CT actor with cleared raw target", () =>
		{
			var rotation = PrimePresummon(0x45A9, .5f);
			Game.CurrentTargetOverride = null;
			Require(Game.Target == null && Game.FindObject(Game.Enemies[0].Chara.GameObjectId) is { } present && present.GetHealthRatio() > 0,
				"fixture has no hard target but its previously observed unfinished CT actor is still present");
			Require(Select(rotation, "GeneralAbility")?.ID == (uint)AutomatonQueenPvE,
				"clearing the hard target does not discard a known same-actor CT Queen opportunity with fresh positive HP");
			var queen = Select(rotation, "GeneralAbility");
			if (queen?.ID == (uint)AutomatonQueenPvE)
			{
				Game.Execute(queen);
				RunProductionFor(rotation, 19);
				Require(Game.DamageByAction.GetValueOrDefault("Pile Bunker") > 0 && Game.DamageByAction.GetValueOrDefault("Crowned Collider") > 0,
					"the restored clear-target CT selection actually completes both Queen finishers on the same-phase return");
			}
		});
		foreach (var state in new[] { "completed P2", "removed actor", "replacement ID", "changed base ID", "zero HP" })
		Case("object lookup rejects " + state, () =>
		{
			var rotation = PrimePresummon(0x459F, .19f);
			var observed = Game.Enemies[0].Chara;
			Game.CurrentTargetOverride = null;
			if (state == "completed P2") Game.Timeline.Actors[0].Hp.Add([Game.Now, .17f]);
			if (state == "removed actor") Game.ObjectLookupOverride = _ => null;
			if (state == "replacement ID") Game.ObjectLookupOverride = _ => new() { GameObjectId = 999, BaseId = 0x459F, HealthRatio = () => .5f };
			if (state == "changed base ID") Game.ObjectLookupOverride = _ => new() { GameObjectId = observed.GameObjectId, BaseId = 0x45A9, HealthRatio = () => .5f };
			if (state == "zero HP") Game.Timeline.Actors[0].Hp.Add([Game.Now, 0]);
			Game.Step(.01f);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				state + " does not become a valid pre-Queen merely because an old actor ID was cached");
		});
		Case("fresh object HP outranks stale raw actor HP", () =>
		{
			var rotation = PrimePresummon(0x459F, .19f);
			var observed = Game.Enemies[0].Chara;
			Game.CurrentTargetOverride = new() { GameObjectId = observed.GameObjectId, BaseId = observed.BaseId, HealthRatio = () => .19f };
			Game.Timeline.Actors[0].Hp.Add([Game.Now, .17f]);
			Game.Step(.01f);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				"fresh object-table HP below the P2 objective vetoes a stale retained raw-actor HP snapshot");
		});
		foreach (var (baseId, hp) in new[] { (0x459Fu, .199f), (0x45A7u, .189f) })
		Case("completed actual HP check above banking margin " + baseId.ToString("X"), () =>
		{
			var rotation = PrimePresummon(baseId, hp);
			Game.CurrentTargetOverride = null;
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				$"observed phase {baseId:X} at {hp:P1} has passed its actual 20% check even though its offensive banking margin is lower");
		});
		foreach (var scenario in new[] { "completed P2", "latest P2 HP crossed goal", "outside FRU", "unknown return" })
		Case("presummon rejects " + scenario, () =>
		{
			var rotation = PrimePresummon(0x459F, scenario == "completed P2" ? .17f : .181f);
			if (scenario == "latest P2 HP crossed goal") Game.Timeline.Actors[0].Hp.Add([Game.Now, .17f]);
			if (scenario == "outside FRU") Game.IsInFRU = false;
			if (scenario == "unknown return") Game.ForecastOverride = (float.MaxValue, float.MaxValue);
			Game.Step(.01f);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				scenario + " cannot authorize an otherwise executable 100-Battery Queen from the return forecast");
		});
		foreach (var raw in new[] { "friendly", "different phase", "unknown phase", "replacement actor" })
		Case("current object authority over " + raw + " raw target", () =>
		{
			var rotation = PrimePresummon(0x45A9, .5f);
			Game.CurrentTargetOverride = raw == "friendly" ? Game.Player : new() { GameObjectId = 999,
				BaseId = raw == "different phase" ? 0x45AFu : raw == "replacement actor" ? 0x45A9u : 0xFFFFu };
			Require(Select(rotation, "GeneralAbility")?.ID == (uint)AutomatonQueenPvE,
				"an unrelated raw target does not hide the observed CT actor's verified current object identity and unfinished HP");
		});
		Case("unknown observed phase", () =>
		{
			var rotation = PrimePresummon(0xFFFF, .5f);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				"an observed enemy without a known FRU phase objective cannot authorize return preparation");
		});
		Case("completed P2 cleared before final sample", () =>
		{
			var rotation = PrimePresummon(0x459F, .19f);
			Game.CurrentTargetOverride = null;
			Game.Timeline.Actors[0].Hp.Add([Game.Now, .17f]);
			Game.Step(.01f); // The actor is gone before the production tracker can observe the completed HP check.
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE,
				"cached 19% P2 HP plus a cleared raw target cannot prove the phase remains unfinished after its final hit");
		});
		Case("unknown return", () =>
		{
			var rotation = Reset(0x45A9, 740, active: false);
			Machinist.Gauge.Battery = 100;
			Game.ForecastOverride = (float.MaxValue, float.MaxValue);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE, "unknown return preserves Battery");
			Game.ForecastOverride = (float.MaxValue, float.NaN);
			Require(Select(rotation, "GeneralAbility")?.ID != (uint)AutomatonQueenPvE, "NaN return preserves Battery");
		});
		Case("recommendation is not a Queen", () =>
		{
			var rotation = Reset(0x45AF, 860);
			Game.TtkOverride = 300;
			Machinist.Gauge.Battery = 100;
			var first = rotation.NextAbility(NoGcd);
			Require(first?.ID == (uint)AutomatonQueenPvE, "healthy Pandora selects Queen before optional charge actions");
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)AutomatonQueenPvE, "uncast preview retains the summon recommendation");
			Require(Select(rotation, "EmergencyAbility")?.ID != (uint)QueenOverdrivePvE, "uncast recommendation never authorizes Overdrive");
			Require(Machinist.Gauge.Battery == 100 && !Machinist.Gauge.IsRobotActive, "selection spends no Battery or robot gauge");
		});
		Case("fresh healthy Pandora", () =>
		{
			var rotation = Reset(0x45AF, 860);
			Machinist.Gauge.Battery = 100;
			Game.TtkOverride = 300f;
			Game.AverageTtkOverride = 3f;
			MCH_Reborn.AutomatonQueenPvE.Config.TimeToKill = 20f;
			var queen = rotation.NextAbility(NoGcd);
			Require(queen?.ID == (uint)AutomatonQueenPvE, "healthy Pandora's measured 300s individual TTK outranks the stale 3s global average");
			if (queen?.ID == (uint)AutomatonQueenPvE)
			{
				Require(((IBaseAction)queen).Target.Target == Game.Player, "native Queen action remains self-targeted");
				Game.Execute(queen);
				Game.Step(.8f);
				Require(Select(rotation, "EmergencyAbility")?.ID != (uint)QueenOverdrivePvE,
					"same fresh-target TTK must not instantly Overdrive the confirmed Queen");
				Require(Machinist.Gauge.IsRobotActive && Machinist.Gauge.Battery == 0, "execution confirms robot and consumed Battery");
			}
		});
		Case("unknown target lifetime retains native veto", () =>
		{
			var rotation = Reset(0x45AF, 860);
			Machinist.Gauge.Battery = 100;
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 3;
			MCH_Reborn.AutomatonQueenPvE.Config.TimeToKill = 20;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"unknown individual lifetime cannot justify bypassing the native short-average Queen veto");
		});
		foreach (var (name, id, hp, fru) in new[] { ("dying Pandora", 0x45AFu, .04f, true),
			("other encounter", 0x45AFu, 1f, false), ("other FRU boss", 0x459Fu, 1f, true) })
		Case(name, () =>
		{
			var rotation = Reset(id, 860, hp: hp);
			Game.IsInFRU = fru;
			Game.TtkOverride = 3f;
			Machinist.Gauge.Battery = 100;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE, name + " retains the finite target-lifetime veto");
		});
		Case("confirmed Queen with stale target", () =>
		{
			var rotation = Reset(0x459F, 350);
			Machinist.Gauge.Battery = 100;
			Require(MCH_Reborn.AutomatonQueenPvE.CanUse(out var queen, skipTTKCheck: true), "fixture Queen is native-available");
			Game.Execute(queen);
			Game.Step(.8f);
			Game.Hostiles.Clear();
			Game.ForecastOverride = (3f, 20f);
			Require(Select(rotation, "EmergencyAbility")?.ID != (uint)QueenOverdrivePvE,
				"a newly active robot must not be Overdriven against a stale untargetable boss");
			Game.Step(5f);
			Require(Select(rotation, "EmergencyAbility")?.ID == (uint)QueenOverdrivePvE,
				"a mature confirmed Queen may finish before real imminent downtime");
		});
		foreach (var friendly in new[] { true, false })
		Case("hostile fallback " + friendly, () =>
		{
			var rotation = Reset(0x45AF, 860);
			Game.TtkOverride = 300;
			Machinist.Gauge.Battery = 100;
			Game.CurrentTargetOverride = friendly ? Game.Player : new() { GameObjectId = 999, BaseId = 0x459F, Name = "old boss" };
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)AutomatonQueenPvE,
				"a friendly or stale raw target cannot hide a valid hostile from the self-targeted Queen");
		});
		foreach (var battery in new byte[] { 50, 100 })
		Case("low HP final Queen with sufficient measured life " + battery, () =>
		{
			var rotation = Reset(0x45AF, 860, hp: .015f);
			Game.TtkOverride = 20;
			Game.AverageTtkOverride = 20;
			Machinist.Gauge.Battery = battery;
			var queen = rotation.NextAbility(NoGcd);
			Require(queen?.ID == (uint)AutomatonQueenPvE,
				$"Pandora at 1.5% HP with measured 20s life can execute its final {battery}-Battery Queen's full payload");
			if (queen?.ID == (uint)AutomatonQueenPvE)
			{
				Game.Execute(queen);
				Game.Step(.8f);
				Require(Select(rotation, "EmergencyAbility")?.ID != (uint)QueenOverdrivePvE,
					"the same sufficient measured lifetime prevents an immediate Overdrive from the low-HP heuristic");
				RunProductionFor(rotation, 16.65f);
				Require(Game.DamageByAction.GetValueOrDefault("Pile Bunker") > 0 && Game.DamageByAction.GetValueOrDefault("Crowned Collider") > 0,
					"the selected low-HP final Queen actually completes both modeled finishers inside the proven lifetime");
			}
		});
		Case("low HP final Queen without full lifetime", () =>
		{
			var rotation = Reset(0x45AF, 860, hp: .015f);
			Game.TtkOverride = 5;
			Game.AverageTtkOverride = 20;
			Machinist.Gauge.Battery = 100;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"the same low HP boss with only 5s measured life still vetoes a fresh capped Queen");
		});
		Case("real short target with misleading long average", () =>
		{
			var rotation = Reset(0x45AF, 860, hp: .3f);
			Machinist.Gauge.Battery = 100;
			Game.AverageTtkOverride = 200;
			Game.TtkOverride = 3;
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"candidate lifetime still vetoes Queen even when the global native average is long");
		});
		Case("near-dead unknown target", () =>
		{
			var rotation = Reset(0x45AF, 860, hp: .01f);
			Machinist.Gauge.Battery = 100;
			Game.TtkOverride = float.NaN;
			Game.AverageTtkOverride = 300; // The HP-only veto must stand on its own, not hide behind native global TTK rejection.
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"a 1% real boss does not become safe merely because individual TTK is unknown");
		});
		Case("actual next Queen target", () =>
		{
			var rotation = Reset(0x45A3, 380);
			var veil = new Dalamud.Game.ClientState.Objects.Types.IBattleChara { GameObjectId = 999, BaseId = 0x45A5, Name = "Ice Veil" };
			Game.Hostiles.Add(veil);
			Game.TargetTtkOverride = target => target.BaseId == 0x45A3 ? 5 : 60;
			Machinist.Gauge.Battery = 100;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)AutomatonQueenPvE,
				"a 5s crystal may release Queen when a real 60s Ice Veil remains available");
			Game.ForecastOverride = (10f, 40f);
			Game.Step(.1f);
			Game.Hostiles.Add(veil);
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)AutomatonQueenPvE,
				"a follow-up enemy cannot bypass genuine global downtime inside the Queen payload");
		});
		foreach (var state in new[] { "healthy measured follow-up", "removed follow-up", "dying follow-up", "unknown follow-up lifetime" })
		Case("never-hard-targeted crystal " + state, () =>
		{
			TimelineActor Crystal(string key, bool visible, float hp) => new(key, 0x45A3, key,
				[[visible ? 0 : 1000, 2000]], [[0, hp]], [[0, 100, 100]], null);
			var fixture = new Timeline("party-observed independent crystal", "MCH", 2000,
				[Crystal("current", true, .5f), Crystal("follow-up", state != "removed follow-up", state == "dying follow-up" ? .01f : .5f)],
				[[0, 100, 100]], [], [], [], [], [], 0, [], [], [], "MCH", ["MCH", "PCT"], []);
			var rotation = Reset(0x45A3, 380, timeline: fixture);
			var current = Game.Target!.GameObjectId;
			Game.TargetTtkOverride = target => target.GameObjectId == current || state == "unknown follow-up lifetime" ? float.NaN : 35;
			Game.AverageTtkOverride = 0; // A cached global value cannot erase a currently measured independent hostile's lifetime.
			Machinist.Gauge.Battery = 100;
			var queen = rotation.NextAbility(NoGcd);
			Require(state == "healthy measured follow-up" ? queen?.ID == (uint)AutomatonQueenPvE : queen?.ID != (uint)AutomatonQueenPvE,
				state == "healthy measured follow-up"
					? "an actual healthy targetable crystal with 35s measured life funds Queen even if the hard-targeted crystal and cached native average have no lifetime"
					: state + " cannot manufacture the measured lifetime needed to bypass the native Queen TTK veto");
		});
		Case("genuine startup emergency", () =>
		{
			var rotation = Reset(0x459F, 350);
			Machinist.Gauge.Battery = 100;
			MCH_Reborn.AutomatonQueenPvE.CanUse(out var queen, skipTTKCheck: true);
			Game.Execute(queen);
			Game.Step(.8f);
			Game.TtkOverride = 1;
			Require(Select(rotation, "EmergencyAbility")?.ID == (uint)QueenOverdrivePvE,
				"a real hostile genuinely dying during startup may still require immediate Overdrive");
		});
		foreach (var mode in new[] { "outside FRU", "Auto Burst off", "fresh P4 far downtime", "level 80 outside FRU" })
		Case(mode, () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .2f);
			Game.TtkOverride = mode == "fresh P4 far downtime" ? float.NaN : 25f;
			Game.ForecastOverride = (100f, 200f);
			Game.IsInFRU = !mode.Contains("outside FRU");
			Game.AutoBurst = mode != "Auto Burst off";
			Game.Level = mode.StartsWith("level 80") ? 80 : 100;
			Machinist.Gauge.Heat = 50;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE,
				mode + " does not acquire the FRU P5 paid-Heat reservation");
		});
		Case("known P4 downtime with unknown TTK", () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .4f);
			Game.TtkOverride = float.NaN;
			Game.ForecastOverride = (25f, 80f);
			Machinist.Gauge.Heat = 50;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE,
				"a viable 25s P4 forecast keeps the paid cleave payload available");
		});
		Case("CT Barrel preview is not a confirmed resource", () =>
		{
			var rotation = CtDowntimeSnapshot(50);
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)BarrelStabilizerPvE, "CT Barrel can be previewed before any confirmed status exists");
			Game.Cooldown(BarrelStabilizerPvE).FullAt = Game.Now + 120;
			Game.Step(760.1f - Game.Now);
			Game.ForecastOverride = null;
			Game.TtkOverride = 250;
			Game.Cooldown(WildfirePvE).FullAt = Game.Now + 80;
			foreach (var tool in new[] { AirAnchorPvE, ChainSawPvE, DrillPvE }) Game.Cooldown(tool).FullAt = Game.Now + Math.Min(35f, Game.Cooldown(tool).OneCharge * Game.Cooldown(tool).MaxCharges - .1f);
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE, "a recommendation that never executed cannot latch away the paid payload");
		});
		Case("free status without a CT return window", () =>
		{
			var rotation = CtDowntimeSnapshot(50);
			MCH_Reborn.BarrelStabilizerPvE.CanUse(out var barrel, skipTTKCheck: true);
			Game.Execute(barrel);
			Game.Step(.7f);
			Game.ForecastOverride = (100, 150);
			rotation.NextAbility(NoGcd);
			Game.Step(760.1f - Game.Now);
			Game.ForecastOverride = null;
			Game.TtkOverride = 250;
			Game.Player.Statuses.Remove(StatusID.Hypercharged);
			Game.Player.Statuses.Remove(StatusID.FullMetalMachinist);
			Game.Cooldown(WildfirePvE).FullAt = Game.Now + 80;
			foreach (var tool in new[] { AirAnchorPvE, ChainSawPvE, DrillPvE }) Game.Cooldown(tool).FullAt = Game.Now + Math.Min(35f, Game.Cooldown(tool).OneCharge * Game.Cooldown(tool).MaxCharges - .1f);
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE, "free Hypercharged outside the bounded CT return window cannot arm the P5 reserve");
		});
		Case("CT retains its available twenty Heat", () =>
		{
			FundedCtReturn(20);
			Require(Machinist.Gauge.Heat == 20, "CT retention does not invent the missing thirty Heat or promise a funded P5 entry");
		});
		Case("disabled Wildfire", () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .2f);
			Game.TtkOverride = 25f;
			Machinist.Gauge.Heat = 50;
			MCH_Reborn.WildfirePvE.Config.IsEnabled = false;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE,
				"an explicitly disabled Wildfire does not reserve its paid payload indefinitely");
		});
		foreach (var reset in new[] { "death", "outside territory", "Auto Burst toggle", "rotation hot switch" })
		Case("reservation reset " + reset, () =>
		{
			var rotation = FundedCtReturn(50);
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)HyperchargePvE, "fixture observes confirmed CT Barrel and preserves available Heat");
			Game.TtkOverride = float.NaN;
			Game.ForecastOverride = (100f, 150f);
			if (reset == "death") Game.Timeline.PlayerDebuffs.Add(new("Dead", Game.Now + .1f, Game.Now + .5f));
			if (reset == "outside territory") Game.IsInFRU = false;
			if (reset == "Auto Burst toggle") Game.AutoBurst = false;
			Game.Step(.2f);
			rotation.NextAbility(NoGcd);
			Game.Step(.5f);
			Game.IsInFRU = Game.AutoBurst = true;
			Machinist.Gauge.Heat = 50;
			if (reset == "rotation hot switch") rotation = new MCH_Plan();
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE,
				reset + " clears the reservation instead of retaining an old P4 decision");
		});
		Case("reservation wipe reset", () =>
		{
			var rotation = FundedCtReturn(50);
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)HyperchargePvE, "fixture arms the previous pull through confirmed CT actions");
			Reset(0x45AF, 10);
			Machinist.Gauge.Heat = 50;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE,
				"the same rotation instance clears its reservation when the combat clock restarts");
		});
		Case("friendly raw target Barrel exception", () =>
		{
			var rotation = Reset(0x45A9, 640);
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Game.CurrentTargetOverride = new() { GameObjectId = 1, Name = "low HP ally", HealthRatio = () => .01f };
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)BarrelStabilizerPvE,
				"Queen hostile validation does not change the approved P4 Barrel exception with a friendly raw target");
		});
		foreach (var heat in new byte[] { 50, 60, 95 })
		Case("P4 paid cleave " + heat, () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .2f);
			Game.TtkOverride = 25f;
			Machinist.Gauge.Heat = heat;
			var charge = rotation.NextAbility(NoGcd);
			Require(charge?.ID == (uint)HyperchargePvE,
				$"P4 25s target life retains its viable paid payload at {heat} Heat");
			if (charge?.ID == (uint)HyperchargePvE) { Game.Execute(charge); Require(Machinist.Gauge.Heat == heat - 50, "confirmed paid P4 Hypercharge spends exactly 50 Heat"); }
		});
		foreach (var free in new[] { true, false })
		Case("P3 reserve preserves " + (free ? "free Hypercharge" : "live Wildfire"), () =>
		{
			var rotation = Reset(0x45A7, 575, hp: .24f);
			Game.TtkOverride = 90;
			Machinist.Gauge.Heat = 50;
			Game.Player.Statuses[free ? StatusID.Hypercharged : StatusID.Wildfire_1946] = free ? 20 : 6;
			var charge = rotation.NextAbility(NoGcd);
			Require(charge?.ID == (uint)HyperchargePvE,
				"a P3 reserve cannot waste a free token or abandon an already running Wildfire payload");
			if (free && charge?.ID == (uint)HyperchargePvE)
			{
				Game.Execute(charge);
				Require(Machinist.Gauge.Heat == 50, "confirmed free P3 Hypercharge preserves the paid resource target");
			}
		});
		Case("P4 free charge", () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .2f);
			Game.TtkOverride = 25f;
			Machinist.Gauge.Heat = 50;
			Game.Player.Statuses[StatusID.Hypercharged] = 20f;
			var charge = rotation.NextAbility(NoGcd);
			Require(charge?.ID == (uint)HyperchargePvE, "the free CT/P4 Hypercharged token is spent despite paid-Heat reserve");
			if (charge?.ID == (uint)HyperchargePvE)
			{
				Game.Execute(charge);
				Require(Machinist.Gauge.Heat == 50, "free Hypercharge genuinely keeps the P5 paid payload");
				Require(rotation.NextGcd()?.AdjustedID == (uint)BlazingShotPvE, "confirmed free Hypercharge selects actual Blazing Shot GCD");
			}
		});
		Case("P4 overcap", () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .2f);
			Game.TtkOverride = 25f;
			Machinist.Gauge.Heat = 100;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE, "100 Heat can spend one payload and still retain 50");
		});
		Case("P4 live Wildfire", () =>
		{
			var rotation = Reset(0x45A9, 700, hp: .2f);
			Game.TtkOverride = 25f;
			Machinist.Gauge.Heat = 50;
			Game.Player.Statuses[StatusID.Wildfire_1946] = 6f;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE, "an already running Wildfire completes its actual payload before banking");
		});
		Case("unrelated paid charge", () =>
		{
			var rotation = Reset(0x45AF, 860);
			Machinist.Gauge.Heat = 50;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)HyperchargePvE, "normal healthy P5 paid Hypercharge remains available");
		});
		foreach (var id in new uint[] { 0x45A7, 0x45A9, 0x45AB })
		Case("Barrel phase " + id.ToString("X"), () =>
		{
			var rotation = Reset(id, 640);
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)BarrelStabilizerPvE, "approved P3/P4 independent Barrel survives");
		});
		Case("P4 to CT to P5 payload", () =>
		{
			var rotation = FundedCtReturn(50);
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)HyperchargePvE, "confirmed CT payload preserves the available paid P5 reserve");
			Game.Step(857.1f - Game.Now);
			Game.Cooldown(WildfirePvE).FullAt = 0;
			foreach (var action in new[] { AirAnchorPvE, ChainSawPvE, DrillPvE }) Game.Cooldown(action).FullAt = Game.Now + Math.Min(35, Game.Cooldown(action).OneCharge * Game.Cooldown(action).MaxCharges);
			var start = Game.Now;
			RunProductionFor(rotation, 15);
			foreach (var cast in Game.Casts.Where(c => c.Time >= start)) Console.WriteLine($"P5 {cast.Time:F2} {cast.Action}");
			var wildfire = Game.Casts.FirstOrDefault(c => c.Time >= start && c.Action == nameof(WildfirePvE));
			Require(wildfire.Time >= start && wildfire.Time < 861, "P5 Wildfire commits during live buffs before Barrel returns");
			Require(Game.Casts.Any(c => c.Time >= start && c.Time < 861 && c.Action == nameof(HyperchargePvE)),
				"P5 paid Hypercharge supplies the early Wildfire payload");
			Require(Game.Casts.Count(c => c.Time >= start && c.Time < 877 && c.Action == nameof(BlazingShotPvE)) >= 5,
				"five real Blazing Shot selections land before Starry Muse expires");
			Require(Math.Abs(Game.DamageByAction.GetValueOrDefault("Wildfire") - 240 * 6 * 1.05) < .1,
				$"the selected P5 Wildfire actually detonates at six hits (1440 base potency with live Starry Muse); actual {Game.DamageByAction.GetValueOrDefault("Wildfire"):F1}");
		});
		Case("P5 returning Barrel after Wildfire", () =>
		{
			// 10-21 live: Wildfire 864, CT Barrel back 875.8 inside the buffs, Full Metal Field held to 885.2.
			var rotation = FundedCtReturn(50);
			Game.Step(857.1f - Game.Now);
			Game.Cooldown(WildfirePvE).FullAt = 0;
			foreach (var action in new[] { AirAnchorPvE, ChainSawPvE, DrillPvE }) Game.Cooldown(action).FullAt = Game.Now + Math.Min(35, Game.Cooldown(action).OneCharge * Game.Cooldown(action).MaxCharges);
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 871; // back after Wildfire's window, 6s before Starry Muse ends
			var start = Game.Now;
			RunProductionFor(rotation, 22);
			var barrel = Game.Casts.FirstOrDefault(c => c.Time >= start && c.Action == nameof(BarrelStabilizerPvE));
			var metal = Game.Casts.FirstOrDefault(c => c.Time > barrel.Time && c.Action.StartsWith(nameof(FullMetalFieldPvE)));
			Require(barrel.Action != null && barrel.Time < 873, $"P5 Barrel returns inside the buffs; actual {barrel.Time:F2}");
			Require(metal.Action != null && metal.Time <= barrel.Time + 5.5f && metal.Time < 877,
				$"Full Metal Field follows the returning Barrel inside Starry Muse; Barrel {barrel.Time:F2}, FMF {(metal.Action == null ? "none" : metal.Time.ToString("F2"))}");
		});
		Case("crystal burst Queen takes the first weave", () =>
		{
			// 10-21 live: control returns at the crystals with the buffs already rolling; Air Anchor takes Battery to 90, then
			// Reassemble, Checkmate, Wildfire, Hypercharge each took the weave and the Queen came out 6.5s later (381.1),
			// two Roller Dashes before the crystal died and nothing else. Top MCH summon on the first weave.
			var real = Timeline.Load(FindTimeline("fru-mch-gNfDjbmTPvGK16dx-3.json"));
			var fixture = real with
			{
				PartyBuffs = real.PartyBuffs.Select(b => b.Start is > 360 and < 380 ? b with { Start = 372.2f } : b).ToList(),
				EnemyDebuffs = real.EnemyDebuffs?.Select(d => d.Start is > 360 and < 380 ? d with { Start = 372.2f } : d).ToList(),
			};
			var rotation = Reset(0x459F, 372f, timeline: fixture);
			Machinist.Gauge.Heat = 50;
			Machinist.Gauge.Battery = 70;
			foreach (var id in new[] { BarrelStabilizerPvE, WildfirePvE, AirAnchorPvE, ChainSawPvE }) Game.Cooldown(id).FullAt = 0;
			Game.Cooldown(DrillPvE).FullAt = Game.Now + 20;
			var start = Game.Now;
			RunProductionFor(rotation, 24);
			Require(Game.DamageByAction.GetValueOrDefault("Pile Bunker") > 0 && Game.DamageByAction.GetValueOrDefault("Crowned Collider") > 0
				&& Game.DamageByAction.GetValueOrDefault("Arm Punch") >= 1300, "the crystal Queen lands five Arm Punches and both finishers before the crystals fall");
			var queen = Game.Casts.FirstOrDefault(c => c.Time >= start && c.Action.StartsWith(nameof(AutomatonQueenPvE)));
			var wildfire = Game.Casts.FirstOrDefault(c => c.Time >= start && c.Action == nameof(WildfirePvE));
			Require(queen.Action != null && wildfire.Action != null && queen.Time < wildfire.Time,
				$"the crystal Queen goes before Wildfire; Queen {(queen.Action == null ? "none" : queen.Time.ToString("F2"))}, Wildfire {(wildfire.Action == null ? "none" : wildfire.Time.ToString("F2"))}");
		});
		Console.WriteLine($"MCH Queen/P5 decisions: {_checks} assertions passed; {Failures.Count} failed.");
		if (Failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, Failures));
	}

	public static void RunPreparation()
	{
		PrintSources();
		foreach (var mode in new[] { "observed P2 body", "missing P2", "future P2", "living P1", "missing P1", "replaced P1", "wrong next phase", "damaged P2", "P2 to Oracle" })
		Case("bounded next-phase preparation " + mode, () =>
		{
			var priorId = mode == "P2 to Oracle" ? 0x459Fu : 0x459Bu;
			var nextId = mode is "wrong next phase" or "P2 to Oracle" ? 0x45A7u : 0x459Fu;
			var prior = new TimelineActor("prior", priorId, "old phase", [[0, 100.1f]], [[0, 1], [100.1f, mode == "living P1" ? .1f : 0]], [[0, 100, 100]], null);
			var next = new TimelineActor("next", nextId, "next body", [[104, 2000]], [[0, mode == "damaged P2" ? .98f : 1]], [[0, 100, 100]], null, PresentFrom: mode == "future P2" ? 104 : 100.1f);
			var fixture = new Timeline("observed next body", "MCH", 2000, [prior, next], [[0, 100, 100]], [], [], [], [], [], 0, [], [], [], "MCH", ["MCH"], []);
			var rotation = Reset(priorId, 100, timeline: fixture);
			rotation.NextAbility(NoGcd);
			Game.Step(.2f);
			if (mode == "missing P2") Game.CurrentObjectsOverride = [Game.Enemies[0].Chara];
			if (mode == "missing P1") Game.CurrentObjectsOverride = [Game.Enemies[1].Chara];
			if (mode == "replaced P1") Game.CurrentObjectsOverride = [new() { GameObjectId = 987, BaseId = priorId, HealthRatio = () => 0 }, Game.Enemies[1].Chara];
			Game.ForecastOverride = (float.MaxValue, 3);
			Machinist.Gauge.Battery = 100;
			Require(!Game.Hostiles.Any() && MCH_Reborn.AutomatonQueenPvE.CanUse(out _, skipTTKCheck: true), "preparation fixture has no attackable target but native Queen is executable");
			var selected = rotation.NextAbility(NoGcd);
			Require((selected?.ID == (uint)AutomatonQueenPvE) == (mode is "observed P2 body" or "living P1"), mode is "observed P2 body" or "living P1"
				? "completed observed P1 and actually present full-health P2 body permit the bounded pre-Queen weave"
				: mode + " cannot prove a safe next-phase Queen preparation");
		});
		foreach (var mode in new[] { "completed Absolute Zero", "not observed", "unknown cast", "too early", "early cancel", "missing before completion", "missing after completion", "wrong identity", "failed HP", "dead actor", "death reset", "territory reset", "clock rollback", "expired anchor", "phase carry" })
		Case("observed crystal anchor " + mode, () =>
		{
			var actor = new TimelineActor("shiva", 0x459F, "P2", [[0, 100.1f]], [[0, .19f], [103, mode == "failed HP" ? .21f : mode == "dead actor" ? 0 : .19f]], [[0, 100, 100]], null);
			var next = new TimelineActor("oracle", 0x45A7, "Oracle", [[120, 2000]], [[0, 1]], [[0, 100, 100]], null, PresentFrom: 120);
			var fixture = new Timeline("observed Absolute Zero", "MCH", 2000, mode == "phase carry" ? [actor, next] : [actor], [[0, 100, 100]], [], [], [], [], [], 0,
				mode == "not observed" ? [] : [new("shiva", mode == "unknown cast" ? 999u : 40224u, 100, 3)], [], [], "MCH", ["MCH"], []);
			var rotation = Reset(0x459F, 100, timeline: fixture);
			rotation.NextAbility(NoGcd);
			if (mode == "early cancel")
			{
				Game.Step(1);
				Game.Enemies[0].Chara.CastActionId = 0;
				rotation.NextAbility(NoGcd);
			}
			Game.Step(103.1f - Game.Now);
			if (mode == "missing before completion") Game.CurrentObjectsOverride = [];
			rotation.NextAbility(NoGcd);
			if (mode == "death reset")
			{
				Game.Timeline.PlayerDebuffs.Add(new("Dead", 104, 105));
				Game.Step(1);
				rotation.NextAbility(NoGcd);
			}
			if (mode == "territory reset")
			{
				Game.IsInFRU = false;
				Game.Step(1);
				rotation.NextAbility(NoGcd);
				Game.IsInFRU = true;
			}
			if (mode == "clock rollback")
			{
				Game.Reset(fixture, new Machinist(.2f, .2f), 0);
				Game.Step(95);
				rotation.NextAbility(NoGcd);
			}
			Game.Step((mode == "too early" ? 110 : mode == "expired anchor" ? 126 : 122) - Game.Now);
			if (mode == "missing after completion") Game.CurrentObjectsOverride = [];
			if (mode == "wrong identity") Game.ObjectLookupOverride = _ => new() { GameObjectId = 987, BaseId = 0x459F, HealthRatio = () => .19f };
			Game.ForecastOverride = (float.MaxValue, 999);
			Machinist.Gauge.Battery = 100;
			Require((rotation.NextAbility(NoGcd)?.ID == (uint)AutomatonQueenPvE) == (mode == "completed Absolute Zero"), mode == "completed Absolute Zero"
				? "the actual completed 40224 cast anchors a bounded crystal preparation despite the completed P2 HP check and stale generic forecast"
				: mode + " cannot sustain a completed-cast crystal Queen anchor");
		});
		Console.WriteLine($"MCH preparation decisions: {_checks} assertions passed; {Failures.Count} failed.");
		if (Failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, Failures));
	}

	private static MCH_Plan Reset(uint baseId, float now, bool active = true, float hp = 1f, Timeline? timeline = null)
	{
		var actor = new TimelineActor("boss", baseId, "boss", [[active ? 0 : now + 100, 2000]], [[0, hp]], [[0, 100, 100]], null);
		timeline ??= new Timeline("MCH decision boundary", "MCH", 2000, [actor], [[0, 100, 100]], [], [], [], [], [], 0,
			[], [], [], "MCH", ["MCH", "PCT"], [], false);
		Game.Reset(timeline, new Machinist(.2f, .2f), 0);
		Game.BaseGcd = 2.5f;
		Game.Step(now);
		foreach (var property in typeof(RotationSolver.Basic.Rotations.Basic.MachinistRotation).GetProperties(BindingFlags.Public | BindingFlags.Static))
			if (property.GetValue(null) is IBaseAction action)
			{
				var defaults = action.Setting.CreateConfig?.Invoke() ?? new ActionConfig();
				action.Config.IsEnabled = true;
				action.Config.TimeToKill = action.Setting.TargetStatusProvide?.Length > 0 ? 0 : defaults.TimeToKill;
				action.Config.AoeCount = defaults.AoeCount;
			}
		foreach (var id in new[] { BarrelStabilizerPvE, WildfirePvE, ReassemblePvE, DoubleCheckPvE, CheckmatePvE, AirAnchorPvE, ChainSawPvE, DrillPvE })
			Game.Cooldown(id).FullAt = now + Math.Min(79.9f, Game.Cooldown(id).OneCharge * Game.Cooldown(id).MaxCharges - .1f);
		var rotation = new MCH_Plan();
		foreach (var name in new[] { "UseSecondBurstMedicine", "UseThirdBurstMedicine", "UseFourthBurstMedicine", "OpenerBurstMeds", "PadFatebreaker" })
			Property(rotation, name)?.SetValue(rotation, false);
		return rotation;
	}
	private static MCH_Plan CtDowntimeSnapshot(byte heat)
	{
		var pair = new TimelineActor("pair", 0x45A9, "pair", [[0, 740], [760, 780]], [[0, .2f]], [[0, 100, 100]], null);
		var pandora = new TimelineActor("pandora", 0x45AF, "pandora", [[850, 2000]], [[0, 1]], [[0, 100, 100]], null);
		var fixture = new Timeline("P4/CT/P5 boundary", "MCH", 2000, [pair, pandora], [[0, 100, 100]], [],
			[new("Starry Muse", "PCT", 1.05f, 857, 877)], [], [new(857, 877, false)], [], 0, [], [], [], "MCH", ["MCH", "PCT"], []);
		var rotation = Reset(0x45A9, 700, timeline: fixture);
		Game.TtkOverride = 250;
		rotation.NextAbility(NoGcd); // Observe actual P4 before the downtime snapshot.
		Machinist.Gauge.Heat = heat;
		Game.Step(45);
		Game.ForecastOverride = (float.MaxValue, 15f);
		Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
		return rotation;
	}
	private static MCH_Plan FundedCtReturn(byte heat)
	{
		var rotation = CtDowntimeSnapshot(heat);
		var barrel = rotation.NextAbility(NoGcd);
		Require(barrel?.ID == (uint)BarrelStabilizerPvE, "15s CT Barrel is selected with the next buff window 112s away");
		if (barrel?.ID == (uint)BarrelStabilizerPvE) Game.Execute(barrel);
		Require(Game.Cooldown(BarrelStabilizerPvE).FullAt == 865, "confirmed CT Barrel has its genuine 120s recast");
		Game.Step(.7f);
		rotation.NextAbility(NoGcd); // Observe the actual free Hypercharged status during downtime.
		Game.Step(760.1f - Game.Now);
		Game.ForecastOverride = null;
		foreach (var action in new[] { AirAnchorPvE, ChainSawPvE, DrillPvE }) Game.Cooldown(action).FullAt = Game.Now + Math.Min(35.1f, Game.Cooldown(action).OneCharge * Game.Cooldown(action).MaxCharges - .1f);
		var metal = rotation.NextGcd();
		Require(metal?.ID == (uint)FullMetalFieldPvE, "actual first CT return GCD spends Full Metal Field");
		if (metal?.ID == (uint)FullMetalFieldPvE) Game.Execute(metal);
		Game.Step(.8f);
		Game.TtkOverride = float.NaN;
		var free = rotation.NextAbility(rotation.NextGcd() ?? NoGcd);
		Require(free?.ID == (uint)HyperchargePvE, "confirmed CT Full Metal Field is followed by free Hypercharge");
		if (free?.ID == (uint)HyperchargePvE) Game.Execute(free);
		RunProductionFor(rotation, 8.1f);
		Require(Game.Casts.Count(c => c.Time >= 760 && c.Time < 780 && c.Action.StartsWith(nameof(BlazingShotPvE))) == 5, "free CT Hypercharge completes five actual Blazing Shot GCDs");
		Require(Machinist.Gauge.Heat == heat, $"confirmed free CT payload retains its available {heat} Heat");
		Game.Step(2.1f); // Let the genuine 10s Hypercharge recast finish before testing a second paid use.
		Game.Cooldown(WildfirePvE).FullAt = Game.Now + 80;
		foreach (var action in new[] { DoubleCheckPvE, CheckmatePvE }) Game.Cooldown(action).FullAt = Game.Now + 59.9f;
		return rotation;
	}
	private static MCH_Plan PrimePresummon(uint baseId, float hp)
	{
		var actor = new TimelineActor("boss", baseId, "phase boss", [[0, 350.1f], [354, 2000]], [[0, hp]], [[0, 100, 100]], null);
		var fixture = new Timeline("observed same-phase return", "MCH", 2000, [actor], [[0, 100, 100]], [], [], [], [], [], 0,
			[], [], [], "MCH", ["MCH", "PCT"], []);
		var rotation = Reset(baseId, 350, timeline: fixture);
		rotation.NextAbility(NoGcd); // The production tracker observes an actual attackable boss, with no Battery to summon.
		var observed = Game.Target;
		Game.Step(.2f);
		Game.CurrentTargetOverride = observed; // Native hard-target retention supplies the departing actor's latest HP.
		Machinist.Gauge.Battery = 100;
		Game.ForecastOverride = (float.MaxValue, 3f);
		return rotation;
	}
	private static MCH_Plan ActiveQueenWildfire()
	{
		var rotation = Reset(0x45A3, 380, hp: .5f);
		Game.TtkOverride = 250;
		Machinist.Gauge.Battery = 100;
		Machinist.Gauge.Heat = 50;
		if (!MCH_Reborn.AutomatonQueenPvE.CanUse(out var queen, skipTTKCheck: true)) throw new InvalidOperationException("Queen setup unavailable");
		Game.Execute(queen);
		Game.Step(.7f);
		Game.Cooldown(WildfirePvE).FullAt = 0;
		if (!MCH_Reborn.WildfirePvE.CanUse(out var wildfire, skipTTKCheck: true)) throw new InvalidOperationException("Wildfire setup unavailable");
		Game.Execute(wildfire);
		Game.Step(.7f);
		if (!MCH_Reborn.HyperchargePvE.CanUse(out var hypercharge, skipTTKCheck: true)) throw new InvalidOperationException("Hypercharge setup unavailable");
		Game.Execute(hypercharge);
		Game.Step(.7f);
		for (var i = 0; i < 4; i++)
		{
			if (!MCH_Reborn.BlazingShotPvE.CanUse(out var shot)) throw new InvalidOperationException("Blazing Shot setup unavailable");
			Game.Execute(shot);
			Game.Step(i == 3 ? .7f : 1.5f);
		}
		return rotation;
	}
	private static string FindTimeline(string name)
	{
		for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "tests", "FightSim", "timelines", name))) return Path.Combine(dir.FullName, "tests", "FightSim", "timelines", name);
		throw new FileNotFoundException(name);
	}

	private static void RunProductionFor(CustomRotation rotation, float seconds)
	{
		var end = Game.Now + seconds;
		while (Game.Now < end)
		{
			Game.Step(.02f);
			if (Game.AnimationLock > 0 || Game.Locked) continue;
			var gcd = rotation.NextGcd();
			if (gcd != null && Game.GcdRemain <= .001f) { Game.Execute(gcd); continue; }
			if (rotation.NextAbility(gcd ?? NoGcd) is { } ability) Game.Execute(ability);
		}
	}

	private static void PrintSources()
	{
		Console.WriteLine("Production source fingerprints embedded at build time:");
		var sources = typeof(MchQueenDecisionCheck).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Where(a => a.Key.StartsWith("MCH.SourceSHA256.")).ToArray();
		if (sources.Length != 3 || sources.Any(s => s.Value?.Length != 64)) throw new InvalidOperationException("Missing compiled production source fingerprints.");
		foreach (var source in sources) Console.WriteLine($"  {source.Key}={source.Value}");
	}
	private static PropertyInfo? Property(object owner, string name)
	{
		for (var type = owner.GetType(); type != null; type = type.BaseType)
			if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } property) return property;
		return null;
	}
	private static IAction? Select(CustomRotation rotation, string name)
	{
		for (var type = rotation.GetType(); type != null; type = type.BaseType)
			if (type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } method)
			{
				object?[] args = [NoGcd, null];
				return (bool)method.Invoke(rotation, args)! ? (IAction?)args[1] : null;
			}
		throw new MissingMethodException(name);
	}
	private static void Require(bool condition, string message)
	{
		if (condition) { _checks++; return; }
		Failures.Add("FAIL " + message);
		Console.WriteLine(Failures[^1]);
	}
	private static void Case(string name, Action action)
	{
		try { action(); }
		catch (Exception ex) { Failures.Add(name + ": " + ex.GetBaseException().Message); Console.WriteLine(Failures[^1]); }
	}
}
