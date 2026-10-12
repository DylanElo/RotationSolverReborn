import tempfile
import unittest
from pathlib import Path

from iinact_loader import ability_damage, load_iinact


class LoaderTests(unittest.TestCase):
    def test_damage_encoding(self):
        self.assertEqual(ability_damage(0x730003, 0x8F650000), 36709)
        self.assertEqual(ability_damage(3, 0x423F400F), 999999)
        self.assertEqual(ability_damage(0x6500E, 0x35D0000), 0)
        self.assertEqual(ability_damage(3, 0x100), 0)

    def test_actor_ownership_stacks_and_boundaries(self):
        lines = [
            "01|2026-10-11T12:00:00+09:00|4D6|FRU",
            "03|2026-10-11T12:00:00+09:00|4001|Automaton Queen|00|64|1001",
            "03|2026-10-11T12:00:00+09:00|1001|Player One|1F|64|0",
            "260|2026-10-11T12:00:01+09:00|0|1",
            "03|2026-10-11T12:00:02+09:00|4002|Automaton Queen|00|64|1002",
            "21|2026-10-11T12:00:03+09:00|1001|Player One|B3E|Wildfire|5001|Enemy|0",
            "26|2026-10-11T12:00:04+09:00|35D|Wildfire|10|1001|Player One|5001|Enemy|00",
            "26|2026-10-11T12:00:05+09:00|A80|Overheated|10|1001|Player One|1001|Player One|05",
            "26|2026-10-11T12:00:06+09:00|A80|Overheated|9|1001|Player One|1001|Player One|04",
            "21|2026-10-11T12:00:08+09:00|4001|Automaton Queen|64BB|Crowned Collider|5001|Enemy|730003|8F650000",
            "21|2026-10-11T12:00:08+09:00|4002|Automaton Queen|64BB|Crowned Collider|5001|Enemy|730003|8F650000",
            "04|2026-10-11T12:00:09+09:00|4001|Automaton Queen",
            "03|2026-10-11T12:00:10+09:00|4001|Automaton Queen|00|64|1002",
            "21|2026-10-11T12:00:11+09:00|4001|Automaton Queen|64BB|Crowned Collider|5001|Enemy|730003|8F650000",
            "30|2026-10-11T12:00:14+09:00|35D|Wildfire|0|1001|Player One|5001|Enemy|06",
            "24|2026-10-11T12:00:14+09:00|5001|Enemy|DoT|35D|1E477|100|100|10000|10000|||0|0|0|0|1001|Player One|FFFFFFFF",
            "25|2026-10-11T12:00:15+09:00|1001|Player One|5001|Enemy",
            "260|2026-10-11T12:00:16+09:00|0|0",
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "network.log"
            path.write_text("\n".join(lines), encoding="utf-8")
            pulls = load_iinact(path, "Player One")
            self.assertEqual(load_iinact(path, "Player One", min_length=20), [])
        self.assertEqual(len(pulls), 1)
        pull = pulls[0]
        self.assertEqual(pull["length"], 15)
        self.assertEqual(pull["pet_ids"], [0x4001])
        wildfire = next(e for e in pull["events"] if e["type"] == "removedebuff")
        self.assertEqual((wildfire["stack"], wildfire["targetID"], wildfire["sourceID"]), (6, 0x5001, 0x1001))
        self.assertEqual(len([e for e in pull["events"] if e.get("ability", {}).get("name") == "Crowned Collider"]), 1)
        self.assertEqual([e["type"] for e in pull["events"] if e.get("ability", {}).get("name") == "Overheated"], ["applybuff", "removebuffstack"])
        self.assertTrue(pull["availability"]["damage"])
        self.assertEqual(pull["wf_damage"], [(13, 124023)])
        self.assertEqual(pull["end_reason"], "combat_end")

    def test_damage_requires_matching_target_and_sequence(self):
        base = ["21", "2026-10-11T12:00:03+09:00", "1001", "Player One", "4072", "Drill", "5001", "Enemy", "730003", "8F650000"]
        action = base + ["0"] * (44 - len(base)) + ["000030B6"]
        lines = ["01|2026-10-11T12:00:00+09:00|4D6|FRU", "03|2026-10-11T12:00:00+09:00|1001|Player One|1F|64|0", "260|2026-10-11T12:00:01+09:00|0|1", "|".join(action)]
        second = action.copy()
        second[6] = "5002"
        lines += ["|".join(second), "37|2026-10-11T12:00:04+09:00|5001|Enemy|000030B6", "260|2026-10-11T12:00:16+09:00|0|0"]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "network.log"
            path.write_text("\n".join(lines), encoding="utf-8")
            pull = load_iinact(path, "Player One")[0]
        self.assertEqual(len(pull["casts"]), 1)
        damage = [event for event in pull["events"] if event["type"] == "damage"]
        self.assertEqual([(event["timestamp"], event["amount"], event["targetID"]) for event in damage], [(3, 36709, 0x5001)])
        self.assertEqual(pull["unconfirmed_damage_packets"], 1)
        unresolved = [event for event in pull["events"] if event["type"] == "unconfirmed_damage"]
        self.assertEqual(len(unresolved), 1)
        self.assertEqual((unresolved[0]["targetID"], unresolved[0]["packetID"], unresolved[0]["amount_unconfirmed"]), (0x5002, "000030B6", 36709))
        self.assertNotIn("amount", unresolved[0])


if __name__ == "__main__":
    unittest.main()
