using System.Diagnostics;

namespace AiUsageDashboard.App.Providers;

internal interface ICodexAppServerTransport : IAsyncDisposable
{
	ProcessStartInfo StartInfo { get; }

	void Abort();

	ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

	ValueTask WriteLineAsync(string message, CancellationToken cancellationToken);
}
