namespace AiUsageDashboard.Updater.Core;

public sealed class UpdateInstallationSwitcher
{
	private readonly Action<string, string> _moveDirectory;

	public UpdateInstallationSwitcher()
		: this(Directory.Move)
	{
	}

	internal UpdateInstallationSwitcher(Action<string, string> moveDirectory)
	{
		_moveDirectory = moveDirectory ??
			throw new ArgumentNullException(nameof(moveDirectory));
	}

	public void Switch(UpdateTransaction transaction)
	{
		ArgumentNullException.ThrowIfNull(transaction);

		if (transaction.State != UpdateTransactionState.AppExited)
		{
			throw new InvalidOperationException(
				"Installation can switch only after the application has exited.");
		}

		ValidateLayout(transaction);
		bool hadCurrentInstallation = Directory.Exists(
			transaction.CurrentDirectoryPath);
		transaction.BeginSwitch();

		try
		{
			if (hadCurrentInstallation)
			{
				_moveDirectory(
					transaction.CurrentDirectoryPath,
					transaction.PreviousDirectoryPath);
				transaction.ValidateOwnedDirectoryChain();
			}

			_moveDirectory(
				transaction.StagingDirectoryPath,
				transaction.CurrentDirectoryPath);
			transaction.ValidateOwnedDirectoryChain();
			transaction.TransitionTo(UpdateTransactionState.Committed);
		}
		catch (Exception switchException)
		{
			try
			{
				new UpdateTransactionRecovery(_moveDirectory).RestoreLayout(transaction);
				transaction.TransitionTo(UpdateTransactionState.RolledBack);
			}
			catch (Exception rollbackException)
			{
				Exception failure = new AggregateException(switchException, rollbackException);
				try
				{
					transaction.TransitionTo(UpdateTransactionState.Failed);
				}
				catch (Exception receiptException) when (receiptException is IOException or UnauthorizedAccessException)
				{
					failure = new AggregateException(failure, receiptException);
				}

				throw new UpdateInstallationSwitchException(
					$"Installation switch and rollback both failed; recovery receipt retained at {transaction.StateFilePath}.",
					failure);
			}

			throw new UpdateInstallationSwitchException(
				"Installation switch failed and the previous installation was restored.",
				switchException);
		}
	}

	private static void ValidateLayout(UpdateTransaction transaction)
	{
		transaction.ValidateOwnedDirectoryChain();

		if (!Directory.Exists(transaction.StagingDirectoryPath))
		{
			throw new DirectoryNotFoundException(
				"Staged installation directory was not found.");
		}

		if (Directory.Exists(transaction.PreviousDirectoryPath) ||
			File.Exists(transaction.PreviousDirectoryPath))
		{
			throw new IOException(
				"Previous installation destination already exists.");
		}

		string? installVolume = Path.GetPathRoot(transaction.InstallRootPath);
		string? currentVolume = Path.GetPathRoot(transaction.CurrentDirectoryPath);
		string? stagingVolume = Path.GetPathRoot(transaction.StagingDirectoryPath);
		string? previousVolume = Path.GetPathRoot(transaction.PreviousDirectoryPath);

		if (!string.Equals(installVolume, currentVolume, StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(installVolume, stagingVolume, StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(installVolume, previousVolume, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				"Current, staging, and previous directories must share one volume.");
		}
	}

}

public sealed class UpdateInstallationSwitchException : IOException
{
	public UpdateInstallationSwitchException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
