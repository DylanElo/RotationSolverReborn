using Dalamud.Game.Command;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using RotationSolver.Data;

namespace RotationSolver.Commands
{
	public static partial class RSCommands
	{
		private static readonly HashSet<string> _registeredCommands = [];

		internal static void Enable()
		{
			Disable();
			DataCenter.OnSpecialTypeChanged = specialType =>
			{
				var role = Player.Object?.ClassJob.Value.GetJobRole() ?? JobRole.None;
				_specialString = specialType.ToSpecialString(role);
			};

			// Auto and Off are arguments handled by OnCommand, not separate command names.
			foreach (var command in new[] { Service.COMMAND, Service.ALTCOMMAND })
			{
				if (Svc.Commands.AddHandler(command, new CommandInfo(OnCommand)
				{
					HelpMessage = UiString.Commands_Rotation.GetDescription(),
					ShowInHelp = true,
				}))
				{
					_registeredCommands.Add(command);
				}
			}
		}

		internal static void Disable()
		{
			foreach (var command in _registeredCommands)
			{
				_ = Svc.Commands.RemoveHandler(command);
			}
			_registeredCommands.Clear();
		}

		private static void OnCommand(string command, string arguments)
		{
			DoOneCommand(arguments ?? string.Empty);
		}

		private static void DoOneCommand(string command)
		{
			command = (command ?? string.Empty).Trim();

			// No args => open config
			if (command.Length == 0)
			{
				RotationSolverPlugin.OpenConfigWindow();
				return;
			}

			if (string.Equals(command, "cancel", StringComparison.OrdinalIgnoreCase))
			{
				command = "off";
			}

			if (TryGetOneEnum<StateCommandType>(command, out var stateType))
			{
				// Split command into parts
				var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				var index = -1;

				// Try to parse the second argument as TargetingType if present
				if (parts.Length > 1)
				{
					var value = parts[1];
					if (int.TryParse(value, out var targetingIndex))
					{
						index = targetingIndex;
					}
					else if (Enum.TryParse(typeof(TargetingType), value, true, out var parsedEnumSet))
					{
						var targetingTypeSet = (TargetingType)parsedEnumSet;
						var idx = Service.Config.TargetingTypes.IndexOf(targetingTypeSet);
						if (idx >= 0)
						{
							Service.Config.TargetingIndex = idx;
							if (Service.Config.ShowToggledSettingInChat)
							{
								Svc.Chat.Print($"Set current TargetingType to {targetingTypeSet}.");
							}
							index = idx;
						}
						else
						{
							Svc.Chat.PrintError($"{targetingTypeSet} is not in TargetingTypes list.");
							return;
						}
					}
				}

				DoStateCommandType(stateType, index);
			}
			else if (TryGetOneEnum<SpecialCommandType>(command, out var specialType))
			{
				DoSpecialCommandType(specialType);
			}
			else if (TryGetOneEnum<OtherCommandType>(command, out var otherType))
			{
				var extraCommand = command[otherType.ToString().Length..].Trim();
				DoOtherCommand(otherType, extraCommand);
			}
			else
			{
				RotationSolverPlugin.OpenConfigWindow();
			}
		}

		private static bool TryGetOneEnum<T>(string command, out T type) where T : struct, Enum
		{
			type = default;

			if (string.IsNullOrWhiteSpace(command))
			{
				return false;
			}

			// Parse only the first token (case-insensitive).
			var spaceIdx = command.IndexOf(' ');
			var token = spaceIdx >= 0 ? command[..spaceIdx] : command;

			return Enum.TryParse(token, ignoreCase: true, out type);
		}

		internal static string GetCommandStr(this Enum command, string extraCommand = "")
		{
			var cmdStr = $"{Service.COMMAND} {command}";
			if (!string.IsNullOrEmpty(extraCommand))
			{
				cmdStr += $" {extraCommand}";
			}
			return cmdStr;
		}
	}
}
