"""FFLogs fight -> FightSim timeline JSON.

python -I timeline.py <report code> <fight id> <player name> [out.json]

Pulls every event through the xivanalysis FFLogs proxy and keeps what the rotation reads from the game:
enemy targetability, HP and positions, the player's position and target, party buffs and the debuffs that lock
the player out. The player's own casts and damage are kept only to compare the sim with what happened.
"""
import json, os, sys, time, urllib.error, urllib.request

PROXY = "https://xivanalysis.com/proxy/fflogs"
# Party buffs on the player that RSR's Buffs catalog knows (name -> job, damage multiplier for a ranged physical DPS).
PARTY_BUFFS = {
    "Brotherhood": ("MNK", 1.05), "Divination": ("AST", 1.06), "Starry Muse": ("PCT", 1.05),
    "Battle Litany": ("DRG", 1.0), "Technical Finish": ("DNC", 1.05), "Arcane Circle": ("RPR", 1.03),
    "Embolden": ("RDM", 1.05), "Searing Light": ("SMN", 1.05),
    "The Balance": ("AST", 1.03), "The Spear": ("AST", 1.06),
}
# Other jobs' two-minute raid buffs: their casts mark the party's burst windows.
RAID_BUFFS = {"Brotherhood", "Divination", "Starry Muse", "Battle Litany", "Technical Finish", "Arcane Circle", "Embolden",
              "Searing Light", "Chain Stratagem", "Mug", "Dokumori"}
# Debuffs the rotation reads by name, and the ones that stop the player acting.
PLAYER_DEBUFFS = {"Return", "Stun", "Doom", "Down for the Count", "Deep Freeze", "Weakness"}


def get(path):
    req = urllib.request.Request(PROXY + path, headers={"User-Agent": "curl/8.0"})
    for attempt in range(5):  # the proxy throws the odd 5xx
        try:
            return json.load(urllib.request.urlopen(req, timeout=120))
        except urllib.error.HTTPError as e:
            if e.code < 500 or attempt == 4:
                raise
            time.sleep(3)


