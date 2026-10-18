namespace FightSim;

/// <summary>Seconds from action packet to damage packet, measured in zX9LjrWh3v1F8AMQ fight 12, BRD source 84.</summary>
public static class BardApplicationTiming
{
	// Medians of packetID-matched P4 casts and non-tick damage, collapsing repeated Barrage hits on one recipient.
	// Columns are first and second recipient application, not a known native primary-target ordering (AoE casts target Environment).
	// shortcut: fixed observed medians ignore distance and native recipient ordering; replace when richer action timing data is available.
	public static float Delay(string name, int targetIndex)
	{
		if (name.Contains("(DoT)", StringComparison.OrdinalIgnoreCase)) return 0;
		var normalized = name.Replace(" ", "").Replace("PvE", "").ToLowerInvariant();
		var secondary = targetIndex > 0;
		return normalized switch
		{
			"shot" => 0.756f, // 41 packets
			"heavyshot" or "burstshot" => 1.472f, // 7; lower-level aliases inherit their upgrade's estimate
			"straightshot" or "refulgentarrow" => 1.471f, // 3 packets; Barrage's later same-target hits are not secondary targets
			"venomousbite" or "causticbite" => 1.3115f, // 4
			"windbite" or "stormbite" => 1.289f, // 4
			"ironjaws" => 0.625f, // 3
			"empyrealarrow" => 1.025f, // 9
			"bloodletter" or "heartbreakshot" => 1.6505f, // 2
			"sidewinder" => 0.533f, // 3
			"quicknock" or "ladonsbite" => secondary ? 1.244f : 1.110f, // 17/16
			"widevolley" or "shadowbite" => secondary ? 1.598f : 1.465f, // 5/5
			"apexarrow" => secondary ? 1.199f : 1.066f, // 2/2
			"blastarrow" => secondary ? 1.7765f : 1.643f, // 2/2
			"resonantarrow" => secondary ? 1.290f : 1.157f, // 1/1
			"radiantencore" => secondary ? 2.114f : 1.9805f, // 2/2
			"pitchperfect" => secondary ? 0.933f : 0.800f, // 10/9
			"rainofdeath" => secondary ? 1.778f : 1.645f, // 15/14
			_ => 0,
		};
	}
}
