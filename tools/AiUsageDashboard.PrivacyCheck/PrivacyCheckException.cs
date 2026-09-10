namespace AiUsageDashboard.PrivacyCheck;

internal sealed class PrivacyCheckException : Exception
{
	internal PrivacyCheckException(string message, Exception? innerException = null)
		: base(message, innerException)
	{
	}
}
