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
		Case("Full Metal Field preserves Reassemble", () =>
		{
			Reset(0x45AF, 900);
			Game.Player.Statuses[StatusID.FullMetalMachinist] = 30;
			if (!MCH_Reborn.FullMetalFieldPvE.CanUse(out var fullMetal, skipTTKCheck: true)) throw new InvalidOperationException("FMF model fixture unavailable");
			Game.Player.Statuses[StatusID.Reassembled] = 5; // Exercise the game model independently of the rotation's optional hold.
			Game.Execute(fullMetal);
			Require(Game.Player.HasStatus(true, StatusID.Reassembled), "FMF is unaffected by Reassemble and preserves it for the next weaponskill");
		});

		foreach (var id in new[] { ChainSawPvE, ExcavatorPvE, FullMetalFieldPvE, DoubleCheckPvE, CheckmatePvE })
		Case("MCH observed secondary hit ratio " + id, () =>
		{
			Reset(0x45A9, 750);
			Game.Enemies.Add(new Enemy(new("oracle", 0x45AB, "Oracle", [[0, 2000]], [[0, 1]], [[0, 100, 100]], null), 999));
			Game.Step(.01f);
			Game.TtkOverride = 100;
			Game.Cooldown(id).FullAt = 0;
			if (id == ExcavatorPvE) Game.Player.Statuses[StatusID.ExcavatorReady] = 30;
			if (id == FullMetalFieldPvE) Game.Player.Statuses[StatusID.FullMetalMachinist] = 30;
			var action = (IBaseAction)typeof(MCH_Reborn).GetProperty(id.ToString(), BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;
			Require(action.CanUse(out var selected, skipTTKCheck: true), "two-target model fixture action is usable");
			Game.Execute(selected);
			var hits = Game.DamageApplications.TakeLast(2).ToArray();
			var ratio = id is DoubleCheckPvE or CheckmatePvE ? .70 : .75;
			Require(hits.Length == 2 && hits.All(h => h.Applied) && Math.Abs(hits[1].Damage / hits[0].Damage - ratio) < .0001,
				"secondary damage matches official 7.5 falloff and observed paired hits");
		});

		foreach (var paired in new[] { true, false })
		Case("P4 Reassemble follows available cleave " + paired, () =>
		{
			var rotation = Reset(0x45A9, 750);
			if (paired) Game.Enemies.Add(new Enemy(new("oracle", 0x45AB, "Oracle", [[0, 2000]], [[0, 1]], [[0, 100, 100]], null), 999));
			Game.Step(.01f);
			Game.TtkOverride = 100;
			Game.Player.Statuses[StatusID.Reassembled] = 5;
			foreach (var id in new[] { AirAnchorPvE, DrillPvE, ChainSawPvE }) Game.Cooldown(id).FullAt = 0;
			var next = rotation.NextGcd();
			Require(next?.ID == (uint)(paired ? ChainSawPvE : AirAnchorPvE),
				"a confirmed Reassemble takes available P4 cleave; the single-target priority stays unchanged");
		});

		Case("P2 does not consume Barrel into P1 buff tail", () =>
		{
			Reset(0x459B, 130);
			var fixture = Game.Timeline with {
				Actors = [new("p1", 0x459B, "Fatebreaker", [[0, 140]], [[0, 1]], [[0, 100, 100]], null),
					new("p2", 0x459F, "Shiva", [[144, 2000]], [[0, 1]], [[0, 100, 100]], null)],
				PartyBuffs = [new("Starry Muse", "PCT", 1.05f, 126, 146)] };
			var rotation = Reset(0x459B, 130, timeline: fixture);
			rotation.NextGcd();
			Game.Step(15);
			Game.Cooldown(BarrelStabilizerPvE).FullAt = Game.Cooldown(WildfirePvE).FullAt = 0;
			Game.TtkOverride = 150;
			rotation.NextGcd();
			Require((bool)Property(rotation, "ShouldHoldBarrel")!.GetValue(rotation)!,
				"a one-second P1 buff tail cannot consume the Barrel needed by P2's real burst");
			Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 150, 170));
			Game.Step(2); rotation.NextGcd(); Game.Step(4); rotation.NextGcd();
			Require(!(bool)Property(rotation, "ShouldHoldBarrel")!.GetValue(rotation)!,
				"a fresh P2 party burst is admitted normally");
		});
		Case("P1 Hypercharge anticipates padding", () =>
		{
			var actor = new TimelineActor("boss", 0x459B, "Fatebreaker", [[0, 160]],
				[[137, .104f], [140, .08f]], [[0, 100, 100]], null);
			Reset(0x459B, 137);
			var fixture = Game.Timeline with { Actors = [actor], PartyBuffs = [new("Starry Muse", "PCT", 1.05f, 130, 150)] };
			var rotation = Reset(0x459B, 137, timeline: fixture);
			Property(rotation, "PadFatebreaker")!.SetValue(rotation, true);
			Game.TtkOverride = 30;
			Machinist.Gauge.Heat = 100;
			Game.Cooldown(WildfirePvE).FullAt = Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Game.Step(3);
			// Wildfire and Barrel no longer look ahead to the pad (sim: it pushed P1/P2 Wildfire past the buffs).
			Require(rotation.NextAbility(NoGcd)?.ID != (uint)HyperchargePvE,
				"capped Heat cannot start a payload that upcoming padding would interrupt");
		});
		foreach (var left in new[] { 2f, 10f })
		Case("unclaimed prepared Barrel spends its free Hypercharge before expiry " + left, () =>
		{
			var rotation = Reset(0x459F, 300);
			Game.TtkOverride = 250;
			// The planned window is open but the party never buffed, so the Barrel never reads as unclaimed.
			Game.Timeline.PlanWindows.Add(new(290, 310, false));
			Game.Player.Statuses[StatusID.Hypercharged] = left;
			var ability = rotation.NextAbility(NoGcd);
			Require((ability?.ID == (uint)HyperchargePvE) == (left < 3f),
				"a free Hypercharge with no burst coming is spent in its last 3s, not earlier");
		});
		foreach (var ttk in new[] { 3f, 8f, 15f })
		Case("P1 capped Heat respects remaining payload lifetime " + ttk, () =>
		{
			var rotation = Reset(0x459B, 150);
			Game.TtkOverride = ttk;
			Machinist.Gauge.Heat = 100;
			var ability = rotation.NextAbility(NoGcd);
			Require((ability?.ID == (uint)HyperchargePvE) == (ttk >= 8),
				"a capped P1 gauge only starts Hypercharge when the complete payload can land before P2");
		});
		foreach (var disabled in new[] { true, false })
		Case("crystal reservation releases unusable Wildfire " + disabled, () =>
		{
			var rotation = Reset(0x45A3, 375);
			Game.TtkOverride = disabled ? 30 : 5;
			Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 374, 394));
			Game.Player.Statuses[StatusID.FullMetalMachinist] = 25;
			Game.Player.Statuses[StatusID.Hypercharged] = 25;
			Game.Cooldown(WildfirePvE).FullAt = Game.Now + 5;
			MCH_Reborn.WildfirePvE.Config.IsEnabled = !disabled;
			Game.Step(.01f);
			rotation.NextGcd();
			Require(!(bool)Property(rotation, "PreparedBurstBarrelAwaitsWildfire")!.GetValue(rotation)!,
				"disabled Wildfire or insufficient crystal lifetime cannot reserve the Barrel payload");
		});
		foreach (var baseId in new uint[] { 0x45AF, 0x45A9 })
		foreach (var recent in new[] { 20f, 12f, float.NaN })
		Case($"recent HP final Queen {baseId:X} {recent}", () =>
		{
			var hp = .6f;
			var actor = new TimelineActor("boss", baseId, "boss", [[0, 2000]],
				[[100, float.IsNaN(recent) ? hp : hp * (1 + 3 / recent)], [103, hp]], [[0, 100, 100]], null);
			Reset(baseId, 100);
			var fixture = Game.Timeline with { Actors = [actor] };
			var rotation = Reset(baseId, 100, timeline: fixture);
			Game.TtkOverride = 40;
			Machinist.Gauge.Battery = 90;
			Game.Step(3);
			var expected = baseId == 0x45AF && recent == 20;
			Require((bool)Property(rotation, "LastViableQueenWindow")!.GetValue(rotation)! == expected,
				"only final Pandora with sufficient recent lifetime opens the full Queen release window");
			if (expected) Require(rotation.NextAbility(NoGcd)?.ID == (uint)AutomatonQueenPvE,
				"the production selector executes the final full Queen from recent HP evidence");
		});
		Case("Wildfire snapshots the target debuff", () =>
		{
			Reset(0x45AF, 100);
			var fixture = Game.Timeline with { EnemyDebuffs = [new("boss", "Dokumori", "NIN", 1.05f, 99, 102)] };
			Reset(0x45AF, 100, timeline: fixture);
			Game.Cooldown(WildfirePvE).FullAt = 0;
			Machinist.Gauge.Heat = 50;
			if (!MCH_Reborn.WildfirePvE.CanUse(out var wildfire, skipTTKCheck: true)) throw new InvalidOperationException("snapshot Wildfire unavailable");
			Game.Execute(wildfire);
			Game.Step(.7f);
			MCH_Reborn.HeatedSplitShotPvE.CanUse(out var shot);
			Game.Execute(shot);
			Game.Step(11);
			Require(Math.Abs(Game.DamageByAction.GetValueOrDefault("Wildfire") - 240 * 1.05) < .01,
				"Wildfire retains the target's cast-time Dokumori multiplier after the debuff expires");
		});
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
		foreach (var lifetime in new[] { 3f, 20f })
		Case("low crystal HP uses predicted payload lifetime " + lifetime, () =>
		{
			var rotation = ActiveQueenWildfire(.15f);
			Game.TtkOverride = lifetime;
			Require((Select(rotation, "EmergencyAbility")?.ID == (uint)DetonatorPvE) == (lifetime == 3),
				"low HP alone cannot shorten Wildfire, but an earlier predicted death uses the available weave");
		});
		Case("crystal Wildfire banks its payload when focus moves away", () =>
		{
			var rotation = ActiveQueenWildfire();
			var veil = new Enemy(new("veil", 0x45A5, "Ice Veil", [[0, 2000]], [[0, 1]], [[0, 100, 100]], null), 999);
			Game.Enemies.Add(veil);
			Game.Step(.01f);
			Game.CurrentTargetOverride = veil.Chara;
			Game.TtkOverride = 20;
			Require(Select(rotation, "EmergencyAbility")?.ID == (uint)DetonatorPvE,
				"a living crystal's accumulated Wildfire is banked once the weaponskills move to another target");
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
		Case("partial crystal buffs keep Queen first then prepare Barrel while Wildfire waits", () =>
		{
			var rotation = Reset(0x45A3, 371f);
			Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 372, 392));
			Game.Timeline.PartyJobs!.Add("RPR"); // Arcane has not arrived: only one of the two expected buffs is live.
			Game.Timeline.PlanWindows.Add(new(372, 392, false));
			Game.Timeline.PartyBuffs.Add(new("Arcane Circle", "RPR", 1.03f, 370, 371.1f));
			Game.Step(.01f); // Synchronize the observed earlier buff through the native status boundary.
			rotation.NextGcd(); // Establish that this party's second buff exists, as earlier phases do in a real pull.
			Game.Step(1.548f);
			Game.TtkOverride = 30;
			Machinist.Gauge.Battery = 90;
			Machinist.Gauge.Heat = 100;
			foreach (var action in new[] { BarrelStabilizerPvE, WildfirePvE, ReassemblePvE, DoubleCheckPvE, CheckmatePvE })
				Game.Cooldown(action).FullAt = 0;
			var queen = rotation.NextAbility(NoGcd);
			Require(queen?.ID == (uint)AutomatonQueenPvE, "the crystal Queen retains the first actual weave before Barrel");
			if (queen?.ID != (uint)AutomatonQueenPvE) return;
			Game.Execute(queen);
			Game.Step(.7f);
			var barrel = rotation.NextAbility(NoGcd);
			Require((bool)Property(rotation, "ShouldCommitPersonalBurst")!.GetValue(rotation)!,
				"crystals commit to the first live buff without waiting for a missing party buff");
			Require(barrel?.ID == (uint)BarrelStabilizerPvE,
				"the next partial-buff crystal weave selects Barrel before Reassemble, capped Heat and filler charges can delay its UR recast");
			if (barrel?.ID == (uint)BarrelStabilizerPvE)
			{
				var at = Game.Now;
				Game.Execute(barrel);
				Require(Math.Abs(Game.Cooldown(BarrelStabilizerPvE).FullAt - (at + 120)) < .01f,
					"the actual prioritized crystal Barrel starts its genuine 120s recast");
				// 10-22 sim: the early Barrel read as a downtime Barrel, spent Full Metal Field and the free Hypercharge, and Wildfire
				// (needing that Heat) went out 13s late. They wait for the Wildfire that is 1.7s from ready.
				Game.Cooldown(WildfirePvE).FullAt = Game.Now + 1.7f;
				Machinist.Gauge.Heat = 50; // capped Heat would legitimately spend
				Game.Step(.7f);
				var after = rotation.NextGcd();
				Require(after?.ID != (uint)FullMetalFieldPvE,
					"the crystal Barrel's Full Metal Field waits for the Wildfire that follows");
				Require(rotation.NextAbility(after ?? NoGcd)?.ID != (uint)HyperchargePvE,
					"the crystal Barrel's free Hypercharge waits for the Wildfire that follows");
			}
		});
		foreach (var preceding in new[] { false, true })
		Case("earlier UR Barrel preserves CT and aligns Pandora " + preceding, () =>
		{
			var fixture = ObservedBarrelChain(preceding);
			var ready = preceding ? 493.29f : 492.558f;
			var firstBuff = preceding ? 861.05f : 856.702f;
			var buffEnd = preceding ? 881.018f : 876.659f;
			var rotation = Reset(0x45A7, ready, timeline: fixture);
			Game.TtkOverride = 250;
			Game.ForecastOverride = (6.167f, 10.174f);
			Game.Cooldown(BarrelStabilizerPvE).FullAt = ready;
			Game.Cooldown(WildfirePvE).FullAt = ready + 4.207f;
			var early = rotation.NextAbility(NoGcd);
			Require(early?.ID == (uint)BarrelStabilizerPvE,
				"a genuine planned P3 burst claims Barrel before the short UR Stun rather than waiting until its end");
			if (early?.ID != (uint)BarrelStabilizerPvE) return;
			Game.Execute(early);
			Game.ForecastOverride = null;
			Game.Step(ready + 16 - Game.Now);
			Require(Game.Player.Statuses.GetValueOrDefault(StatusID.FullMetalMachinist) > 0,
				"the real 30s Full Metal Machinist token survives UR and is available for the P3 burst");
			// Verify that the actual selector spends the prepared payload while its token remains valid.
			RunProductionFor(rotation, 14);
			Require(Game.Casts.Any(c => c.Time > ready && c.Time < ready + 30 && c.Action.StartsWith(nameof(FullMetalFieldPvE))),
				"prepared P3 Barrel supplies a selected Full Metal Field before its 30s token expires");
			Game.Step(ready + 120 - Game.Now);
			var p4 = rotation.NextAbility(NoGcd);
			Require(p4?.ID == (uint)BarrelStabilizerPvE,
				"the advanced P3 recast makes the approved P4 Barrel available near 612s");
			if (p4?.ID != (uint)BarrelStabilizerPvE) return;
			Game.Execute(p4);
			var ctReturn = fixture.Actors.First(a => a.BaseId == 0x45A9).Spans[1][0];
			RunProductionFor(rotation, ctReturn + 10 - Game.Now);
			// User's plan: the CT Barrel (~12:18) goes before the return and is back for P5.
			Require(Game.Casts.Any(c => c.Time >= ready + 240 && c.Time < ctReturn && c.Action == nameof(BarrelStabilizerPvE)),
				"the CT Barrel goes in the downtime before the P4 pair returns");
			Game.Step(firstBuff - Game.Now);
			Require(Game.Cooldown(BarrelStabilizerPvE).FullAt <= Game.Now,
				"the CT Barrel is back by the start of P5 buffs");
			Game.Cooldown(WildfirePvE).FullAt = 0;
			Machinist.Gauge.Heat = 0;
			RunProductionFor(rotation, 21);
			var p5 = Game.Casts.FirstOrDefault(c => c.Time >= firstBuff && c.Action == nameof(BarrelStabilizerPvE));
			Require(p5.Action != null && p5.Time < firstBuff + 2.5f,
				$"reserving Barrel makes its first P5 weave available at buff start; actual {p5.Time:F3}");
			Require(Game.Casts.Count(c => c.Time >= firstBuff && c.Time < buffEnd && c.Action == nameof(BlazingShotPvE)) >= 5,
				"all five actual P5 Blazing Shots fit inside the recorded Arcane interval after preserving CT");
		});
		Case("prepared UR Barrel within the ordinary five-second veto", () =>
		{
			var rotation = Reset(0x45A7, 495);
			Game.Timeline.PlanWindows.Add(new(508, 528, false));
			Game.ForecastOverride = (3f, 7f);
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Require(rotation.NextAbility(NoGcd)?.ID == (uint)BarrelStabilizerPvE,
				"an actual planned burst safely claims a 30s Barrel token three seconds before UR even inside the normal five-second veto");
		});
		foreach (var guard in new[] { "unknown plan", "downtime already started", "Auto Burst off", "wrong phase", "unknown return" })
		Case("early UR Barrel guard " + guard, () =>
		{
			var rotation = Reset(guard == "wrong phase" ? 0x45A9u : 0x45A7u, 492.558f);
			Game.Timeline.PlanWindows.Add(new(508, 528, false));
			if (guard == "unknown plan") Game.Timeline.PlanWindows.Clear();
			Game.ForecastOverride = (guard == "downtime already started" ? 0f : 6.167f, guard == "unknown return" ? float.MaxValue : 10.174f);
			Game.AutoBurst = guard != "Auto Burst off";
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Require(guard == "downtime already started"
				? !(bool)Property(rotation, "CanPrepareBarrelBeforeDowntime")!.GetValue(rotation)!
				: rotation.NextAbility(NoGcd)?.ID != (uint)BarrelStabilizerPvE,
				guard + " cannot activate the bounded pre-UR Barrel exception");
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
			Require(wildfire.Time >= start && wildfire.Time < 864, "P5 Wildfire commits with the already-ready Barrel during live buffs");
			Require(Game.Casts.Any(c => c.Time >= start && c.Time < 865 && c.Action == nameof(HyperchargePvE)),
				"P5 prepared Hypercharge supplies the Wildfire payload");
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
	private static Timeline ObservedBarrelChain(bool preceding)
	{
		// Narrow replay facts from the two 2026-10-22 local long pulls. HP is held healthy here;
		// these cases prove cooldown/availability and selected payloads, not a whole-fight DPS result.
		TimelineActor Actor(string key, uint id, List<float[]> spans) => new(key, id, key, spans, [[0, 1]], [[0, 100, 100]], null);
		var actors = preceding
			? new List<TimelineActor>
			{
				Actor("oracle", 0x45A7, [[440.594f, 602.019f]]),
				Actor("usurper", 0x45A9, [[607.876f, 705.539f], [755.997f, 773.031f]]),
				Actor("pairOracle", 0x45AB, [[634.04f, 707.103f], [755.997f, 772.942f]]),
				Actor("pandora", 0x45AF, [[850.431f, 1122.599f]]),
			}
			: new List<TimelineActor>
			{
				Actor("oracle", 0x45A7, [[439.704f, 600.821f]]),
				Actor("usurper", 0x45A9, [[606.663f, 704.831f], [754.856f, 771.792f]]),
				Actor("pairOracle", 0x45AB, [[632.853f, 705.859f], [754.856f, 771.97f]]),
				Actor("pandora", 0x45AF, [[849.211f, 1120.97f]]),
			};
		var plans = preceding
			? new List<PlanWindow> { new(504.787f, 524.787f, false), new(632.846f, 652.846f, false), new(852.178f, 872.178f, false) }
			: new List<PlanWindow> { new(503.68f, 523.68f, false), new(631.924f, 651.924f, false), new(851.893f, 871.893f, false) };
		var buffs = preceding
			? new List<PartyBuff> { new("Arcane Circle", "RPR", 1.03f, 510.14f, 530.096f), new("Arcane Circle", "RPR", 1.03f, 635.873f, 655.841f), new("Arcane Circle", "RPR", 1.03f, 861.05f, 881.018f) }
			: new List<PartyBuff> { new("Arcane Circle", "RPR", 1.03f, 509.028f, 529.024f), new("Arcane Circle", "RPR", 1.03f, 635.265f, 655.25f), new("Arcane Circle", "RPR", 1.03f, 856.702f, 876.659f) };
		var stuns = preceding
			? new List<Debuff> { new("Stun", 499.629f, 503.629f), new("Return", 736.812f, 743.812f), new("Stun", 743.82f, 752.82f) }
			: new List<Debuff> { new("Stun", 498.725f, 502.725f), new("Return", 735.722f, 742.722f), new("Stun", 742.727f, 751.727f) };
		return new("10-22 observed Barrel opportunity bounds", "MCH", 1130, actors, [[0, 100, 100]], [], buffs, stuns, plans, [], 0,
			[new("oracle", 40286, preceding ? 503.612f : 502.732f, 2.7f)], [], [], "MCH", ["MCH", "RPR"], [], true);
	}

	private static MCH_Plan CtDowntimeSnapshot(byte heat)
	{
		var pair = new TimelineActor("pair", 0x45A9, "pair", [[0, 730], [760, 780]], [[0, .2f]], [[0, 100, 100]], null);
		var pandora = new TimelineActor("pandora", 0x45AF, "pandora", [[850, 2000]], [[0, 1]], [[0, 100, 100]], null);
		var fixture = new Timeline("P4/CT/P5 boundary", "MCH", 2000, [pair, pandora], [[0, 100, 100]], [],
			[new("Starry Muse", "PCT", 1.05f, 857, 877)], [], [new(857, 877, false)], [], 0, [], [], [], "MCH", ["MCH", "PCT"], []);
		var rotation = Reset(0x45A9, 700, timeline: fixture);
		Game.TtkOverride = 250;
		rotation.NextAbility(NoGcd); // Observe actual P4 before the downtime snapshot.
		Machinist.Gauge.Heat = heat;
		Game.Step(35);
		Game.ForecastOverride = (float.MaxValue, 20f);
		Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
		return rotation;
	}
	private static MCH_Plan FundedCtReturn(byte heat)
	{
		var rotation = CtDowntimeSnapshot(heat);
		var barrel = rotation.NextAbility(NoGcd);
		Require(barrel?.ID == (uint)BarrelStabilizerPvE, "20s CT Barrel is selected with a confirmed window 122s away");
		if (barrel?.ID == (uint)BarrelStabilizerPvE) Game.Execute(barrel);
		Require(Game.Cooldown(BarrelStabilizerPvE).FullAt == 855, "confirmed CT Barrel has its genuine 120s recast");
		Game.Step(750.1f - Game.Now);
		Game.ForecastOverride = (float.MaxValue, 9.9f);
		rotation.NextAbility(NoGcd); // Observe the still-live token inside the final 15 seconds of CT.
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
	private static MCH_Plan ActiveQueenWildfire(float hp = .5f)
	{
		var rotation = Reset(0x45A3, 380, hp: hp);
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
