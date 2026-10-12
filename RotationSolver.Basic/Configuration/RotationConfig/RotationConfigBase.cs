using ECommons.Logging;
using RotationSolver.Basic.Rotations.Duties;
using System.Collections.Concurrent;
using static RotationSolver.Basic.Rotations.Duties.DutyRotation;

namespace RotationSolver.Basic.Configuration.RotationConfig;

/// <summary>
/// Base class for rotation configuration.
/// </summary>
internal abstract class RotationConfigBase : IRotationConfig
{
	private readonly PropertyInfo _property;
	private readonly ICustomRotation? _rotation;
	private readonly DutyRotation? _dutyRotation;
	private readonly string? _rotationChoice;

	private ConcurrentDictionary<string, string> RotationConfigurations
		=> _rotationChoice == null
			? Service.Config.RotationConfigurations
			: Service.Config.GetRotationConfigurations(DataCenter.Job, _rotationChoice);

	/// <summary>
	/// Gets the name of the configuration.
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// Gets the default value of the configuration.
	/// </summary>
	public string DefaultValue { get; }

	/// <summary>
	/// Gets the display name of the configuration.
	/// </summary>
	public string DisplayName { get; }

	/// <summary>
	/// Gets the combat type of the configuration.
	/// </summary>
	public CombatType Type { get; }

	/// <summary>
	/// Gets the Phantom Job for the configuration.
	/// </summary>
	public PhantomJob PhantomJob { get; }

	/// <summary>
	/// Gets the parent of this configuration.
	/// </summary>
	public string Parent { get; }

	/// <inheritdoc />
	public string Section { get; }

	/// <inheritdoc />
	public int Order { get; }

	/// <inheritdoc />
	public string Tooltip { get; }

	/// <summary>
	/// Gets the parent value of this configuration.
	/// </summary>
	public object? ParentValue { get; private set; }

	/// <summary>
	/// Gets or sets the value of the configuration.
	/// </summary>
	public string Value
	{
		get => !RotationConfigurations.TryGetValue(Name, out var config) ? DefaultValue : config;
		set
		{
			RotationConfigurations[Name] = value;
			SetValue(value);
		}
	}

	/// <summary>
	/// Gets or sets the collection of rotation configurations.
	/// </summary>
	public IEnumerable<IRotationConfig>? Configs { get; set; }

