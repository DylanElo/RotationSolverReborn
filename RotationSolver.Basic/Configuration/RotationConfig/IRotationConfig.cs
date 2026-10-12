namespace RotationSolver.Basic.Configuration.RotationConfig;

/// <summary>
/// Represents a single configuration setting for rotation.
/// </summary>
public interface IRotationConfig
{
	/// <summary>
	/// Gets the name of this setting.
	/// </summary>
	string Name { get; }

	/// <summary>
	/// Gets the display name of this setting.
	/// </summary>
	string DisplayName { get; }

	/// <summary>
	/// Gets the default value for this configuration.
	/// </summary>
	string DefaultValue { get; }

	/// <summary>
	/// Gets the type of this configuration, indicating whether it is for PvP, PvE, or both.
	/// </summary>
	CombatType Type { get; }

	/// <summary>
	/// Gets the parent of this configuration for hierarchical display.
	/// </summary>
	string Parent { get; }

	/// <summary>
	/// Gets the optional section heading for this configuration.
	/// </summary>
	string Section { get; }

	/// <summary>
	/// Gets the stable display order for this configuration.
	/// </summary>
	int Order { get; }

	/// <summary>
	/// Gets the contextual help text for this configuration.
	/// </summary>
	string Tooltip { get; }

	/// <summary>
	/// Gets whether the current value is equivalent to the declared default.
	/// </summary>
	bool IsDefault { get; }

	/// <summary>
	/// Gets or sets the current value of this configuration.
	/// </summary>
	string Value { get; set; }

	/// <summary>
	/// Gets or sets the collection of sub-configurations associated with this configuration.
	/// </summary>
	IEnumerable<IRotationConfig>? Configs { get; set; }

	/// <summary>
	/// Executes a command to update the configuration.
	/// </summary>
	/// <param name="set">The rotation config set.</param>
	/// <param name="str">The command string.</param>
	/// <returns><c>true</c> if the command was executed successfully; otherwise, <c>false</c>.</returns>
	bool DoCommand(IRotationConfigSet set, string str);
}
