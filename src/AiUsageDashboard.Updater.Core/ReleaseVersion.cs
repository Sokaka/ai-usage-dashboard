namespace AiUsageDashboard.Updater.Core;

internal sealed class ReleaseVersion :
	IComparable,
	IComparable<ReleaseVersion>,
	IEquatable<ReleaseVersion>
{
	private readonly string _major;
	private readonly string _minor;
	private readonly string _patch;
	private readonly string[] _preReleaseIdentifiers;
	private readonly string _value;

	private ReleaseVersion(
		string value,
		string major,
		string minor,
		string patch,
		string[] preReleaseIdentifiers)
	{
		_value = value;
		_major = major;
		_minor = minor;
		_patch = patch;
		_preReleaseIdentifiers = preReleaseIdentifiers;
	}

	public static ReleaseVersion Parse(string value)
	{
		ArgumentNullException.ThrowIfNull(value);

		if (!TryParse(value, out ReleaseVersion version))
		{
			throw new FormatException("Release version is not valid SemVer.");
		}

		return version;
	}

	public static bool TryParse(string? value, out ReleaseVersion version)
	{
		version = null!;

		if (string.IsNullOrEmpty(value) || value.Contains('+'))
		{
			return false;
		}

		int preReleaseSeparator = value.IndexOf('-');
		string coreVersion = preReleaseSeparator >= 0
			? value[..preReleaseSeparator]
			: value;
		string[] coreIdentifiers = coreVersion.Split('.');

		if ((coreIdentifiers.Length != 3) ||
			!IsValidCoreIdentifier(coreIdentifiers[0]) ||
			!IsValidCoreIdentifier(coreIdentifiers[1]) ||
			!IsValidCoreIdentifier(coreIdentifiers[2]))
		{
			return false;
		}

		string[] preReleaseIdentifiers = [];

		if (preReleaseSeparator >= 0)
		{
			string preRelease = value[(preReleaseSeparator + 1)..];
			preReleaseIdentifiers = preRelease.Split('.');

			if ((preReleaseIdentifiers.Length == 0) ||
				preReleaseIdentifiers.Any(identifier =>
					!IsValidPreReleaseIdentifier(identifier)))
			{
				return false;
			}
		}

		version = new ReleaseVersion(
			value,
			coreIdentifiers[0],
			coreIdentifiers[1],
			coreIdentifiers[2],
			preReleaseIdentifiers);
		return true;
	}

	public int CompareTo(object? obj)
	{
		if (obj is null)
		{
			return 1;
		}

		if (obj is not ReleaseVersion other)
		{
			throw new ArgumentException(
				"Object must be a release version.",
				nameof(obj));
		}

		return CompareTo(other);
	}

	public int CompareTo(ReleaseVersion? other)
	{
		if (other is null)
		{
			return 1;
		}

		int comparison = CompareNumericIdentifier(_major, other._major);

		if (comparison != 0)
		{
			return comparison;
		}

		comparison = CompareNumericIdentifier(_minor, other._minor);

		if (comparison != 0)
		{
			return comparison;
		}

		comparison = CompareNumericIdentifier(_patch, other._patch);

		if (comparison != 0)
		{
			return comparison;
		}

		bool hasPreRelease = _preReleaseIdentifiers.Length > 0;
		bool otherHasPreRelease = other._preReleaseIdentifiers.Length > 0;

		if (!hasPreRelease || !otherHasPreRelease)
		{
			return hasPreRelease == otherHasPreRelease
				? 0
				: hasPreRelease ? -1 : 1;
		}

		int sharedIdentifierCount = Math.Min(
			_preReleaseIdentifiers.Length,
			other._preReleaseIdentifiers.Length);

		for (int index = 0; index < sharedIdentifierCount; index++)
		{
			comparison = ComparePreReleaseIdentifier(
				_preReleaseIdentifiers[index],
				other._preReleaseIdentifiers[index]);

			if (comparison != 0)
			{
				return comparison;
			}
		}

		return _preReleaseIdentifiers.Length.CompareTo(
			other._preReleaseIdentifiers.Length);
	}

	public bool Equals(ReleaseVersion? other)
	{
		return (other is not null) &&
			string.Equals(_value, other._value, StringComparison.Ordinal);
	}

	public override bool Equals(object? obj)
	{
		return Equals(obj as ReleaseVersion);
	}

	public override int GetHashCode()
	{
		return StringComparer.Ordinal.GetHashCode(_value);
	}

	public override string ToString()
	{
		return _value;
	}

	private static int CompareNumericIdentifier(string left, string right)
	{
		int lengthComparison = left.Length.CompareTo(right.Length);

		return lengthComparison != 0
			? lengthComparison
			: string.Compare(left, right, StringComparison.Ordinal);
	}

	private static int ComparePreReleaseIdentifier(string left, string right)
	{
		bool isLeftNumeric = left.All(IsAsciiDigit);
		bool isRightNumeric = right.All(IsAsciiDigit);

		if (isLeftNumeric && isRightNumeric)
		{
			return CompareNumericIdentifier(left, right);
		}

		if (isLeftNumeric != isRightNumeric)
		{
			return isLeftNumeric ? -1 : 1;
		}

		return string.Compare(left, right, StringComparison.Ordinal);
	}

	private static bool IsValidCoreIdentifier(string identifier)
	{
		return (identifier.Length > 0) &&
			((identifier.Length == 1) || (identifier[0] != '0')) &&
			identifier.All(IsAsciiDigit);
	}

	private static bool IsValidPreReleaseIdentifier(string identifier)
	{
		if ((identifier.Length == 0) ||
			!identifier.All(character =>
				IsAsciiDigit(character) ||
				(character >= 'A' && character <= 'Z') ||
				(character >= 'a' && character <= 'z') ||
				(character == '-')))
		{
			return false;
		}

		return !identifier.All(IsAsciiDigit) ||
			(identifier.Length == 1) ||
			(identifier[0] != '0');
	}

	private static bool IsAsciiDigit(char character)
	{
		return (character >= '0') && (character <= '9');
	}
}
