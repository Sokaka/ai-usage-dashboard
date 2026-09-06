namespace AiUsageDashboard.Tests;

public sealed class TemporaryDirectoryTests
{
	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task Dispose_WhenFileIsBrieflyLocked_RetriesCleanup()
	{
		TaskCompletionSource retryObserved = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TemporaryDirectory temporaryDirectory = new(
			() => retryObserved.TrySetResult());
		string directoryPath = temporaryDirectory.Path;
		string lockedFilePath = Path.Combine(directoryPath, "locked.bin");
		File.WriteAllText(lockedFilePath, "fixture");
		FileStream lockedFile = new(
			lockedFilePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None);
		Task dispose = Task.Run(() =>
		{
			temporaryDirectory.Dispose();
		});

		try
		{
			await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(dispose.IsCompleted);

			lockedFile.Dispose();
			await dispose.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.False(Directory.Exists(directoryPath));
		}
		finally
		{
			lockedFile.Dispose();
			await dispose.WaitAsync(TimeSpan.FromSeconds(6));

			if (Directory.Exists(directoryPath))
			{
				temporaryDirectory.Dispose();
			}
		}
	}
}
