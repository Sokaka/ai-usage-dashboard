using System.Diagnostics;

namespace AiUsageDashboard.Tests;

internal static class JunctionTestHelper
{
	internal static async Task CreateAsync(
		string junctionPath,
		string targetPath)
	{
		ProcessStartInfo startInfo = new(
			Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
		{
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("mklink");
		startInfo.ArgumentList.Add("/J");
		startInfo.ArgumentList.Add(junctionPath);
		startInfo.ArgumentList.Add(targetPath);
		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"The junction creation process did not start.");
		await process.WaitForExitAsync();
		string standardError = await process.StandardError.ReadToEndAsync();
		string standardOutput = await process.StandardOutput.ReadToEndAsync();
		Assert.True(
			process.ExitCode == 0,
			$"mklink failed: {standardOutput} {standardError}");
	}

	internal static void Delete(string junctionPath)
	{
		if (Directory.Exists(junctionPath))
		{
			Directory.Delete(junctionPath, recursive: false);
		}
	}
}
