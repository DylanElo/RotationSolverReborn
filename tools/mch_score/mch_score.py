"""Score MCH pulls using recorded outcomes, with xivanalysis-inspired checks.

python mch_score.py fflogs REPORT FIGHT [REPORT FIGHT ...] [--player-id ID]
python mch_score.py cache cache/REPORT-FIGHT.json [...] [--offline]
python mch_score.py capture mch_pulls.json [...] [--min-length 60]
python mch_score.py iinact Network_30301_YYYYMMDD.log [--player-name "Your Name"]  (or set MCH_SCORE_PLAYER)
All modes accept --json PATH. Cached FFLogs pulls are enriched unless --offline.
Gauge and GCD uptime below are estimates, never evidence of landed damage.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys
import time
import urllib.error
import urllib.request

from outcome_checks import analyse_outcomes

PROXY = "https://xivanalysis.com/proxy/fflogs/report"
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cache")
GCD = {"Heated Split Shot", "Heated Slug Shot", "Heated Clean Shot", "Drill", "Air Anchor", "Chain Saw", "Excavator",
       "Full Metal Field", "Blazing Shot", "Heat Blast", "Scattergun", "Bioblaster", "Auto Crossbow"}
RAIDBUFF = {"Divination", "Battle Litany", "Technical Finish", "Searing Light", "Brotherhood", "Embolden", "Starry Muse",
            "Arcane Circle", "Chain Stratagem", "Radiant Finale", "Battle Voice", "Dokumori", "Mug"}
POT = ("draught", "tincture", "Medicated")


def _get(url):
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            with urllib.request.urlopen(req, timeout=30) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code not in (429, 500, 502, 503, 504) or attempt == 2:
                raise
        except (urllib.error.URLError, TimeoutError):
            if attempt == 2:
                raise
        time.sleep(2 ** (attempt + 1))


def _save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temp, path)


def _events(code, kind, start, end, player_id=None):
    result, cursor = [], start
    while cursor < end:
        url = f"{PROXY}/events/{kind}/{code}?start={cursor}&end={end}&translate=true"
        if player_id is not None:
            url += f"&sourceid={player_id}"
        page = _get(url)
        if "events" not in page:
            raise ValueError(f"{kind}: response has no events; completeness unknown")
        result.extend(page["events"])
        following = page.get("nextPageTimestamp")
        if following is None:
            break
        if following <= cursor:
            raise ValueError(f"{kind}: pagination did not advance")
        cursor = following
    return result


def _metadata(code):
    path = Path(CACHE) / f"{code}-metadata.json"
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    data = _get(f"{PROXY}/fights/{code}?translate=true")
    if "fights" not in data or "friendlies" not in data:
        raise ValueError("Report metadata is incomplete")
    _save(path, data)
    return data


def load_fflogs(code, fight, player_id=None, offline=False, cache_path=None):
    path = Path(cache_path) if cache_path else Path(CACHE) / f"{code}-{fight}.json"
    raw = json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
    if raw is None and offline:
        raise FileNotFoundError(path)
    if not offline and (raw is None or raw.get("schema_version", 0) < 2):
        meta = _metadata(code)
        encounter = next(f for f in meta["fights"] if f["id"] == fight)
        candidates = [p for p in meta["friendlies"] if p.get("type") == "Machinist"
                      and any(f["id"] == fight for f in p.get("fights", []))]
        if player_id is not None:
            candidates = [p for p in candidates if p["id"] == player_id]
        elif raw is not None:
            candidates = [p for p in candidates if p["name"] == raw["name"]]
        if len(candidates) != 1:
            raise ValueError(f"{code}#{fight}: select one Machinist using --player-id; candidates={[(p['id'], p['name']) for p in candidates]}")
        player = candidates[0]
        if raw is not None:
            existing_ids = {e.get("sourceID") for e in raw["ev"]["casts"] if e.get("type") == "cast"}
            if existing_ids and existing_ids != {player["id"]}:
                raise ValueError("Cached player differs from selected player; use a separate cache file")
        else:
            raw = {"name": player["name"], "start": encounter["start_time"], "end": encounter["end_time"],
                   "names": {e["id"]: e["name"] for e in meta.get("enemies", [])}, "ev": {}}
        # Upgrade the old cache additively: its original streams are retained.
        for kind in ("casts", "damage-done", "buffs", "debuffs", "deaths"):
            if kind not in raw["ev"]:
                raw["ev"][kind] = _events(code, kind, raw["start"], raw["end"],
                                          None if kind == "deaths" else player["id"])
        raw.update(schema_version=2, player_id=player["id"],
                   pet_ids=[p["id"] for p in meta.get("friendlyPets", []) if p.get("petOwner") == player["id"]],
                   report_start=meta.get("start"), fight_id=fight, report=code,
                   encounter=encounter.get("name"), kill=encounter.get("kill"))
        _save(path, raw)
    if player_id is not None and raw.get("player_id") not in (None, player_id):
        raise ValueError("Cached player differs from --player-id")
    pull = normalise_fflogs(raw, f"{raw['name']} {code}#{fight}")
    if player_id is not None and pull["player_id"] != player_id:
        raise ValueError("Cached player differs from --player-id")
    return pull


def normalise_fflogs(raw, label):
    start, end = raw["start"], raw["end"]
    names = {int(k): v for k, v in raw.get("names", {}).items()}
    streams = raw["ev"]
    ids = {e.get("sourceID") for e in streams.get("casts", []) if e.get("type") == "cast"}
    player_id = raw.get("player_id")
    if player_id is None and len(ids) == 1:
        player_id = next(iter(ids))
    if player_id is None:
        raise ValueError("Cannot identify the player in cached casts")
    events, seen = [], set()
    for stream in streams.values():
        for original in stream:
            # Exact duplicates between streams must not create extra stacks or hits.
            key = json.dumps(original, sort_keys=True)
            if key in seen:
                continue
            seen.add(key)
            event = dict(original)
            event["timestamp"] = (original["timestamp"] - start) / 1000
            events.append(event)
    events.sort(key=lambda e: e["timestamp"])
    casts = []
    for event in events:
        if event["type"] != "cast" or event.get("sourceID") != player_id:
            continue
        resources = event.get("targetResources") or {}
        hp = 100 * resources["hitPoints"] / resources["maxHitPoints"] if resources.get("maxHitPoints") else None
        casts.append((event["timestamp"], event["ability"]["name"], names.get(event.get("targetID"), ""), hp))
    # Preserve buffs still active at pull end; do not silently discard them.
    buffs, active = [], {}
    for event in events:
        name = event.get("ability", {}).get("name", "")
        if event.get("targetID") != player_id or (name not in RAIDBUFF and not any(p.lower() in name.lower() for p in POT)):
            continue
        key = (event.get("sourceID"), event.get("ability", {}).get("guid", name))
        if event["type"] in ("applybuff", "refreshbuff"):
            active.setdefault(key, (event["timestamp"], name))
        elif event["type"] == "removebuff" and key in active:
            timestamp, name = active.pop(key)
            buffs.append((timestamp, event["timestamp"], name))
    duration = (end - start) / 1000
    buffs.extend((timestamp, duration, name) for timestamp, name in active.values())
    wf_damage = [(e["timestamp"], e.get("amount", 0)) for e in events
                 if e["type"] == "damage" and e.get("sourceID") == player_id and e.get("ability", {}).get("name") == "Wildfire"]
    start_iso = (datetime.fromtimestamp((raw["report_start"] + start) / 1000, timezone.utc).isoformat()
                 if raw.get("report_start") is not None else None)
    return {"label": label, "length": duration, "casts": casts, "buffs": buffs, "wf_damage": wf_damage,
            "events": events, "player_id": player_id, "pet_ids": raw.get("pet_ids"), "start_iso": start_iso,
            "encounter": raw.get("encounter"), "kill": raw.get("kill"),
            "availability": {"damage": "damage-done" in streams, "statuses": "buffs" in streams,
                             "pets": raw.get("pet_ids") is not None and "damage-done" in streams,
                             "deaths": "deaths" in streams}}


def load_captures(path, min_length):
    def timestamp(value):
        return datetime.fromisoformat(value[:23])
    pulls = []
    for pull in json.loads(Path(path).read_text(encoding="utf-8")):
        start = timestamp(pull["start"])
        casts = [((timestamp(a[0]) - start).total_seconds(), a[1], a[3], None) for a in pull["acts"]]
        if not casts or casts[-1][0] < min_length:
            continue
        buffs = [((timestamp(b[0]) - start).total_seconds(), (timestamp(b[0]) - start).total_seconds() + b[2], b[1])
                 for b in pull["buffs"]]
        # Legacy extracts discarded actor IDs and damage. Keep their cast-only limit explicit.
        events = [{"timestamp": t, "type": "cast", "sourceID": 0, "ability": {"name": name}}
                  for t, name, _, _ in casts]
        pulls.append({"label": f"capture {pull['start']}", "length": casts[-1][0], "casts": casts,
                      "buffs": buffs, "wf_damage": None, "events": events, "player_id": 0, "pet_ids": None,
                      "start_iso": pull["start"], "availability": {},
                      "evidence_limits": ["Legacy capture: target IDs, landed damage, statuses, pets, and complete death data unavailable."]})
    return pulls


def score(p):
    casts = sorted(p["casts"])
    gcds = [(t, n) for t, n, _, _ in casts if n in GCD]
    r = {"label": p["label"], "len": p["length"], "start_iso": p.get("start_iso"),
         "encounter": p.get("encounter"), "kill": p.get("kill"), "end_reason": p.get("end_reason")}
    outcomes = analyse_outcomes(p.get("events", []), p["length"], p.get("player_id"),
                                p.get("pet_ids"), p.get("availability", {}))
    r.update(outcomes)
    r["evidence_limits"] = list(dict.fromkeys(outcomes.get("evidence_limits", []) + p.get("evidence_limits", [])))
    raid = [b for b in p["buffs"] if b[2] in RAIDBUFF]
    for window in r["wildfires"]:
        window["raid_buffs_at_cast"] = sorted({n for start, end, n in raid if start <= window["t"] < end})
        window["target_name"] = next((target for t, name, target, _ in casts
                                      if name == "Wildfire" and abs(t - window["t"]) < .001), None)
    battery = heat = bat_lost = heat_lost = 0
    hypercharged_until = -1.0
    queens = []
    for t, name, _, _ in casts:
        gain_b = 10 if name == "Heated Clean Shot" else 20 if name in ("Air Anchor", "Chain Saw", "Excavator") else 0
        gain_h = 5 if name in ("Heated Split Shot", "Heated Slug Shot", "Heated Clean Shot") else 10 if name == "Scattergun" else 0
        bat_lost += max(0, battery + gain_b - 100)
        battery = min(100, battery + gain_b)
        heat_lost += max(0, heat + gain_h - 100)
        heat = min(100, heat + gain_h)
        if name == "Barrel Stabilizer":
            hypercharged_until = t + 30
        elif name == "Hypercharge":
            if t <= hypercharged_until:
                hypercharged_until = -1
            else:
                heat = max(0, heat - 50)
        elif name == "Automaton Queen":
            queens.append(battery)
            battery = 0
    idle = active = 0.0
    for (t0, n0), (t1, _) in zip(gcds, gcds[1:]):
        gap = t1 - t0
        if gap <= 6:
            active += gap
            idle += max(0.0, gap - (1.5 if n0 in ("Blazing Shot", "Heat Blast", "Auto Crossbow") else 2.5) - 0.1)
    r["estimates"] = {"queen_battery": queens, "battery_overcap": bat_lost, "heat_overcap": heat_lost,
                      "gcd_uptime": 100 * (1 - idle / active) if active else None,
                      "limits": "Cast-based gauge assumes zero starting gauge and successful combos; uptime treats gaps over 6s as downtime. Not pass/fail checks."}
    return r


def clock(t):
    return f"{int(t // 60)}:{t % 60:04.1f}"


def report(results):
    for r in results:
        print(f"\n== {r['label']} ({r['len'] / 60:.1f} min) {r.get('start_iso') or 'date unavailable'}")
        for w in r["wildfires"]:
            print(f"  WF {clock(w['t'])} target={w.get('target_id')}/{w.get('target_instance')} "
                  f"hits={w.get('hits')}/6 damage={w.get('damage')} {w.get('outcome')} "
                  f"buffs={len(w.get('raid_buffs_at_cast', []))} evidence={w.get('evidence')}")
        for key in ("hypercharges", "queens", "reassemble"):
            windows = r[key]
            counts = {value: sum(w.get("outcome") == value for w in windows) for value in sorted({str(w.get("outcome")) for w in windows})}
            print(f"  {key}: {counts}")
            for w in windows:
                if w.get("outcome") not in ("complete", "appropriate", "appropriate_aoe"):
                    print(f"    {clock(w['t'])}: {w.get('outcome')} {w.get('censored_reason') or ''} "
                          f"{w.get('action') or ''} casts={w.get('casts', '-')}")
        for w in r["potions"]:
            print(f"  Potion {clock(w['t'])} casts: {w.get('cast_coverage')}; damage landed during window: {w.get('coverage')}")
        for limit in r["evidence_limits"]:
            print(f"  LIMIT: {limit}")


def summary(results, title):
    print(f"\n{title}: {len(results)} pulls")
    for key in ("wildfires", "hypercharges", "queens", "reassemble"):
        windows = [w for r in results for w in r[key]]
        counts = {value: sum(w.get("outcome") == value for w in windows) for value in sorted({str(w.get("outcome")) for w in windows})}
        print(f"  {key}: {len(windows)} {counts}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=("fflogs", "cache", "capture", "iinact"))
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--min-length", type=float, default=0)
    parser.add_argument("--player-id", type=int)
    parser.add_argument("--player-name", default=os.environ.get("MCH_SCORE_PLAYER"))
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--json", dest="json_path")
    args = parser.parse_args(argv)
    pulls = []
    if args.mode == "fflogs":
        if len(args.inputs) % 2:
            parser.error("fflogs inputs must be REPORT FIGHT pairs")
        for code, fight in zip(args.inputs[::2], args.inputs[1::2]):
            pulls.append(load_fflogs(code, int(fight), args.player_id, args.offline))
    elif args.mode == "cache":
        for path in args.inputs:
            code, fight = Path(path).stem.rsplit("-", 1)
            pulls.append(load_fflogs(code, int(fight), args.player_id, args.offline, path))
    elif args.mode == "capture":
        for path in args.inputs:
            pulls.extend(load_captures(path, args.min_length))
    else:
        if not args.player_name:
            parser.error("iinact mode needs --player-name or the MCH_SCORE_PLAYER environment variable")
        from iinact_loader import load_iinact
        for path in args.inputs:
            pulls.extend(load_iinact(path, args.player_name, args.min_length))
    results = [score(p) for p in pulls if p["length"] >= args.min_length]
    report(results)
    summary(results, args.mode)
    if args.json_path:
        _save(args.json_path, {"schema_version": 2, "source": args.mode,
                              "xivanalysis_reference": "f532855e635bdfb4211cec8128d582dadfdc6a75",
                              "pulls": results})
    return 0


if __name__ == "__main__":
    sys.exit(main())
