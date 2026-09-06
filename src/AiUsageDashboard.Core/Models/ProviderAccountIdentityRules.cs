namespace AiUsageDashboard.Core.Models;

public static class ProviderAccountIdentityRules
{
	public const int MaximumLength = 320;

	public static StringComparer Comparer { get; } =
		StringComparer.OrdinalIgnoreCase;

	public static bool TryNormalize(
		string? value,
		out string? normalizedIdentity)
	{
		normalizedIdentity = null;

		if (value is null)
		{
			return true;
		}

		string identity = value.Trim();

		if ((identity.Length == 0) ||
			(identity.Length > MaximumLength) ||
			identity.Any(char.IsControl))
		{
			return false;
		}

		normalizedIdentity = identity;
		return true;
	}
}
