import unittest

from outcome_checks import analyse_outcomes


def event(t, name, kind="cast", source=1, target=10, **extra):
    return {"timestamp": t, "ability": {"name": name}, "type": kind,
            "sourceID": source, "targetID": target, **extra}


COMPLETE = dict(damage=True, statuses=True, pets=True, deaths=True)


class OutcomeChecks(unittest.TestCase):
    def analyse(self, events, duration=60, pets=None, availability=COMPLETE):
        return analyse_outcomes(events, duration, 1, pets, availability)

    def test_wildfire_counts_carrier_instance_landed_direct_hits_once(self):
        events = [event(0, "Wildfire", targetInstance=2)]
        for i in range(6):
            events.extend([event(i + 1, "Blazing Shot", "damage", amount=10, packetID=i, targetInstance=2),
                           event(i + 1, "Blazing Shot", "damage", amount=10, packetID=i, targetInstance=2),
                           event(i + 1, "Blazing Shot", "calculateddamage", amount=10, packetID=i, targetInstance=2)])
        events += [event(4, "Drill", "damage", target=20, amount=10),
                   event(4, "Drill", "damage", targetInstance=1, amount=10),
                   event(4, "Bioblaster", "damage", targetInstance=2, amount=10, tick=True),
                   event(4, "Drill", "damage", targetInstance=2, amount=0),
                   event(10, "Wildfire", "damage", targetInstance=2, amount=100, tick=True)]
        w = self.analyse(events)["wildfires"][0]
        self.assertEqual((w["hits"], w["damage"], w["outcome"]), (6, 100, "complete"))

    def test_wildfire_delayed_application_early_detonation_and_delayed_hit(self):
        events = [event(0, "Wildfire"), event(.65, "Wildfire", "applybuff", target=1, duration=10000),
                  event(.4, "Drill", "damage", amount=10), event(1, "Air Anchor", "damage", amount=10),
                  event(3, "Detonator"), event(3.2, "Drill", "damage", amount=10),
                  event(3.3, "Wildfire", "damage", amount=100)]
        w = self.analyse(events)["wildfires"][0]
        self.assertEqual((w["start"], w["hits"], w["end"], w["early_detonator"]), (.65, 2, 3.3, True))

    def test_wildfire_death_pull_end_and_missing_streams_are_distinct(self):
        events = [event(0, "Wildfire"), event(5, "Death", "death", target=10)]
        self.assertEqual(self.analyse(events)["wildfires"][0]["outcome"], "censored")
        self.assertEqual(self.analyse(events[:1], duration=5)["wildfires"][0]["censored_reason"], "pull ended")
        unknown = self.analyse(events[:1], availability={})["wildfires"][0]
        self.assertIsNone(unknown["hits"])
        self.assertIsNone(unknown["damage"])
        self.assertEqual(unknown["outcome"], "unknown")
        self.assertEqual(self.analyse(events[:1])["wildfires"][0]["outcome"], "fizzled")

    def test_status_stacks_take_precedence(self):
        events = [event(0, "Wildfire"), event(8, "Wildfire", "applydebuffstack", stack=6),
                  event(10, "Wildfire", "damage", amount=100)]
        w = self.analyse(events)["wildfires"][0]
        self.assertEqual((w["hits"], w["evidence"]), (6, "status_stacks"))

    def test_hypercharge_includes_tenth_second_auto_crossbow(self):
        events = [event(0, "Hypercharge")]
        events += [event(t, "Auto Crossbow") for t in [3, 4.5, 6, 7.5, 9.5]]
        h = self.analyse(events)["hypercharges"][0]
        self.assertEqual((h["casts"], h["outcome"]), (5, "complete"))
        events += [event(7, "Death", "death", target=1)]
        h = self.analyse(events)["hypercharges"][0]
        self.assertEqual((h["casts"], h["outcome"]), (3, "censored"))

    def test_terminal_wildfire_stack_without_damage_is_still_reported(self):
        events = [event(0, "Wildfire"), event(.5, "Wildfire", "applydebuff", duration=10000),
                  event(10.5, "Wildfire", "removedebuff", stack=6)]
        w = self.analyse(events, availability={"statuses": True, "deaths": True})["wildfires"][0]
        self.assertEqual((w["hits"], w["evidence"], w["outcome"]), (6, "status_stacks", "unknown"))
        self.assertIsNone(w["damage"])

    def test_queen_requires_owned_pet_and_observed_finisher(self):
        events = [event(0, "Automaton Queen"), event(15, "Crowned Collider", "damage", source=8, amount=100),
                  event(14, "Crowned Collider", "damage", source=9, amount=200)]
        self.assertEqual(self.analyse(events)["queens"][0]["outcome"], "unknown")
        q = self.analyse(events, pets=[8])["queens"][0]
        self.assertEqual((q["outcome"], q["collider_damage"]), ("complete", 100))
        self.assertEqual(self.analyse(events[:1], duration=8, pets=[8])["queens"][0]["outcome"], "censored")

    def test_reassemble_uses_removal_and_excavator_is_allowed(self):
        events = [event(0, "Reassemble"), event(.1, "Reassembled", "applybuff", target=1),
                  event(2, "Excavator"), event(2, "Reassembled", "removebuff", target=1)]
        r = self.analyse(events)["reassemble"][0]
        self.assertEqual((r["action"], r["outcome"], r["evidence"]), ("Excavator", "appropriate", "status_removal_and_cast"))
        r = self.analyse([event(0, "Reassemble"), event(2, "Scattergun")])["reassemble"][0]
        self.assertEqual((r["outcome"], r["evidence"]), ("aoe_context_unknown", "next_gcd_estimate"))

    def test_potion_actual_status_cutoff_and_unavailable_pet_damage(self):
        events = [event(1, "Medicated", "applybuff", target=1, duration=30000),
                  event(31, "Medicated", "removebuff", target=1),
                  event(30.8, "Full Metal Field"),
                  event(30.9, "Full Metal Field", "damage", amount=100),
                  event(31, "Full Metal Field", "damage", amount=200)]
        p = self.analyse(events, availability={"damage": True, "statuses": True})["potions"][0]
        self.assertEqual(p["coverage"]["full_metal_field"], {"hits": 1, "damage": 100})
        self.assertIsNone(p["coverage"]["crowned_collider"])
        self.assertEqual(p["cast_coverage"]["full_metal_field"], 1)
        self.assertEqual(p["coverage_basis"], "damage_resolution_time_not_buff_snapshot")

    def test_reassemble_expiration_does_not_credit_old_gcd_or_potion(self):
        events = [event(0, "Reassemble"), event(.1, "Reassembled", "applybuff", target=1),
                  event(2, "Drill"), event(5.1, "Reassembled", "removebuff", target=1),
                  event(0, "Medicated", "applybuff", target=1, duration=30000)]
        result = self.analyse(events)
        self.assertIsNone(result["reassemble"][0]["action"])
        self.assertEqual(result["reassemble"][0]["evidence"], "status_removal_unmatched")
        self.assertEqual(result["potions"][0]["coverage"]["reassemble_consumptions"], 0)

    def test_duplicate_hypercharge_cast_targets_do_not_inflate_count(self):
        events = [event(0, "Hypercharge"), event(2, "Auto Crossbow", target=10, packetID=1),
                  event(2, "Auto Crossbow", target=11, packetID=1),
                  event(4, "Auto Crossbow", target=10), event(4, "Auto Crossbow", target=11)]
        self.assertEqual(self.analyse(events)["hypercharges"][0]["casts"], 2)

    def test_terminal_stack_survives_slightly_earlier_player_removal(self):
        events = [event(0, "Wildfire"), event(.5, "Wildfire", "applydebuff", duration=10000),
                  event(10.45, "Wildfire", "removebuff", target=1),
                  event(10.5, "Wildfire", "removedebuff", stack=6)]
        self.assertEqual(self.analyse(events)["wildfires"][0]["hits"], 6)

    def test_unconfirmed_damage_keeps_missing_finisher_unknown(self):
        events = [event(0, "Automaton Queen"),
                  event(15, "Crowned Collider", "unconfirmed_damage", source=8, amount=100),
                  event(0, "Medicated", "applybuff", target=1)]
        result = self.analyse(events, pets=[8])
        self.assertEqual(result["queens"][0]["outcome"], "unknown")
        self.assertEqual(result["queens"][0]["unconfirmed_hits"], 1)
        self.assertEqual(result["potions"][0]["coverage_completeness"], "confirmed_damage_lower_bound")

    def test_unconfirmed_wildfire_hits_require_status_stacks_for_partial_verdict(self):
        events = [event(0, "Wildfire"), event(2, "Drill", "unconfirmed_damage", amount=100),
                  event(10, "Wildfire", "damage", amount=100)]
        self.assertEqual(self.analyse(events)["wildfires"][0]["outcome"], "unknown")
        events += [event(10, "Wildfire", "removedebuff", stack=4)]
        self.assertEqual(self.analyse(events)["wildfires"][0]["outcome"], "partial")

    def test_reassemble_death_and_delayed_expiration_are_not_consumptions(self):
        events = [event(0, "Reassemble"), event(.1, "Reassembled", "applybuff", target=1),
                  event(1.2, "Reassembled", "removebuff", target=1), event(3.2, "Death", "death", target=1)]
        r = self.analyse(events)["reassemble"][0]
        self.assertEqual((r["outcome"], r["censored_reason"]), ("censored", "player died"))
        events = [event(0, "Reassemble"), event(.1, "Reassembled", "applybuff", target=1),
                  event(5.7, "Reassembled", "removebuff", target=1)]
        r = self.analyse(events)["reassemble"][0]
        self.assertEqual((r["outcome"], r["evidence"]), ("unconsumed_or_unknown", "status_removal_unmatched"))


if __name__ == "__main__":
    unittest.main()
