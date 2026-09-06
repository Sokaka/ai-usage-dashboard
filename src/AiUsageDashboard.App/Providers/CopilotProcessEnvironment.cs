using System.Collections;
using System.Diagnostics;
using System.IO;

namespace AiUsageDashboard.App.Providers;

internal static class CopilotProcessEnvironment
{
	private static readonly string[] InheritedNetworkVariableAllowlist =
	{
		"ALL_PROXY",
		"HTTP_PROXY",
		"HTTPS_PROXY",
		"NODE_EXTRA_CA_CERTS",
		"NO_PROXY",
		"SSL_CERT_DIR",
		"SSL_CERT_FILE"
	};

	internal static IReadOnlyDictionary<string, string>
		CreateSanitizedEnvironment(string homeDirectory)
	{
		Dictionary<string, string?> inheritedEnvironment = new(
			StringComparer.OrdinalIgnoreCase);

		foreach (DictionaryEntry variable in
			Environment.GetEnvironmentVariables())
		{
			if ((variable.Key is not string name) ||
				(variable.Value is not string value))
			{
				continue;
			}

			inheritedEnvironment[name] = value;
		}

		return CreateSanitizedEnvironment(
			inheritedEnvironment,
			homeDirectory);
	}

	internal static IReadOnlyDictionary<string, string>
		CreateSanitizedEnvironment(
			IReadOnlyDictionary<string, string?> inheritedEnvironment,
			string homeDirectory)
	{
		ArgumentNullException.ThrowIfNull(inheritedEnvironment);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
		string normalizedHomeDirectory = Path.GetFullPath(homeDirectory);
		string windowsDirectory = Environment.GetFolderPath(
			Environment.SpecialFolder.Windows);
		string systemDirectory = Environment.SystemDirectory;
		string commandInterpreter = Path.Combine(systemDirectory, "cmd.exe");
		string? systemDrive = Path.GetPathRoot(windowsDirectory)?.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);

		if (string.IsNullOrWhiteSpace(windowsDirectory) ||
			!Path.IsPathFullyQualified(windowsDirectory) ||
			windowsDirectory.Contains(Path.PathSeparator) ||
			string.IsNullOrWhiteSpace(systemDirectory) ||
			!Path.IsPathFullyQualified(systemDirectory) ||
			systemDirectory.Contains(Path.PathSeparator) ||
			string.IsNullOrWhiteSpace(systemDrive) ||
			!File.Exists(commandInterpreter))
		{
			throw new InvalidOperationException(
				"The trusted Windows process environment is unavailable.");
		}

		Dictionary<string, string> environment = new(
			StringComparer.OrdinalIgnoreCase);

		foreach (string name in InheritedNetworkVariableAllowlist)
		{
			if (!inheritedEnvironment.TryGetValue(name, out string? value) ||
				string.IsNullOrEmpty(value))
			{
				continue;
			}

			environment[name] = value;
		}

		environment["APPDATA"] = normalizedHomeDirectory;
		environment["COMSPEC"] = commandInterpreter;
		environment["COPILOT_HOME"] = normalizedHomeDirectory;
		environment["HOME"] = normalizedHomeDirectory;
		environment["LOCALAPPDATA"] = normalizedHomeDirectory;
		environment["OS"] = "Windows_NT";
		environment["PATH"] = string.Join(
			Path.PathSeparator,
			new[] { systemDirectory, windowsDirectory }
				.Distinct(StringComparer.OrdinalIgnoreCase));
		environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
		environment["SystemDrive"] = systemDrive;
		environment["SystemRoot"] = windowsDirectory;
		environment["TEMP"] = normalizedHomeDirectory;
		environment["TMP"] = normalizedHomeDirectory;
		environment["USERPROFILE"] = normalizedHomeDirectory;
		environment["WINDIR"] = windowsDirectory;

		return environment;
	}

	internal static void Apply(
		ProcessStartInfo startInfo,
		string homeDirectory)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
		IReadOnlyDictionary<string, string> environment =
			CreateSanitizedEnvironment(homeDirectory);
		startInfo.Environment.Clear();

		foreach ((string name, string value) in environment)
		{
			startInfo.Environment[name] = value;
		}
	}
}