	/// <summary>
	/// Initializes a new instance of the <see cref="RotationConfigBase"/> class.
	/// </summary>
	/// <param name="rotation">The custom rotation instance.</param>
	/// <param name="property">The property information.</param>
	protected RotationConfigBase(ICustomRotation rotation, PropertyInfo property)
	{
		_property = property ?? throw new ArgumentNullException(nameof(property));
		_rotation = rotation ?? throw new ArgumentNullException(nameof(rotation));
		_rotationChoice = rotation.GetType().FullName ?? rotation.GetType().Name;

		Name = property.Name;
		DefaultValue = property.GetValue(rotation)?.ToString() ?? string.Empty;
		var attr = property.GetCustomAttribute<RotationConfigAttribute>();
		if (attr != null)
		{
			DisplayName = attr.Name;
			Type = attr.Type;
			Parent = attr.Parent;
			Section = attr.Section;
			Order = attr.Order;
			Tooltip = attr.Tooltip;

			// Use ParentValue from the attribute if specified
			if (attr.ParentValue != null)
			{
				ParentValue = attr.ParentValue;
			}
			// Otherwise, get the actual value from the parent property
			else if (!string.IsNullOrEmpty(Parent))
			{
				var parentProperty = rotation.GetType().GetProperty(Parent);
				if (parentProperty != null)
				{
					ParentValue = parentProperty.GetValue(rotation);
				}
			}
		}
		else
		{
			DisplayName = Name;
			Type = CombatType.None;
			Parent = string.Empty;
			Section = string.Empty;
			Order = int.MaxValue;
			Tooltip = string.Empty;
		}

		Reload();
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RotationConfigBase"/> class.
	/// </summary>
	/// <param name="rotation">The custom rotation instance.</param>
	/// <param name="property">The property information.</param>
	protected RotationConfigBase(DutyRotation rotation, PropertyInfo property)
	{
		_property = property ?? throw new ArgumentNullException(nameof(property));
		_dutyRotation = rotation ?? throw new ArgumentNullException(nameof(rotation));

		Name = property.Name;
		DefaultValue = property.GetValue(rotation)?.ToString() ?? string.Empty;
		var attr = property.GetCustomAttribute<RotationConfigAttribute>();
		if (attr != null)
		{
			DisplayName = attr.Name;
			Type = attr.Type;
			PhantomJob = attr.PhantomJob;
			Parent = attr.Parent;
			Section = attr.Section;
			Order = attr.Order;
			Tooltip = attr.Tooltip;

			// Use ParentValue from the attribute if specified
			if (attr.ParentValue != null)
			{
				ParentValue = attr.ParentValue;
			}
			// Otherwise, get the actual value from the parent property
			else if (!string.IsNullOrEmpty(Parent))
			{
				var parentProperty = rotation.GetType().GetProperty(Parent);
				if (parentProperty != null)
				{
					ParentValue = parentProperty.GetValue(rotation);
				}
			}
		}
		else
		{
			DisplayName = Name;
			Type = CombatType.None;
			Parent = string.Empty;
			Section = string.Empty;
			Order = int.MaxValue;
			Tooltip = string.Empty;
		}

		Reload();
	}

	/// <inheritdoc />
	public virtual bool IsDefault
		=> string.Equals(Value, DefaultValue, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Reloads the runtime property from its owning persisted configuration bucket.
	/// </summary>
	internal void Reload()
	{
		SetValue(RotationConfigurations.TryGetValue(Name, out var value) ? value : DefaultValue);
	}

	/// <summary>
	/// Sets the value of the property.
	/// </summary>
	/// <param name="value">The value to set.</param>
	private void SetValue(string value)
	{
		var type = _property.PropertyType;
		if (type == null)
		{
			return;
		}

		object? currentRotation = _rotation == null ? _dutyRotation : _rotation;
		if (currentRotation == null)
		{
			return; // can't possible update a null rotation
		}

		try
		{
			_property.SetValue(currentRotation, ChangeType(value, type));
		}
		catch (Exception ex)
		{
			PluginLog.Error($"Failed to convert type: {ex.Message}");
			_property.SetValue(currentRotation, ChangeType(DefaultValue, type));
		}
	}

	/// <summary>
	/// Changes the type of the value.
	/// </summary>
	/// <param name="value">The value to change.</param>
	/// <param name="type">The target type.</param>
	/// <returns>The converted value.</returns>
	private static object ChangeType(string value, Type type)
	{
		if (type.IsEnum)
		{
			// Attempt to match the value with the enum name
			if (Enum.IsDefined(type, value))
			{
				return Enum.Parse(type, value);
			}

			// Attempt to match the value with the Description attribute
			foreach (var field in type.GetFields())
			{
				var descriptionAttribute = field.GetCustomAttribute<DescriptionAttribute>();
				if (descriptionAttribute != null && descriptionAttribute.Description == value)
				{
					return Enum.Parse(type, field.Name);
				}
			}

			// Log a warning and return the default value if no match is found
			PluginLog.Warning($"Invalid enum value '{value}' for type '{type.Name}'. Using default value.");
			return Enum.GetValues(type).GetValue(0) ?? throw new InvalidOperationException($"No default value available for enum type '{type.Name}'.");
		}
		else if (type == typeof(bool))
		{
			return bool.Parse(value);
		}

		return Convert.ChangeType(value, type) ?? throw new InvalidOperationException($"Failed to convert value '{value}' to type '{type.Name}'.");
	}

	/// <summary>
	/// Executes a command.
	/// </summary>
	/// <param name="set">The rotation config set.</param>
	/// <param name="str">The command string.</param>
	/// <returns><c>true</c> if the command was executed; otherwise, <c>false</c>.</returns>
	public virtual bool DoCommand(IRotationConfigSet set, string str)
	{
		return str.StartsWith(Name);
	}

	/// <summary>
	/// Returns a string that represents the current object.
	/// </summary>
	/// <returns>A string that represents the current object.</returns>
	public override string ToString()
	{
		return Value;
	}
}
