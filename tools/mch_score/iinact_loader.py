"""Stream FRU network captures; preserve actor identity and observed status stacks.

Action damage requires target+sequence confirmation from NetworkActionSync (37).
Protocol: https://github.com/OverlayPlugin/cactbot/blob/main/docs/LogGuide.md
"""
from datetime import datetime


def ability_damage(flags, value):
    """Decode documented damage effects only; never treat status values as damage."""
    if flags & 0xFF not in {3, 5, 6} or value & 0x100:
        return 0
    return (value >> 16) + ((value & 0xFF) << 16 if value & 0x4000 else 0)


def load_iinact(path, player_name, min_length=0):
    pulls, current, in_zone, in_combat = [], None, False, False
    player_id, pets, names, owners = None, set(), {}, {}

    def resolve_pets():
        pets.clear()
        pets.update(actor for actor, owner in owners.items() if owner == player_id and names.get(actor) == "Automaton Queen")
        if current is not None:
            current["pet_ids"] = sorted(set(current["pet_ids"]) | pets)

    def number(value):
        return int(value or "0", 16)

    def finish(end, reason):
        nonlocal current
        if current is None:
            return
        current["length"] = (end - current.pop("_start")).total_seconds()
        for name, (start, duration) in current.pop("_buffs").items():
            current["buffs"].append((start, min(start + duration, current["length"]), name))
        current.pop("_seen")
        current.pop("_cast_seen")
        current.pop("_statuses")
        pending = current.pop("_pending")
        current["unconfirmed_damage_packets"] = len(pending)
        for event in pending.values():
            event["type"] = "unconfirmed_damage"
            event["amount_unconfirmed"] = event.pop("amount")
            current["events"].append(event)
        current["events"].sort(key=lambda event: event["timestamp"])
        current["end_reason"] = reason
        if current["length"] >= min_length and any(c[1] in {"Drill", "Air Anchor", "Wildfire", "Blazing Shot", "Heat Blast"} for c in current["casts"]):
            pulls.append(current)
        current = None

    last = None
    with open(path, encoding="utf-8", errors="replace") as stream:
        for line in stream:
            fields = line.rstrip().split("|")
            kind = fields[0]
            if kind not in {"01", "03", "04", "21", "22", "24", "25", "26", "30", "37", "260"} or len(fields) < 3:
                continue
            when = datetime.fromisoformat(fields[1])
            last = when
            if kind == "03" and len(fields) > 6:
                actor = number(fields[2])
                names[actor] = fields[3]
                owners[actor] = number(fields[6])
                if fields[3] == player_name:
                    player_id = actor
                    if current is not None:
                        current["player_id"] = actor
                resolve_pets()
                continue
            if kind == "04":
                actor = number(fields[2])
                owners.pop(actor, None)
                names.pop(actor, None)
                pets.discard(actor)
                continue
            if kind == "01":
                finish(when, "zone_change")
                owners.clear()
                names.clear()
                pets.clear()
                player_id = None
                in_zone, in_combat = fields[2].upper() == "4D6", False
                continue
            if not in_zone:
                continue
            if kind == "260" and len(fields) > 3:
                active = fields[3] == "1"
                if active and not in_combat:
                    current = {"label": f"IINACT {when.isoformat()}", "start_iso": when.isoformat(),
                               "_start": when, "_buffs": {}, "_statuses": {}, "_seen": set(), "_cast_seen": set(), "_pending": {}, "casts": [], "buffs": [],
                               "events": [], "wf_damage": [], "player_id": player_id, "pet_ids": list(pets),
                               "availability": {"damage": True, "statuses": True, "pets": True, "deaths": True},
                               "evidence_limits": ["Only sequence-and-target-confirmed action damage is counted; missing sync records remain unconfirmed, not proven ghosts.",
                                                   "FRU pulls are delimited by observed game combat flags; no pre-pull status snapshot."]}
                elif not active and in_combat:
                    finish(when, "combat_end")
                in_combat = active
                continue
            if current is None:
                continue
            t = (when - current["_start"]).total_seconds()
            if kind in {"21", "22"} and len(fields) > 8:
                source, target = number(fields[2]), number(fields[6])
                if fields[3] == player_name:
                    player_id = source
                    current["player_id"] = source
                    resolve_pets()
                if source != player_id and source not in pets:
                    continue
                # One action may have one record per target; retain targets in events.
                key = (fields[1], fields[2], fields[4], fields[6], fields[44] if len(fields) > 44 else "")
                if key in current["_seen"]:
                    continue
                current["_seen"].add(key)
                packet = fields[44] if len(fields) > 44 else fields[1]
                cast_key = (source, fields[4], packet)
                event = {"timestamp": t, "type": "cast", "sourceID": source, "targetID": target,
                         "packetID": packet, "ability": {"name": fields[5], "guid": number(fields[4])}}
                fresh_cast = cast_key not in current["_cast_seen"]
                if fresh_cast:
                    current["events"].append(event)
                    current["_cast_seen"].add(cast_key)
                if len(fields) > 44:
                    amount = sum(ability_damage(number(fields[i]), number(fields[i + 1])) for i in range(8, 24, 2))
                    if amount:
                        current["_pending"][(packet, target)] = {**event, "type": "damage", "amount": amount}
                if source == player_id and fresh_cast:
                    hp = None
                    if len(fields) > 25 and fields[24].isdigit() and fields[25].isdigit() and int(fields[25]):
                        hp = 100 * int(fields[24]) / int(fields[25])
                    action = (t, fields[5], fields[7], hp)
                    if not current["casts"] or current["casts"][-1][:2] != action[:2]:
                        current["casts"].append(action)
            elif kind == "37" and len(fields) > 4:
                event = current["_pending"].pop((fields[4], number(fields[2])), None)
                if event is not None:
                    event["timestamp"] = t
                    current["events"].append(event)
            elif kind == "24" and len(fields) > 18 and fields[4] == "DoT" and fields[5].upper() == "35D" and number(fields[17]) == player_id:
                amount = number(fields[6])
                current["events"].append({"timestamp": t, "type": "damage", "sourceID": player_id,
                                          "targetID": number(fields[2]), "amount": amount,
                                          "ability": {"name": "Wildfire", "guid": 2878}})
                current["wf_damage"].append((t, amount))
            elif kind in {"26", "30"} and len(fields) > 9:
                source, target = number(fields[5]), number(fields[7])
                if source != player_id and target != player_id and source not in pets and target not in pets:
                    continue
                is_buff = target == player_id or target in pets
                status_key = (source, target, fields[2])
                stack = number(fields[9])
                old_stack = current["_statuses"].pop(status_key, None)
                suffix = "buff" if is_buff else "debuff"
                event_type = ("apply" if kind == "26" else "remove") + suffix
                if kind == "26":
                    current["_statuses"][status_key] = stack
                    if old_stack is not None:
                        event_type = ("remove" if stack < old_stack else "apply") + suffix + "stack" if stack != old_stack else "refresh" + suffix
                event = {"timestamp": t, "type": event_type,
                         "sourceID": source, "targetID": target, "stack": number(fields[9]), "duration": float(fields[4]) * 1000,
                         "ability": {"name": fields[3], "guid": 1000000 + number(fields[2])}}
                current["events"].append(event)
                if target == player_id:
                    name = fields[3]
                    old = current["_buffs"].pop(name, None)
                    if old:
                        current["buffs"].append((old[0], min(t, old[0] + old[1]), name))
                    if kind == "26":
                        current["_buffs"][name] = (t, float(fields[4]))
            elif kind == "25" and len(fields) > 5:
                current["events"].append({"timestamp": t, "type": "death", "sourceID": number(fields[4]),
                                          "targetID": number(fields[2])})
    if last is not None:
        finish(last, "capture_end")
    return pulls
