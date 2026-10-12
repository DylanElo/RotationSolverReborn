"""Outcome checks for normalized FFLogs events; missing streams are never successes.

Timestamps are pull-relative seconds. FFLogs status duration remains milliseconds.
Damage means landed `damage` events, never the earlier `calculateddamage` event.
"""

GCD = {"Heated Split Shot", "Heated Slug Shot", "Heated Clean Shot", "Split Shot",
       "Slug Shot", "Clean Shot", "Drill", "Air Anchor", "Chain Saw", "Excavator",
       "Full Metal Field", "Blazing Shot", "Heat Blast", "Scattergun", "Bioblaster",
       "Auto Crossbow", "Spread Shot", "Hot Shot"}
FAST = {"Blazing Shot", "Heat Blast", "Auto Crossbow"}
TOOLS = {"Drill", "Air Anchor", "Chain Saw", "Excavator"}


def _name(e):
    return e.get("ability", {}).get("name", "")


def _target(e):
    return e.get("targetID"), e.get("targetInstance")


def _landed(e):
    return e.get("type") == "damage" and e.get("amount", 0) > 0 and e.get("hitType") not in (0, 7, 8)


def analyse_outcomes(events, duration, player_id, pet_ids=None, availability=None):
    """Return per-window outcomes and evidence limits, without grading planned holds."""
    available = availability or {}
    events = sorted(events, key=lambda e: e["timestamp"])
    mine = [e for e in events if player_id is not None and e.get("sourceID") == player_id]
    casts, seen_casts = [], set()
    for e in mine:
        if e.get("type") != "cast":
            continue
        key = (_name(e), e.get("packetID", e["timestamp"]))
        if key not in seen_casts:
            casts.append(e)
            seen_casts.add(key)
    gcds = [e for e in casts if _name(e) in GCD]
    damage = [e for e in mine if _landed(e)]
    unconfirmed = [e for e in mine if e.get("type") == "unconfirmed_damage"]
    statuses = [e for e in events if e.get("targetID") == player_id and
                e.get("type") in ("applybuff", "refreshbuff", "removebuff", "applybuffstack", "removebuffstack")]
    deaths = [e for e in events if e.get("type") == "death"]
    result = {k: [] for k in ("wildfires", "hypercharges", "queens", "reassemble", "potions")}
    result["evidence_limits"] = [f"{key} stream incomplete or unavailable" for key in
                                 ("damage", "statuses", "pets", "deaths") if not available.get(key)]
    result["evidence_limits"].append("Queen attribution uses owned pet damage within 20 seconds of each summon")
    if player_id is None:
        result["evidence_limits"].append("Player identity unavailable; actor attribution cannot be checked")
        return result

    def status_start(name, t, tolerance=1.5):
        return next((e for e in statuses if _name(e) == name and e["type"] == "applybuff"
                     and t <= e["timestamp"] <= t + tolerance), None)

    def player_death(start, end):
        return next((e["timestamp"] for e in deaths if e.get("targetID") == player_id
                     and start <= e["timestamp"] <= end), None)

    def status_end(name, start, natural):
        return next((e["timestamp"] for e in statuses if _name(e) == name and e["type"] == "removebuff"
                     and start <= e["timestamp"] <= natural + .2), natural)

    def distinct_hits(hits):
        # One weaponskill contributes once, even if the log repeats its damage packet.
        seen, out = set(), []
        for e in hits:
            preceding = [c for c in gcds if _name(c) == _name(e) and
                         0 <= e["timestamp"] - c["timestamp"] <= 3]
            key = (_name(e), e.get("packetID")) if e.get("packetID") is not None else (
                _name(e), preceding[-1]["timestamp"] if preceding else e["timestamp"])
            if key not in seen:
                seen.add(key)
                out.append(e)
        return out

    for cast in [e for e in casts if _name(e) == "Wildfire"]:
        t, carrier = cast["timestamp"], _target(cast)
        debuff = next((e for e in mine if _name(e) == "Wildfire" and e.get("type") == "applydebuff"
                       and _target(e) == carrier and t <= e["timestamp"] <= t + 1.5), None)
        applied = debuff or status_start("Wildfire", t)
        start = applied["timestamp"] if applied else t
        natural = start + (applied.get("duration", 10000) / 1000 if applied else 10)
        det = next((e for e in damage if _name(e) == "Wildfire" and _target(e) == carrier
                    and start <= e["timestamp"] <= natural + 2), None)
        early = next((e["timestamp"] for e in casts if _name(e) == "Detonator"
                      and start <= e["timestamp"] <= natural), None)
        removal = next((e["timestamp"] for e in mine if _name(e) == "Wildfire" and
                        e.get("type") == "removedebuff" and _target(e) == carrier
                        and start <= e["timestamp"] <= natural + .2), None)
        end = min(duration, natural + .2, status_end("Wildfire", start, natural),
                  det["timestamp"] if det else natural + .2,
                  removal if removal is not None else natural + .2)
        target_death = next((e["timestamp"] for e in deaths if _target(e) == carrier
                             and start <= e["timestamp"] <= natural), None)
        died = player_death(start, natural)
        hits = distinct_hits([e for e in damage if _name(e) in GCD and not e.get("tick")
                              and _target(e) == carrier and start <= e["timestamp"] <= end])
        stacks = [e.get("stack") for e in events if _name(e) == "Wildfire" and
                  e.get("type") in ("applybuffstack", "applydebuffstack", "removedebuff") and
                  e.get("sourceID") == player_id and
                  (_target(e) == carrier or e.get("targetID") == player_id) and
                  start <= e["timestamp"] <= end + .2 and isinstance(e.get("stack"), (int, float))]
        count = int(max(stacks)) if stacks else len(hits) if available.get("damage") else None
        unresolved = [e for e in unconfirmed if _name(e) in GCD and _target(e) == carrier
                      and start <= e["timestamp"] <= end]
        censored = "pull ended" if duration < natural - .2 else "target died" if target_death is not None else "player died" if died is not None else None
        if count is None or not available.get("damage") or (unresolved and not stacks and count < 6):
            outcome = "unknown"
        elif det:
            outcome = "complete" if count >= 6 else "partial"
        elif censored:
            outcome = "censored"
        elif available.get("deaths"):
            outcome = "fizzled"
        else:
            outcome = "no_detonation_observed"
        result["wildfires"].append({"t": t, "start": start, "end": end,
            "target_id": carrier[0], "target_instance": carrier[1], "hits": count,
            "ideal_hits": 6, "evidence": "status_stacks" if stacks else "landed_damage_reconstruction" if available.get("damage") else "unavailable",
            "timing_evidence": "status" if applied else "cast_estimate",
            "damage": det["amount"] if det else 0 if available.get("damage") else None,
            "outcome": outcome, "censored_reason": censored, "early_detonator": early is not None,
            "unconfirmed_hits": len(unresolved)})

    for cast in [e for e in casts if _name(e) == "Hypercharge"]:
        t = cast["timestamp"]
        applied = status_start("Overheated", t)
        start = applied["timestamp"] if applied else t
        natural = start + (applied.get("duration", 10000) / 1000 if applied else 10)
        died = player_death(start, natural)
        end = min(duration, status_end("Overheated", start, natural), died if died is not None else natural)
        actions = [e for e in gcds if _name(e) in FAST and start <= e["timestamp"] <= end]
        count = len(actions)
        censored = "pull ended" if duration < natural else "player died" if died is not None else None
        result["hypercharges"].append({"t": t, "start": start, "end": end, "casts": count,
            "ideal_casts": 5, "actions": [{"t": e["timestamp"], "name": _name(e)} for e in actions],
            "outcome": "complete" if count >= 5 else "censored" if censored else "partial",
            "censored_reason": censored, "timing_evidence": "status" if applied else "cast_estimate"})

    summons = [e for e in casts if _name(e) == "Automaton Queen"]
    owned = set(pet_ids or [])
    pet_damage = [e for e in events if e.get("sourceID") in owned and _landed(e)]
    pet_unconfirmed = [e for e in events if e.get("sourceID") in owned and e.get("type") == "unconfirmed_damage"]
    for i, summon in enumerate(summons):
        t = summon["timestamp"]
        # shortcut: 20s bounds one Queen; use summon/despawn events if pet lifetimes change.
        natural = t + 20
        end = min(duration, natural, summons[i + 1]["timestamp"] if i + 1 < len(summons) else natural)
        hits = [e for e in pet_damage if t <= e["timestamp"] < end]
        collider = [e for e in hits if _name(e) == "Crowned Collider"]
        unresolved = [e for e in pet_unconfirmed if t <= e["timestamp"] < end]
        unresolved_collider = any(_name(e) == "Crowned Collider" for e in unresolved)
        died = player_death(t, end)
        censored = "pull ended" if duration < natural else "player died" if died is not None else None
        known = bool(available.get("pets") and available.get("damage") and pet_ids is not None)
        result["queens"].append({"t": t, "end": end, "damage": sum(e["amount"] for e in hits) if known else None,
            "collider_damage": sum(e["amount"] for e in collider) if known else None,
            "outcome": "unknown" if not known else "complete" if collider else "censored" if censored else "unknown" if unresolved_collider else "missing_finisher",
            "censored_reason": censored, "unconfirmed_hits": len(unresolved),
            "evidence": "confirmed_damage_lower_bound" if unresolved else "confirmed_damage" if known else "unavailable"})

    for cast in [e for e in casts if _name(e) == "Reassemble"]:
        t = cast["timestamp"]
        applied = status_start("Reassembled", t)
        start = applied["timestamp"] if applied else t
        end = min(duration, start + 5)
        removal = next((e for e in statuses if _name(e) == "Reassembled" and e["type"] == "removebuff"
                        and start <= e["timestamp"] <= end + 1), None)
        eligible = [e for e in gcds if t <= e["timestamp"] <= (removal["timestamp"] + .1 if removal else end)]
        consumed = [e for e in eligible if removal and abs(e["timestamp"] - removal["timestamp"]) <= .100001]
        action = consumed[-1] if consumed else eligible[0] if eligible and not removal else None
        name = _name(action) if action else None
        died = player_death(start, min(duration, start + 5.7))
        censored = "player died" if died is not None and not action else "pull ended" if duration < start + 5 and not action else None
        if name in TOOLS:
            outcome = "appropriate"
        elif name == "Scattergun":
            targets = {_target(e) for e in damage if _name(e) == name and action and
                       0 <= e["timestamp"] - action["timestamp"] <= 2}
            outcome = "appropriate_aoe" if len(targets) >= 3 else "aoe_context_unknown"
        elif name:
            outcome = "review_consumption"
        else:
            outcome = "censored" if censored else "unconsumed_or_unknown"
        result["reassemble"].append({"t": t, "action": name, "action_t": action["timestamp"] if action else None,
            "outcome": outcome, "censored_reason": censored,
            "evidence": "status_removal_and_cast" if consumed else "status_removal_unmatched" if removal else "next_gcd_estimate"})

    pots = [e for e in statuses if _name(e) == "Medicated" and e["type"] == "applybuff"]
    inferred = False
    if not pots:
        pots = [e for e in casts if any(s in _name(e).lower() for s in ("tincture", "draught", "potion of dexterity"))]
        inferred = True
    for pot in pots:
        t = pot["timestamp"]
        end = min(duration, status_end("Medicated", t, t + pot.get("duration", 30000) / 1000))
        hits = damage + pet_damage
        metrics = {}
        for label, names in (("wildfire", {"Wildfire"}), ("full_metal_field", {"Full Metal Field"}),
                             ("crowned_collider", {"Crowned Collider"}), ("blazing_shot", {"Blazing Shot", "Heat Blast"})):
            known = available.get("damage") and (label != "crowned_collider" or available.get("pets"))
            covered = [e for e in hits if _name(e) in names and t <= e["timestamp"] < end]
            metrics[label] = {"hits": len(covered), "damage": sum(e["amount"] for e in covered)} if known else None
        metrics["reassemble_consumptions"] = sum(1 for r in result["reassemble"] if r["action_t"] is not None and
                                                  t <= r["action_t"] < end and r["evidence"] == "status_removal_and_cast") if available.get("statuses") else None
        cast_coverage = {label: sum(1 for e in casts if _name(e) in names and t <= e["timestamp"] < end)
                         for label, names in (("wildfire", {"Wildfire"}), ("full_metal_field", {"Full Metal Field"}),
                                              ("blazing_shot", {"Blazing Shot", "Heat Blast"}))}
        cast_coverage["reassemble_consumptions"] = metrics["reassemble_consumptions"]
        unresolved = [e for e in unconfirmed + pet_unconfirmed if t <= e["timestamp"] < end]
        result["potions"].append({"t": t, "end": end, "evidence": "cast_30s_estimate" if inferred else "medicated_status",
                                  "coverage": metrics, "cast_coverage": cast_coverage,
                                  "coverage_basis": "damage_resolution_time_not_buff_snapshot",
                                  "unconfirmed_hits": len(unresolved),
                                  "coverage_completeness": "confirmed_damage_lower_bound" if unresolved else "as_available_streams"})
    return result
