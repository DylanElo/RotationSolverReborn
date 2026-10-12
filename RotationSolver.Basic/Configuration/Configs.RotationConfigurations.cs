using ECommons.ExcelServices;
using System.Collections.Concurrent;

namespace RotationSolver.Basic.Configuration;

internal partial class Configs
{
	/// <summary>
	/// Gets the configuration values owned by a specific rotation for a specific job.
	/// </summary>
	/// <param name="job">The job that owns the configuration values.</param>
	/// <param name="rotationChoice">The full type name of the rotation that owns the values.</param>
	/// <returns>The rotation's persisted configuration values.</returns>
	internal ConcurrentDictionary<string, string> GetRotationConfigurations(Job job, string rotationChoice)
	{
		if (!_rotationConfigurationsDict.TryGetValue(job, out var rotations))
		{
			rotations = _rotationConfigurationsDict[job] = [];
		}

		if (!rotations.TryGetValue(rotationChoice, out var configurations))
		{
			configurations = rotations[rotationChoice] = [];
		}

		return configurations;
	}
}
