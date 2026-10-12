using System.Collections.Concurrent;

namespace ECommons.ExcelServices
{
	internal enum Job : byte
	{
		MCH,
		GLA,
	}
}

namespace ECommons.Logging
{
	internal static class PluginLog
	{
		internal static void Error(string message)
		{
		}

		internal static void Warning(string message)
		{
		}
	}
}

namespace RotationSolver.Basic
{
	internal static class DataCenter
	{
		internal static Job Job { get; set; } = Job.MCH;
	}

	internal static class Service
	{
		internal static Configuration.Configs Config { get; } = new();
	}
}

namespace RotationSolver.Basic.Configuration
{
	internal partial class Configs
	{
		private readonly Dictionary<Job, Dictionary<string, ConcurrentDictionary<string, string>>> _rotationConfigurationsDict = [];

		internal string RotationChoice { get; set; } = string.Empty;

		internal ConcurrentDictionary<string, string> RotationConfigurations
			=> GetRotationConfigurations(DataCenter.Job, RotationChoice);
	}
}

namespace RotationSolver.Basic.Rotations
{
	public interface ICustomRotation
	{
	}
}

namespace RotationSolver.Basic.Rotations.Duties
{
	public class DutyRotation
	{
		public enum PhantomJob : byte
		{
			None,
		}
	}
}
