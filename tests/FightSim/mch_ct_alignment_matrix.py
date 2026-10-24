"""Vary final P4 kill speed and P5 buffs; replay production from pull start.

Usage: python mch_ct_alignment_matrix.py --dll PATH --sources A.json B.json --output DIR
shortcut: recorded CT mechanics stay fixed; later mechanics shift with the P4 kill,
while only raid-buff/plan timing varies independently. This is not live latency proof.
"""
import argparse
import concurrent.futures
import copy
import json
import pathlib
import subprocess


P4 = (0x45A9, 0x45AB)


def bounds(timeline):
    pair = [a for a in timeline["actors"] if a["baseId"] in P4]
    return (min(a["spans"][-1][0] for a in pair),
            max(a["spans"][-1][1] for a in pair),
            min(a["spans"][0][0] for a in timeline["actors"] if a["baseId"] == 0x45AF))


def first_p5_buff(timeline):
    p5 = bounds(timeline)[2]
    keys = {a["key"] for a in timeline["actors"] if a["baseId"] == 0x45AF}
    buffs = [b for b in timeline["partyBuffs"] if p5 <= b["start"] < p5 + 40]
    buffs += [b for b in timeline.get("enemyDebuffs", [])
              if b.get("key") in keys and p5 <= b["start"] < p5 + 40]
    if not buffs:
        raise ValueError("No P5 raid buff/debuff in the first 40 seconds")
    return min(b["start"] for b in buffs)


def scenario(original, kill_delta, buff_delay):
    t = copy.deepcopy(original)
    ct_return, old_kill, _ = bounds(t)
    new_kill = old_kill + kill_delta
    if new_kill <= ct_return:
        raise ValueError("P4 kill must follow CT return")

    def shift(time):
        if time < ct_return:
            return time
        if time < old_kill:
            return ct_return + (time - ct_return) * (new_kill - ct_return) / (old_kill - ct_return)
        return time + kill_delta

    # A faster/slower final P4 burn changes its HP trajectory and every subsequent
    # mechanic, including the long transition lockout and all P5 actors/casts.
    for actor in t["actors"]:
        for field in ("spans", "hp", "pos"):
            actor[field] = [[shift(v) for v in row] if field == "spans"
                            else [shift(row[0]), *row[1:]] for row in actor.get(field, [])]
        for field in ("death", "presentFrom"):
            if actor.get(field) is not None:
                actor[field] = shift(actor[field])
    for field in ("playerPos", "playerTarget", "loggedCasts", "loggedHits"):
        t[field] = [[shift(row[0]), *row[1:]] for row in t.get(field, [])]
    for field in ("partyBuffs", "enemyDebuffs", "playerDebuffs", "planWindows", "enemyCasts"):
        for event in t.get(field, []):
            start = event["start"]
            event["start"] = shift(start)
            if "end" in event:
                event["end"] = shift(event["end"])
            if "duration" in event and start + event["duration"] >= ct_return:
                event["duration"] = shift(start + event["duration"]) - event["start"]
    t["duration"] = shift(t["duration"])
    p5 = bounds(t)[2]
    for field in ("partyBuffs", "enemyDebuffs", "planWindows"):
        for event in t.get(field, []):
            if event["start"] >= p5:
                event["start"] += buff_delay
                event["end"] += buff_delay

    # Preserve pre-P5 facts. Only moved P5 uses are constrained by real party
    # cooldowns; duplicate same-cast debuffs on two targets share one delay.
    for field in ("partyBuffs", "enemyDebuffs"):
        last, previous = {}, {}
        for event in sorted(t.get(field, []), key=lambda x: x["start"]):
            key = (event["name"], event.get("job"))
            start = event["start"]
            duplicate = key in previous and abs(start - previous[key][0]) < .1
            wait = previous[key][1] if duplicate else max(0, last.get(key, -1e9) + 119.5 - start) if start >= p5 else 0
            if not duplicate:
                last[key] = start + wait
            previous[key] = (start, wait)
            event["start"] += wait
            event["end"] += wait

    t["source"] = f"COUNTERFACTUAL CT kill delta={kill_delta:g}; P5 buff delay={buff_delay:g}; " + original["source"]
    assert all(lo < hi for a in t["actors"] for lo, hi in a["spans"])
    # The entire P1/P2/crystal/UR input prefix must remain identical.
    for field in ("playerPos", "playerTarget", "loggedCasts", "loggedHits"):
        assert [r for r in t[field] if r[0] < ct_return] == [r for r in original.get(field, []) if r[0] < ct_return]
    for field in ("partyBuffs", "enemyDebuffs", "playerDebuffs", "planWindows", "enemyCasts"):
        assert [e for e in t.get(field, []) if e.get("end", e["start"] + e.get("duration", 0)) < ct_return] == [
            e for e in original.get(field, []) if e.get("end", e["start"] + e.get("duration", 0)) < ct_return]
    return t


