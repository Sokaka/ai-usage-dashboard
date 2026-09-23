namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityAccountMetadataValidator
{
	private const int MaximumPlanTierLength = 64;

	internal static bool TryNormalizePlanTier(
		string? planTier,
		out string? normalizedPlanTier)
	{
		normalizedPlanTier = null;

		if (planTier is null)
		{
			return true;
		}

		if (string.IsNullOrWhiteSpace(planTier) ||
			(planTier.Length > MaximumPlanTierLength) ||
			!string.Equals(planTier, planTier.Trim(), StringComparison.Ordinal) ||
			planTier.Any(char.IsControl))
		{
			return false;
		}

		normalizedPlanTier = planTier;
		return true;
	}

	internal static bool IsValidAsciiEmail(string? email)
	{
		if (string.IsNullOrEmpty(email) || (email.Length > 320))
		{
			return false;
		}

		foreach (char character in email)
		{
			if ((character < '!') || (character > '~'))
			{
				return false;
			}
		}

		int atIndex = email.IndexOf('@');
		if ((atIndex <= 0) ||
			(atIndex != email.LastIndexOf('@')) ||
			(atIndex > 64) ||
			(atIndex == email.Length - 1))
		{
			return false;
		}

		ReadOnlySpan<char> local = email.AsSpan(0, atIndex);
		ReadOnlySpan<char> domain = email.AsSpan(atIndex + 1);
		const string localPunctuation = ".!#$%&'*+-/=?^_`{|}~";

		if ((local[0] == '.') || (local[^1] == '.') ||
			local.Contains("..", StringComparison.Ordinal) ||
			(domain.Length > 253) ||
			(domain[0] == '.') || (domain[^1] == '.') ||
			domain.Contains("..", StringComparison.Ordinal))
		{
			return false;
		}

		foreach (char character in local)
		{
			if (!char.IsAsciiLetterOrDigit(character) &&
				!localPunctuation.Contains(character, StringComparison.Ordinal))
			{
				return false;
			}
		}

		int labelStart = 0;
		for (int index = 0; index <= domain.Length; index++)
		{
			if ((index < domain.Length) && (domain[index] != '.'))
			{
				continue;
			}

			ReadOnlySpan<char> label = domain[labelStart..index];
			if ((label.Length == 0) || (label.Length > 63) ||
				(label[0] == '-') || (label[^1] == '-'))
			{
				return false;
			}

			foreach (char character in label)
			{
				if (!char.IsAsciiLetterOrDigit(character) &&
					(character != '-'))
				{
					return false;
				}
			}

			labelStart = index + 1;
		}

		return true;
	}
}
