using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;
namespace FightSim;
internal static class MchCtBarrelCheck
{
 public static void Run()
 {
  var checks = 0;
  foreach (var mode in new[] { "visible", "121 boundary", "near horizon", "120 no margin", "too late", "too distant", "token boundary", "token expiry", "zero return", "unknown return", "no AutoBurst", "dead pair", "no cooldown", "hidden next phase", "no window", "retained visible", "retained hidden", "Return clock" })
  {
   const float now = 100.2f;
   var nextIn = mode == "121 boundary" ? 121f : mode == "near horizon" ? 149f : mode == "120 no margin" ? 120f : mode == "too late" ? 119f : mode == "too distant" ? 151f : 130f;
   var returnIn = mode == "token boundary" ? 20.1f : mode == "token expiry" ? 25f : 18f;
   var hp = mode == "dead pair" ? 0f : .5f;
   var actor = new TimelineActor("pair", 0x45A9, "P4", [[0, 100.1f], [now + returnIn, 150]], [[0, hp]], [[0, 100, 100]], null);
   var pandora = new TimelineActor("pandora", 0x45AF, "P5", [[200, 2000]], [[0, 1]], [[0, 100, 100]], null);
   var hidden = mode is "hidden next phase" or "retained hidden";
   var fixture = new Timeline("CT Barrel cooldown deadline", "MCH", 2000, [actor, pandora], [[0, 100, 100]], [], [], [], [], [], 0, [], [], [], "MCH", ["MCH", "PCT"], [], hidden);
   var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [0x45A9u, 100f, true, hp, fixture])!;
   var noGcd = BaseAction.Create(None, (ref ActionSetting _) => { });
   // First observe a real current-phase plan, so hiding P5 exercises the production 999 sentinel.
   Game.Timeline.PlanWindows.Add(new(99, 100.1f, false));
   if (mode != "no window") Game.Timeline.PlanWindows.Add(new(now + nextIn, now + nextIn + 20, false));
   rotation.NextAbility(noGcd);
   var observedTarget = Game.Target;
   Game.Step(now - Game.Now);
   Game.ForecastOverride = (float.MaxValue, mode == "zero return" ? 0f : mode == "unknown return" ? float.MaxValue : returnIn);
   if (mode.StartsWith("retained"))
   {
    Game.CurrentTargetOverride = observedTarget;
    Require(observedTarget != null && !observedTarget.IsTargetable && !Game.Hostiles.Any(), "retained fixture keeps the departed hard target without an attackable hostile");
   }
   if (mode == "Return clock")
   {
    Game.Player.Statuses[StatusID.Return] = returnIn - 12.2f;
    Game.ForecastOverride = (float.MaxValue, 5f); // The native Return clock supersedes the prematurely short BMR estimate.
   }
   Game.AutoBurst = mode != "no AutoBurst";
   Game.Cooldown(BarrelStabilizerPvE).FullAt = mode == "no cooldown" ? 150 : 0;
   var selected = rotation.NextAbility(noGcd);
   // BMR's CT end reads ~5.5s early, so its 20s is ~25s real. The P5 window is hidden in live CT, so the 999 stand-in counts.
   var expected = mode is not ("token boundary" or "token expiry" or "zero return" or "unknown return" or "no AutoBurst" or "dead pair" or "no cooldown");
   Require((selected?.ID == (uint)BarrelStabilizerPvE) == expected,
    mode + ": CT Barrel fires within 20s of BossMod's return estimate while the P4 pair left alive");
   if (expected)
   {
    Game.Execute(selected!);
    Require(nextIn < 121f || hidden || mode == "no window" || Game.Cooldown(BarrelStabilizerPvE).FullAt + 1f <= now + nextIn + .001f,
     mode + ": executed CT Barrel is ready before the start of the confirmed P5 window, including one-second margin");
    Game.Step(returnIn + .01f);
    Game.CurrentTargetOverride = null;
    Game.ForecastOverride = null;
    Require(Game.Player.Statuses.GetValueOrDefault(StatusID.FullMetalMachinist) > 0 && Game.Hostiles.Any(),
     mode + ": the actual CT token survives until an attackable target returns");
    Require(MCH_Reborn.FullMetalFieldPvE.CanUse(out var metal, skipTTKCheck: true) && metal != null,
     mode + ": the prepared Full Metal Field is natively usable on return");
    Game.Execute(metal!);
    Require(!Game.Player.Statuses.ContainsKey(StatusID.FullMetalMachinist), mode + ": actual Full Metal Field consumes the prepared token");
   }
   checks++;
  }
  foreach (var mode in new[] { "safe wait", "terminal kill", "no buffs", "disabled Barrel", "short buffs", "far Barrel" })
  {
   var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, [0x45AFu, 983f, true, 1f, null])!;
   if (mode != "no buffs") Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 980, mode == "short buffs" ? 990 : 1005));
   Game.TtkOverride = mode == "terminal kill" ? 10 : 100;
   Game.AverageTtkOverride = mode == "terminal kill" ? 10 : 100;
   Game.Cooldown(BarrelStabilizerPvE).FullAt = mode == "far Barrel" ? 995 : 987;
   Game.Cooldown(WildfirePvE).FullAt = 0;
   MCH_Reborn.BarrelStabilizerPvE.Config.IsEnabled = mode != "disabled Barrel";
   Machinist.Gauge.Heat = 100;
   Game.Step(.02f); // Apply the newly added timeline buff to the native status boundary.
   rotation.NextGcd();
   var selected = rotation.NextAbility(BaseAction.Create(None, (ref ActionSetting _) => { })); // Refresh the production Decision and live-buff tracker.
   Require(!(bool)Property(rotation, "ReserveHeatForPandora"), "fixture has no inherited CT reservation masking the pending Barrel guard");
   var waits = (bool)Property(rotation, "WaitForPandoraBarrel");
   Require(waits == (mode == "safe wait"), mode + ": wait only when Barrel and the full payload fit the boss and remaining buffs");
   if (mode == "safe wait")
   {
    Require(!(bool)Property(rotation, "ShouldUseWildfire"), "safe wait reserves Wildfire for the imminent Barrel");
    Require(!(bool)Property(rotation, "ShouldUseHypercharge"), "safe wait also reserves capped Heat without relying on CT reservation");
    Require(selected?.ID != (uint)WildfirePvE && selected?.ID != (uint)HyperchargePvE, "actual selector cannot split the prepared Pandora payload");
   }
   if (mode == "terminal kill")
    Require((bool)Property(rotation, "ShouldUseWildfire"), "terminal Wildfire remains eligible instead of waiting beyond boss death");
   checks++;
  }
  Console.WriteLine($"CT Barrel: {checks} selector scenarios passed.");
 }

 private static object Property(object rotation, string name)
  => typeof(MCH_SyncPlus).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rotation)!;

 private static void Require(bool value, string message)
 {
  if (!value) throw new InvalidOperationException(message);
 }
}
