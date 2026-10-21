using RotationSolver.RebornRotations;

namespace FightSim;

/// <summary>
/// FRU P1 Fatebreaker padding (FRUPad.Padding, the real code on the sim's 1/s HP record and cast replay).
/// Synthetic HP curves cover the cast, the pre-cast 160s fallback and the guards; the logged P1 ends pin what it did
/// to real pulls: five MCH kills where it never fires, and gNf-3, where the party killed Fatebreaker 1.6s early.
/// </summary>
internal static class FruPadCheck
{
	private const uint Fatebreaker = 0x459B;
	private const uint Enrage = 40128;

	public static void Run()
	{
		var checks = 0;
		void Require(bool condition, string name)
		{
			if (!condition) throw new InvalidOperationException("FRU pad: " + name);
			checks++;
		}

		// hp(t) = h0 - rate * (t - t0): the party's steady damage.
		bool PadAt(float t, float h0, float t0, float rate, bool cast = true, bool fru = true, bool otherTarget = false)
		{
			var hp = new List<float[]>();
			for (var s = 0f; s <= 200f; s += .5f) hp.Add([s, Math.Max(0f, h0 - rate * (s - t0))]);
			var fixture = Fixture(new TimelineActor("fb", Fatebreaker, "Fatebreaker", [[0f, 200f]], hp, [[0f, 100f, 100f]], null),
				cast ? [new EnemyCast("fb", Enrage, 150.8f, 9.6f)] : []);
			Game.Reset(fixture, new Bard(.2f, .2f), 0);
			Game.IsInFRU = fru;
			Advance(t - 6f);
			Advance(t);
			return FRUPad.Padding(otherTarget ? null : Game.Hostiles.First(h => h.BaseId == Fatebreaker), Game.Now);
		}

		// Cast 150.8-160.4: the kill lands 5s+ early -> hold; too close or too slow -> hit.
		Require(PadAt(152.2f, .04f, 150f, .01f), "party killing it ~6s early during the cast holds");
		Require(!PadAt(152.2f, .04f, 150f, .002f), "party too slow to kill in time keeps hitting");
		Require(!PadAt(152.2f, .04f, 150f, 0f), "HP not dropping keeps hitting");
		Require(!PadAt(152.2f, .06f, 150f, .01f), "above 5% never holds");
		Require(!PadAt(156f, .04f, 150f, .005f), "last 5s of the cast always hits");
		// No cast bar yet: the phase end is assumed 160s of combat, floor 9.7s.
		Require(PadAt(145f, .06f, 140f, .008f, cast: false), "before the cast, ~2.5s kill with 15s left holds");
		Require(!PadAt(145f, .05f, 140f, .001f, cast: false), "before the cast, slow party keeps hitting");
		Require(PadAt(158f, .06f, 145f, .004f, cast: false), "past 160s with no cast info the 9.7s floor still holds a 2s kill");
		Require(!PadAt(158f, .06f, 145f, .0029f, cast: false), "past 160s with no cast info a ~8s kill hits");
		Require(!PadAt(152.2f, .04f, 150f, .01f, fru: false), "outside FRU never pads");
		Require(!PadAt(152.2f, .04f, 150f, .01f, otherTarget: true), "another target never pads");

		// Logged P1 ends. Expected: only gNf-3 holds, inside 149.5-153.6, never in the last 5s or above 5%.
		foreach (var name in new[] { "AWXjPmH98Vqhzya4-7", "AWXjPmH98Vqhzya4-8", "AWXjPmH98Vqhzya4-10", "jJXkAtKaFQPyH4Wn-5", "jJXkAtKaFQPyH4Wn-27", "gNfDjbmTPvGK16dx-3" })
		{
			var timeline = Timeline.Load(FindTimeline($"fru-mch-{name}.json"));
			var cast = timeline.EnemyCasts!.First(c => c.Id == Enrage);
			var end = cast.Start + cast.Duration;
			Game.Reset(timeline, new Bard(.2f, .2f), 0);
			Advance(138f);
			var held = new List<float>();
			while (Game.Now < end - .01f) // after the cast bar ends it is a wipe either way
			{
				Advance(Game.Now + .25f);
				if (Game.Now >= end - .01f) break;
				if (Game.Hostiles.FirstOrDefault(h => h.BaseId == Fatebreaker) is not { } fb || !FRUPad.Padding(fb, Game.Now)) continue;
				held.Add(Game.Now);
				Require(fb.GetHealthRatio() <= .05f, $"{name}: held above 5% at {Game.Now:F1}s");
				Require(end - Game.Now > 5f, $"{name}: held inside the last 5s at {Game.Now:F1}s");
			}
			if (name.StartsWith("gNf"))
				Require(held.Count > 0 && held.Min() >= 149.5f && held.Max() <= 153.6f, $"{name}: holds only 149.5-153.6s (got {(held.Count > 0 ? $"{held.Min():F1}-{held.Max():F1}" : "none")})");
			else
				Require(held.Count == 0, $"{name}: never holds (held {held.Count} ticks from {(held.Count > 0 ? held.Min() : 0):F1}s)");
		}
		Console.WriteLine($"FRU pad: {checks} checks passed.");
	}

	private static void Advance(float to)
	{
		while (Game.Now < to - .001f) Game.Step(Math.Min(.1f, to - Game.Now));
	}

	private static Timeline Fixture(TimelineActor actor, List<EnemyCast> casts) => new("fru-pad-check", "Test", 200f, [actor], [[0f, 100f, 100f]], [],
		[], [], [], [], 0, casts, [], []);

	private static string FindTimeline(string name)
	{
		for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "tests", "FightSim", "timelines", name))) return Path.Combine(dir.FullName, "tests", "FightSim", "timelines", name);
		throw new FileNotFoundException(name);
	}
}
