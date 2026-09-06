namespace AiUsageDashboard.Core.Persistence;

public sealed class AccountProfileStoreException : Exception
{
	public bool HasCommittedChanges { get; }

	public AccountProfileStoreException(
		string message,
		Exception? innerException = null,
		bool hasCommittedChanges = false)
		: base(message, innerException)
	{
		HasCommittedChanges = hasCommittedChanges;
	}
}
