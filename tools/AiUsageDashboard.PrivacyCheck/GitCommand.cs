using System.Diagnostics;
using System.Text;

namespace AiUsageDashboard.PrivacyCheck;

internal sealed record GitRequest(
	string Operation,
	IReadOnlyList<string> Arguments,
	int MaximumOutputBytes,
	string? StandardInput = null);

internal static class GitCommand
{
	private const int MaximumErrorBytes = 16 * 1024;
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

	internal static async Task<byte[]> RunAsync(
		string repositoryPath,
		GitRequest request,
		CancellationToken cancellationToken)
	{
		ProcessStartInfo startInfo = new("git")
		{
			WorkingDirectory = repositoryPath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		ConfigureEnvironment(startInfo.Environment);
		foreach (string argument in request.Arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(startInfo) ??
			throw new PrivacyCheckException($"Could not start Git operation '{request.Operation}'.");
		using CancellationTokenSource commandCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		commandCancellation.CancelAfter(CommandTimeout);
		Task<byte[]> outputTask = ReadBoundedAsync(
			process.StandardOutput.BaseStream, request.MaximumOutputBytes, commandCancellation);
		Task<byte[]> errorTask = ReadBoundedAsync(
			process.StandardError.BaseStream, MaximumErrorBytes, commandCancellation);
		Task inputTask = WriteInputAsync(process, request.StandardInput, commandCancellation);
		Task exitTask = process.WaitForExitAsync(commandCancellation.Token);
		try
		{
			await Task.WhenAll(outputTask, errorTask, inputTask, exitTask).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
				}
			}
			catch (Exception cleanupException)
			{
				throw new PrivacyCheckException(
					$"Git operation '{request.Operation}' failed and its process could not be stopped.",
					new AggregateException(exception, cleanupException));
			}
			throw new PrivacyCheckException(
				$"Git operation '{request.Operation}' failed or exceeded its time/output limit.", exception);
		}

		if (process.ExitCode != 0)
		{
			throw new PrivacyCheckException(
				$"Git operation '{request.Operation}' exited with code {process.ExitCode}.",
				new InvalidOperationException(Encoding.UTF8.GetString(await errorTask.ConfigureAwait(false))));
		}
		return await outputTask.ConfigureAwait(false);
	}

	internal static void ConfigureEnvironment(IDictionary<string, string?> environment)
	{
		foreach (string name in new[]
		{
			"GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_NAMESPACE", "GIT_INDEX_FILE",
			"GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_SHALLOW_FILE",
			"GIT_REPLACE_REF_BASE", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS"
		})
		{
			environment.Remove(name);
		}
		environment["GIT_OPTIONAL_LOCKS"] = "0";
		environment["GIT_NO_REPLACE_OBJECTS"] = "1";
		environment["GIT_NO_LAZY_FETCH"] = "1";
	}

	private static async Task<byte[]> ReadBoundedAsync(
		Stream stream,
		int maximumBytes,
		CancellationTokenSource cancellation)
	{
		using MemoryStream output = new();
		byte[] buffer = new byte[81920];
		try
		{
			int read;
			while ((read = await stream.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) > 0)
			{
				if ((output.Length + read) > maximumBytes)
				{
					throw new PrivacyCheckException($"Git output exceeded {maximumBytes} bytes.");
				}
				output.Write(buffer, 0, read);
			}
			return output.ToArray();
		}
		catch
		{
			await cancellation.CancelAsync().ConfigureAwait(false);
			throw;
		}
	}

	private static async Task WriteInputAsync(
		Process process,
		string? input,
		CancellationTokenSource cancellation)
	{
		try
		{
			if (input is not null)
			{
				await process.StandardInput.WriteAsync(input.AsMemory(), cancellation.Token).ConfigureAwait(false);
				await process.StandardInput.FlushAsync(cancellation.Token).ConfigureAwait(false);
			}
		}
		catch
		{
			await cancellation.CancelAsync().ConfigureAwait(false);
			throw;
		}
		finally
		{
			process.StandardInput.Close();
		}
	}
}
