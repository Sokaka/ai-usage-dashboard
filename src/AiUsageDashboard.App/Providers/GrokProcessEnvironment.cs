using System.Diagnostics;
using System.IO;

namespace AiUsageDashboard.App.Providers;

internal static class GrokProcessEnvironment
{
	internal static void Apply(
		ProcessStartInfo startInfo,
		string homeDirectory)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

		string fullHomeDirectory = Path.GetFullPath(homeDirectory);
		string fullExecutablePath = Path.GetFullPath(startInfo.FileName);
		string? executableDirectory = Path.GetDirectoryName(fullExecutablePath);
		string systemDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.System);
		string windowsDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.Windows);
		string commandInterpreter = Path.Combine(systemDirectory, "cmd.exe");

		if (!Path.IsPathFullyQualified(homeDirectory) ||
			!Path.IsPathFullyQualified(startInfo.FileName) ||
			string.IsNullOrWhiteSpace(executableDirectory) ||
			executableDirectory.Contains(Path.PathSeparator) ||
			string.IsNullOrWhiteSpace(systemDirectory) ||
			!Path.IsPathFullyQualified(systemDirectory) ||
			systemDirectory.Contains(Path.PathSeparator) ||
			string.IsNullOrWhiteSpace(windowsDirectory) ||
			!Path.IsPathFullyQualified(windowsDirectory) ||
			!File.Exists(commandInterpreter))
		{
			throw new ArgumentException(
				"Grok process environment requires absolute local paths.");
		}

		string temporaryDirectory = Path.Combine(fullHomeDirectory, "tmp");
		startInfo.Environment.Clear();
		startInfo.Environment["APPDATA"] = fullHomeDirectory;
		startInfo.Environment["COMSPEC"] = commandInterpreter;
		startInfo.Environment["GROK_HOME"] = fullHomeDirectory;
		startInfo.Environment["HOME"] = fullHomeDirectory;
		startInfo.Environment["LOCALAPPDATA"] = fullHomeDirectory;
		startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
		startInfo.Environment["TEMP"] = temporaryDirectory;
		startInfo.Environment["TMP"] = temporaryDirectory;
		startInfo.Environment["USERPROFILE"] = fullHomeDirectory;
		startInfo.Environment["SystemRoot"] = windowsDirectory;
		startInfo.Environment["WINDIR"] = windowsDirectory;
		startInfo.Environment["PATH"] = string.Join(
			Path.PathSeparator,
			new[] { executableDirectory, systemDirectory }
				.Distinct(StringComparer.OrdinalIgnoreCase));
	}
}
