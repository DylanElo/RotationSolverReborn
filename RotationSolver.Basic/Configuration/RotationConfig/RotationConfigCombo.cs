using RotationSolver.Basic.Rotations.Duties;

namespace RotationSolver.Basic.Configuration.RotationConfig;

/// <summary>
/// Represents a combo box rotation configuration.
/// </summary>
internal class RotationConfigCombo : RotationConfigBase
{
	/// <summary>
	/// Gets the display values for the combo box.
	/// </summary>
	public string[] DisplayValues { get; }

	/// <summary>
	/// Gets the enum names for the combo box.
	/// </summary>
	private string[] EnumNames { get; }

	/// <inheritdoc />
	public override bool IsDefault => ValueIndex(Value) == ValueIndex(DefaultValue);

	internal int ValueIndex(string value)
	{
		for (var i = 0; i < DisplayValues.Length; i++)
		{
			if (DisplayValues[i].Equals(value, StringComparison.OrdinalIgnoreCase)
				|| EnumNames[i].Equals(value, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RotationConfigCombo"/> class.
	/// </summary>
	/// <param name="rotation">The custom rotation instance.</param>
	/// <param name="property">The property information.</param>
	public RotationConfigCombo(ICustomRotation rotation, PropertyInfo property)
	: base(rotation, property)
	{
		if (!property.PropertyType.IsEnum)
		{
			throw new ArgumentException("Property type must be an enum", nameof(property));
		}

		List<string> names = [];
		List<string> enumNames = [];
		foreach (Enum v in Enum.GetValues(property.PropertyType))
		{
			// Retrieve the Description attribute if it exists
			var fieldInfo = property.PropertyType.GetField(v.ToString());
			var descriptionAttribute = fieldInfo?.GetCustomAttribute<DescriptionAttribute>();
			names.Add(descriptionAttribute?.Description ?? v.ToString());
			enumNames.Add(v.ToString());
		}

		DisplayValues = names.ToArray();
		EnumNames = enumNames.ToArray();

		// Set the Value to the display value of the default enum value
		var defaultEnumValue = property.GetValue(rotation);
		if (defaultEnumValue is Enum defaultEnum)
		{
			var defaultFieldInfo = property.PropertyType.GetField(defaultEnum.ToString());
			var defaultDescriptionAttribute = defaultFieldInfo?.GetCustomAttribute<DescriptionAttribute>();
			Value = defaultDescriptionAttribute?.Description ?? defaultEnum.ToString();
		}
		else
		{
			Value = DisplayValues[0]; // Fallback to the first item if no default is found
		}
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RotationConfigCombo"/> class.
	/// </summary>
	/// <param name="rotation">The custom rotation instance.</param>
	/// <param name="property">The property information.</param>
	public RotationConfigCombo(DutyRotation rotation, PropertyInfo property)
	: base(rotation, property)
	{
		if (!property.PropertyType.IsEnum)
		{
			throw new ArgumentException("Property type must be an enum", nameof(property));
		}

		List<string> names = [];
		List<string> enumNames = [];
		foreach (Enum v in Enum.GetValues(property.PropertyType))
		{
			// Retrieve the Description attribute if it exists
			var fieldInfo = property.PropertyType.GetField(v.ToString());
			var descriptionAttribute = fieldInfo?.GetCustomAttribute<DescriptionAttribute>();
			names.Add(descriptionAttribute?.Description ?? v.ToString());
			enumNames.Add(v.ToString());
		}

		DisplayValues = names.ToArray();
		EnumNames = enumNames.ToArray();

		// Set the Value to the display value of the default enum value
		var defaultEnumValue = property.GetValue(rotation);
		if (defaultEnumValue is Enum defaultEnum)
		{
			var defaultFieldInfo = property.PropertyType.GetField(defaultEnum.ToString());
			var defaultDescriptionAttribute = defaultFieldInfo?.GetCustomAttribute<DescriptionAttribute>();
			Value = defaultDescriptionAttribute?.Description ?? defaultEnum.ToString();
		}
		else
		{
			Value = DisplayValues[0]; // Fallback to the first item if no default is found
		}
	}

	/// <summary>
	/// Returns a string that represents the current object.
	/// </summary>
	/// <returns>A string that represents the current object.</returns>
	public override string ToString()
	{
		return Value;
	}

	/// <summary>
	/// Executes a command to update the combo box configuration.
	/// </summary>
	/// <param name="set">The rotation config set.</param>
	/// <param name="str">The command string.</param>
	/// <returns><c>true</c> if the command was executed; otherwise, <c>false</c>.</returns>
	public override bool DoCommand(IRotationConfigSet set, string str)
	{
		if (!base.DoCommand(set, str))
		{
			return false;
		}

		var numStr = str[Name.Length..].Trim();
		var length = DisplayValues.Length;

		var currentIndex = ValueIndex(Value);
		if (currentIndex == -1)
		{
			currentIndex = 0;
		}

		var nextId = (currentIndex + 1) % length;
		if (int.TryParse(numStr, out var num))
		{
			nextId = num % length;
		}
		else
		{
			for (var i = 0; i < length; i++)
			{
				if (DisplayValues[i].Equals(numStr, StringComparison.OrdinalIgnoreCase) ||
					EnumNames[i].Equals(numStr, StringComparison.OrdinalIgnoreCase))
				{
					nextId = i;
					break;
				}
			}
		}

		Value = DisplayValues[nextId];
		return true;
	}
}