def run(dll, config, path):
    command = ["dotnet", str(dll), str(path), "--seeds", "1", "--seed-report", "--resource-report"]
    if config:
        command += ["--config", config]
    output = path.with_suffix(".txt")
    with output.open("w", encoding="utf-8") as stream:
        subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, check=True)
    lines = output.read_text(encoding="utf-8").splitlines()
    return (json.loads(next(line[5:] for line in lines if line.startswith("SEED "))),
            [json.loads(line[9:]) for line in lines if line.startswith("RESOURCE ")])


def verify(timeline, seed, resources, baseline):
    ct_return, p4_kill, p5 = bounds(timeline)
    first_buff = first_p5_buff(timeline)
    p3 = min(a["spans"][0][0] for a in timeline["actors"] if a["baseId"] == 0x45A7)
    pair = [a for a in timeline["actors"] if a["baseId"] in P4]
    ct_start = max(a["spans"][-2][1] for a in pair)
    casts = seed["casts"]
    barrels = [c for c in casts if c["action"] == "BarrelStabilizerPvE"]
    ct_barrels = [c for c in barrels if ct_start <= c["time"] < p4_kill]
    failures = []

    def check(condition, message):
        if not condition:
            failures.append(message)

    prefix = lambda result: [(c["time"], c["action"], c.get("target")) for c in result["casts"] if c["time"] < p3]
    check(prefix(seed) == prefix(baseline[0]), "P1/P2/crystal cast trace changed")
    check([r for r in resources if r["time"] < p3] == [r for r in baseline[1] if r["time"] < p3],
          "P1/P2/crystal resource snapshots changed")
    check(len(ct_barrels) <= 1, "More than one CT Barrel")
    payloads = []
    for barrel in ct_barrels:
        spent = {}
        for action in ("FullMetalFieldPvE", "HyperchargePvE"):
            hit = next((c for c in casts if barrel["time"] < c["time"] < p4_kill and c["action"].startswith(action)), None)
            age = hit["time"] - barrel["time"] if hit else None
            check(age is not None and age < 30, f"CT {action} token not spent before 30s/P4 kill")
            spent[action] = {"time": hit["time"] if hit else None, "tokenAge": age}
        payloads.append({"barrel": barrel["time"], "consumers": spent})

    # If P5 Barrel has already fired, its previous cooldown was necessarily ready.
    # Otherwise the final pre-buff activation must have completed its 120s recast.
    p5_barrel = next((c for c in barrels if c["time"] >= p5), None)
    check(p5_barrel is not None, "No P5 Barrel activation in the complete replay")
    if p5_barrel and p5_barrel["time"] <= first_buff + .03:
        predecessor = [c for c in barrels if c["time"] < p5_barrel["time"]]
    else:
        predecessor = [c for c in barrels if c["time"] < first_buff]
    ready_at = predecessor[-1]["time"] + 120 if predecessor else 0
    check(ready_at <= first_buff + .03, f"Barrel ready {ready_at:.3f}, after first P5 buff {first_buff:.3f}")
    before_ct = [c for c in barrels if c["time"] < ct_start]
    earliest = max(ct_return - 30 + .03, before_ct[-1]["time"] + 120 if before_ct else 0)
    stun = next(d for d in timeline["playerDebuffs"] if d["name"] == "Stun" and ct_start < d["start"] < ct_return)
    latest = min(first_buff - 120, stun["start"] - .03)
    # Empty intervals must be skipped, never repaired by making P5 late.
    if earliest > latest:
        check(not ct_barrels, "Infeasible CT interval was used instead of skipped")
    return dict(firstP5Buff=first_buff, p5Barrel=p5_barrel["time"] if p5_barrel else None,
                readyAt=ready_at, readyLead=first_buff-ready_at, ctReturn=ct_return, p4Kill=p4_kill,
                feasibleCtInterval=[earliest, latest], ctSkipped=not ct_barrels, payloads=payloads,
                failures=failures, total=seed["total"], phase=seed["phase"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll")
    parser.add_argument("--sources", nargs="+", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--config")
    parser.add_argument("--kill-deltas", default="-16,-8,0,8")
    parser.add_argument("--buff-delays", default="0,4,8")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--generate-only", action="store_true")
    args = parser.parse_args()
    if not args.generate_only and not args.dll:
        parser.error("--dll is required unless --generate-only")
    root = pathlib.Path(args.output)
    root.mkdir(parents=True, exist_ok=True)
    jobs, baselines = [], {}
    for source in map(pathlib.Path, args.sources):
        original = json.loads(source.read_text(encoding="utf-8-sig"))
        original["planHiddenAcrossKills"] = True
        folder = root / source.stem
        folder.mkdir(exist_ok=True)
        baseline = folder / "baseline.json"
        baseline.write_text(json.dumps(original), encoding="utf-8")
        baselines[source.stem] = baseline
        for delta in map(float, args.kill_deltas.split(",")):
            for delay in map(float, args.buff_delays.split(",")):
                timeline = scenario(original, delta, delay)
                path = folder / f"kill{delta:+g}_buff{delay:g}.json"
                path.write_text(json.dumps(timeline), encoding="utf-8")
                jobs.append((source.stem, delta, delay, path, timeline))
        # A separate unchanged visible-plan control prevents the hidden-plan
        # safety matrix from passing only because every optional CT use is skipped.
        visible = copy.deepcopy(original)
        visible["planHiddenAcrossKills"] = False
        visible_name = source.stem + "-visible-plan"
        visible_baseline = folder / "visible-baseline.json"
        visible_baseline.write_text(json.dumps(visible), encoding="utf-8")
        baselines[visible_name] = visible_baseline
        visible_path = folder / "visible-reference.json"
        visible_path.write_text(json.dumps(visible), encoding="utf-8")
        jobs.append((visible_name, 0, 0, visible_path, visible))
    print(f"Generated {len(jobs)} whole-fight counterfactuals and {len(baselines)} unchanged baselines", flush=True)
    if args.generate_only:
        return
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        baseline_results = dict(pool.map(lambda item: (item[0], run(args.dll, args.config, item[1])), baselines.items()))

        def execute(job):
            name, delta, delay, path, timeline = job
            seed, resources = run(args.dll, args.config, path)
            result = verify(timeline, seed, resources, baseline_results[name])
            result.update(source=name, killDelta=delta, buffDelay=delay)
            print(f"{name} kill{delta:+g} buff{delay:g}: {'FAIL' if result['failures'] else 'PASS'}; "
                  f"P5 ready lead {result['readyLead']:.3f}s; CT {'skipped' if result['ctSkipped'] else 'spent'}", flush=True)
            return result

        results = list(pool.map(execute, jobs))
    visible = [r for r in results if r["source"].endswith("-visible-plan")]
    if not any(not r["ctSkipped"] for r in visible):
        for result in visible:
            result["failures"].append("Visible-plan references never exercise conditional CT Barrel")
    (root / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    failures = [r for r in results if r["failures"]]
    print(f"{len(results)-len(failures)}/{len(results)} passed; {sum(r['ctSkipped'] for r in results)} CT skips")
    raise SystemExit(bool(failures))


if __name__ == "__main__":
    main()
