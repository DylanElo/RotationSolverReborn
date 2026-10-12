import copy
import unittest
from unittest.mock import patch

import mch_score


class LoaderTests(unittest.TestCase):
    def raw(self):
        return {"name": "Player", "start": 1000, "end": 31000, "names": {"8": "Crystal"},
                "ev": {"casts": [{"timestamp": 1200, "type": "cast", "sourceID": 1,
                                  "targetID": 8, "ability": {"name": "Wildfire"}}],
                       "damage-done": [], "buffs": []}}

    def test_legacy_missing_streams_not_complete(self):
        pull = mch_score.normalise_fflogs(self.raw(), "old cache")
        self.assertFalse(pull["availability"]["pets"])
        self.assertFalse(pull["availability"]["deaths"])
        result = mch_score.score(pull)
        self.assertEqual(result["wildfires"][0]["outcome"], "no_detonation_observed")

    def test_buff_open_at_end_and_other_actor_filter(self):
        raw = self.raw()
        raw["ev"]["buffs"] = [
            {"timestamp": 2000, "type": "applybuff", "sourceID": 9, "targetID": 1,
             "ability": {"name": "Divination", "guid": 100}},
            {"timestamp": 2100, "type": "applybuff", "sourceID": 9, "targetID": 2,
             "ability": {"name": "Battle Litany", "guid": 101}}]
        pull = mch_score.normalise_fflogs(raw, "test")
        self.assertEqual(pull["buffs"], [(1.0, 30.0, "Divination")])

    def test_duplicate_stream_events_only_once_and_identity_retained(self):
        raw = self.raw()
        event = {"timestamp": 2000, "type": "damage", "sourceID": 1, "targetID": 8,
                 "targetInstance": 2, "amount": 123, "ability": {"name": "Drill"}}
        raw["ev"]["damage-done"] = [event]
        raw["ev"]["extra"] = [copy.deepcopy(event)]
        pull = mch_score.normalise_fflogs(raw, "test")
        self.assertEqual(len(pull["events"]), 2)
        self.assertEqual(pull["events"][1]["targetInstance"], 2)

    def test_pagination_starts_at_zero_and_rejects_loop(self):
        with patch.object(mch_score, "_get", side_effect=[
                {"events": [{"timestamp": 0}], "nextPageTimestamp": 10},
                {"events": [{"timestamp": 10}]}]) as get:
            self.assertEqual(len(mch_score._events("r", "deaths", 0, 20)), 2)
            self.assertNotIn("sourceid", get.call_args.args[0])
        with patch.object(mch_score, "_get", return_value={"events": [], "nextPageTimestamp": 0}):
            with self.assertRaises(ValueError):
                mch_score._events("r", "deaths", 0, 20)

    def test_actor_ambiguity_refused(self):
        raw = self.raw()
        raw["ev"]["casts"].append(dict(raw["ev"]["casts"][0], sourceID=2))
        with self.assertRaises(ValueError):
            mch_score.normalise_fflogs(raw, "ambiguous")


if __name__ == "__main__":
    unittest.main()
