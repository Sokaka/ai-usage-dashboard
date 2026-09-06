using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterExecutionSafetyTests
{
	[Fact]
	public void EnsureNotElevated_WhenTokenIsNotElevated_AllowsExecution()
	{
		UpdateExecutionSafety.EnsureNotElevated(isElevated: false);
	}

	[Fact]
	public void EnsureNotElevated_WhenTokenIsElevated_RejectsExecution()
	{
		Assert.Throws<InvalidOperationException>(() =>
			UpdateExecutionSafety.EnsureNotElevated(isElevated: true));
	}
}
