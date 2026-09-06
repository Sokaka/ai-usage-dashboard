namespace AiUsageDashboard.Licensing;

public static class LegalCommandLine
{
	public const int FailureExitCode = 4;
	public const int AcceptanceRequiredExitCode = 5;

	public static bool IsLegalCommand(IReadOnlyList<string> arguments) =>
		(arguments.Count > 0) && ((arguments[0] == "--licenses") ||
			(arguments[0] == "--license-digest") ||
			(arguments[0] == "--export-licenses") ||
			(arguments[0] == "--accept-licenses"));

	public static bool TryHandle(
		IReadOnlyList<string> arguments,
		Func<LegalCatalog> createCatalog,
		Func<LegalAcceptanceStore> createStore,
		out int exitCode,
		TextWriter? output = null,
		TextWriter? error = null)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(createCatalog);
		ArgumentNullException.ThrowIfNull(createStore);
		exitCode = 0;
		if (!IsLegalCommand(arguments))
		{
			return false;
		}

		try
		{
			return TryHandle(arguments, createCatalog(), createStore, out exitCode, output, error);
		}
		catch (Exception exception) when (IsCommandFailure(exception))
		{
			(error ?? Console.Error).WriteLine($"License command failed: {exception.Message}");
			exitCode = FailureExitCode;
			return true;
		}
	}

	public static bool TryHandle(
		IReadOnlyList<string> arguments,
		LegalCatalog catalog,
		LegalAcceptanceStore store,
		out int exitCode,
		TextWriter? output = null,
		TextWriter? error = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		return TryHandle(arguments, catalog, () => store, out exitCode, output, error);
	}

	public static bool TryHandle(
		IReadOnlyList<string> arguments,
		LegalCatalog catalog,
		Func<LegalAcceptanceStore> createStore,
		out int exitCode,
		TextWriter? output = null,
		TextWriter? error = null)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(createStore);
		exitCode = 0;
		if (!IsLegalCommand(arguments))
		{
			return false;
		}
		output ??= Console.Out;
		error ??= Console.Error;
		try
		{
			switch (arguments[0])
			{
				case "--licenses" when arguments.Count == 1:
					output.Write(catalog.GetReadableText());
					break;
				case "--license-digest" when arguments.Count == 1:
					output.WriteLine(catalog.Digest);
					break;
				case "--export-licenses" when arguments.Count == 2:
					catalog.Export(arguments[1]);
					output.WriteLine($"Licenses exported to '{Path.GetFullPath(arguments[1])}'. Acceptance digest: {catalog.Digest}");
					break;
				case "--accept-licenses" when arguments.Count == 2:
					createStore().Accept(catalog, arguments[1]);
					output.WriteLine($"Accepted license scope {catalog.Digest} for the current Windows user.");
					break;
				default:
					throw new ArgumentException("Expected --licenses, --license-digest, --export-licenses <empty-directory>, or --accept-licenses <displayed-digest>.");
			}
		}
		catch (Exception exception) when (IsCommandFailure(exception))
		{
			error.WriteLine($"License command failed: {exception.Message}");
			exitCode = FailureExitCode;
		}
		return true;
	}

	private static bool IsCommandFailure(Exception exception) =>
		(exception is IOException) || (exception is InvalidDataException) ||
		(exception is UnauthorizedAccessException) || (exception is InvalidOperationException) ||
		(exception is ArgumentException) || (exception is TimeoutException);
}