def main():
    code, fight_id, player = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    out = sys.argv[4] if len(sys.argv) > 4 else f"timelines/{code}-{fight_id}.json"
    meta = get(f"/report/fights/{code}?translate=true")
    fight = next(f for f in meta["fights"] if f["id"] == fight_id)
    t0, t1 = fight["start_time"], fight["end_time"]
    me = next(p for p in meta["friendlies"] if p["name"] == player and any(f["id"] == fight_id for f in p["fights"]))
    enemies = {p["id"]: p for p in meta["enemies"] if p["guid"] < 1_000_000}

    events, t = [], t0
    while t is not None and t < t1:
        page = get(f"/report/events/{code}?start={t}&end={t1}&translate=true")
        events += page["events"]
        t = page.get("nextPageTimestamp")

    sec = lambda ts: round((ts - t0) / 1000, 3)
    key = lambda i, inst: f"{i}.{inst or 1}"
    actors = {}

    def actor(i, inst):
        k = key(i, inst)
        if k not in actors:
            e = enemies[i]
            actors[k] = {"key": k, "baseId": e["guid"], "name": e["name"], "seen": [], "targetable": [], "hp": [], "pos": [], "death": None, "taken": []}
        return actors[k]

    me_pos, my_target, my_casts, my_damage, party, debuffs, open_buffs = [], [], [], 0, [], [], {}
    my_hits, enemy_casts, stats, raid_casts = [], [], {}, []
    for ev in events:
        ts, ty = sec(ev["timestamp"]), ev["type"]
        src, tgt = ev.get("sourceID"), ev.get("targetID")
        if tgt in enemies:
            a = actor(tgt, ev.get("targetInstance"))
            if ty == "damage" and ev.get("sourceIsFriendly"):  # only what the party actually hits counts as attackable
                a["seen"].append(ts)
                if ev.get("unmitigatedAmount"):  # the enemy's own damage-taken modifier (Ice Veil: 0.5)
                    a["taken"].append(ev["amount"] / ev["unmitigatedAmount"])
            r = ev.get("targetResources")
            if r and r.get("maxHitPoints"):
                a["hp"].append([ts, round(r["hitPoints"] / r["maxHitPoints"], 5)])
                a["pos"].append([ts, r["x"] / 100, r["y"] / 100])
            if ty == "death":
                a["death"] = ts
        if ty == "targetabilityupdate" and src in enemies:
            actor(src, ev.get("sourceInstance"))["targetable"].append([ts, ev["targetable"]])
        if tgt == me["id"] and ev.get("targetResources", {}).get("x") is not None:
            r = ev["targetResources"]
            me_pos.append([ts, r["x"] / 100, r["y"] / 100])
        if src == me["id"]:
            if ty == "cast":
                my_casts.append([ts, ev["ability"]["name"], key(tgt, ev.get("targetInstance")) if tgt in enemies else None])
                if tgt in enemies:
                    my_target.append([ts, key(tgt, ev.get("targetInstance"))])
            if ty == "damage":
                my_damage += ev.get("amount", 0)
                if ev.get("amount", 0) > 0:  # [t, ability, amount, crit, direct hit, DoT tick]
                    my_hits.append([ts, ev["ability"]["name"], ev["amount"], ev.get("hitType") == 2, bool(ev.get("directHit")), bool(ev.get("tick"))])
            if ty == "combatantinfo":
                stats = {"crit": ev.get("simulatedCrit", 0.2), "dh": ev.get("simulatedDirectHit", 0.2)}
        if ty == "begincast" and src in enemies:
            enemy_casts.append({"key": key(src, ev.get("sourceInstance")), "id": ev["ability"]["guid"], "start": ts, "duration": ev.get("duration", 0) / 1000})
        name = ev.get("ability", {}).get("name")
        if ty == "cast" and ev.get("sourceIsFriendly") and src != me["id"] and name in RAID_BUFFS:
            raid_casts.append(ts)
        if tgt == me["id"] and src != me["id"] and name in PARTY_BUFFS:
            if ty in ("applybuff", "refreshbuff"):
                open_buffs.setdefault(name, ts)
            elif ty == "removebuff" and name in open_buffs:
                job, mult = PARTY_BUFFS[name]
                party.append({"name": name, "job": job, "mult": mult, "start": open_buffs.pop(name), "end": ts})
        if tgt == me["id"] and name in PLAYER_DEBUFFS:
            if ty == "applydebuff":
                open_buffs.setdefault("debuff:" + name, ts)
            elif ty == "removedebuff" and "debuff:" + name in open_buffs:
                debuffs.append({"name": name, "start": open_buffs.pop("debuff:" + name), "end": ts})

    for name, start in list(open_buffs.items()):  # still up at the kill
        if not name.startswith("debuff:"):
            job, mult = PARTY_BUFFS[name]
            party.append({"name": name, "job": job, "mult": mult, "start": start, "end": sec(t1)})

    # A death locks the player out until their next action (the raise, then the first cast after it).
    for ev in events:
        if ev["type"] == "death" and ev.get("targetID") == me["id"]:
            ts = sec(ev["timestamp"])
            back = next((c[0] for c in my_casts if c[0] > ts), sec(t1))
            debuffs.append({"name": "Dead", "start": ts, "end": back})

    duration = sec(t1)
    actors = {k: a for k, a in actors.items() if a["seen"]}
    for a in actors.values():
        # Targetable spans: from the first sighting (or a targetable update) until untargetable, death or last sighting.
        spans, state, since = [], None, None
        ups = sorted(a.pop("targetable"))
        first, last = min(a["seen"]), max(a["seen"])
        if not ups or ups[0][1] == 0:
            state, since = 1, first
        for ts, v in ups:
            if v == 1 and state != 1:
                state, since = 1, ts
            elif v == 0 and state == 1:
                spans.append([since, ts])
                state = 0
        if state == 1:
            spans.append([since, a["death"] if a["death"] is not None else last])
        a["spans"] = [s for s in spans if s[1] - s[0] > 1.0]
        a.pop("seen")
        taken = sorted(a["taken"])
        a["taken"] = round(taken[len(taken) // 2], 3) if taken else 1.0
    # ponytail: only enemies the player targeted at least once. RSR's own hostile filter (line of sight, name lists,
    # TTK) is not in the log; FRU's dark crystals were hittable but RSR never counted them for AoE.
    targeted = {k for _, k in my_target}
    actors = {k: a for k, a in actors.items() if a["spans"] and a["hp"] and k in targeted}

    # BRD Plan windows: the plan is authored to the party's burst (its comments: party buffs land +0.4..+4.8s after a window
    # opens), so each cluster of other jobs' raid buffs opens one 2s earlier; the opener's is at the pull. Taken from the party,
    # not from the player's own Battle Voice, so the sim does not inherit the logged rotation's timing. Casts, not buffs on the
    # player: a player out of range (or dead) still has the window.
    raid = sorted(raid_casts)
    clusters = []
    for t in raid:
        if not clusters or t - clusters[-1] > 30:
            clusters.append(t)
    pots = [c[0] for c in my_casts if "Gemdraught" in c[1] or "Tincture" in c[1]]
    windows = []
    for i, first in enumerate(clusters):
        start = 0.0 if i == 0 and first < 15 else max(0.0, first - 2.0)
        windows.append({"start": round(start, 2), "end": round(start + 20, 2), "potion": any(start - 15 <= p <= start + 25 for p in pots)})

    json.dump({
        "source": f"{code}#{fight_id}", "player": player, "duration": duration, "kill": fight.get("kill", False),
        "actors": list(actors.values()), "playerPos": me_pos, "playerTarget": my_target, "partyBuffs": party,
        "playerDebuffs": debuffs, "planWindows": windows, "loggedCasts": my_casts, "loggedDamage": my_damage,
        "loggedHits": my_hits, "stats": stats, "enemyCasts": enemy_casts,
    }, open(out, "w"))
    print(f"{out}: {duration:.0f}s, {len(actors)} enemies, {len(party)} party buffs, {len(debuffs)} debuffs, {len(windows)} windows, {len(my_casts)} casts")


if __name__ == "__main__":
    main()
