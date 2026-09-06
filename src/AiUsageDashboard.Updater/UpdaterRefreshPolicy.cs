using System.Diagnostics;
using System.Security.Cryptography;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal enum UpdaterRefreshAction
{
	RunCurrent,
	DelegateToDownloadedUpdater
}

internal sealed record CurrentUpdaterIdentity(
	string ExecutablePath,
	string Version,
	long SizeBytes,
	string Sha256)
{
	internal static async Task<CurrentUpdaterIdentity> ReadAsync(
		string executablePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		string fullPath = Path.GetFullPath(executablePath);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);
		FileInfo executable = new(fullPath);
		string productVersion = FileVersionInfo.GetVersionInfo(fullPath)
			.ProductVersion ?? throw new InvalidDataException(
				"The running updater does not have a ProductVersion.");
		int revisionSeparatorIndex = productVersion.IndexOf(
			'+',
			StringComparison.Ordinal);
		string version = revisionSeparatorIndex < 0
			? productVersion
			: productVersion[..revisionSeparatorIndex];
		_ = ReleaseVersion.Parse(version);

		await using FileStream stream = new(
			fullPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
		UpdateTransaction.ThrowIfNotOrdinaryFile(fullPath, mustExist: true);

		return new CurrentUpdaterIdentity(
			fullPath,
			version,
			executable.Length,
			Convert.ToHexString(hash).ToLowerInvariant());
	}
}

internal static class UpdaterRefreshPolicy
{
	internal static void EnsureMatchesSignedInstaller(
		CurrentUpdaterIdentity current,
		UpdateReleaseArtifact available)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(available);
		if ((current.Version != available.Version) ||
			(current.SizeBytes != available.SizeBytes) ||
			!string.Equals(current.Sha256, available.Sha256, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				$"Running updater {current.Version} does not match signed installer {available.Version}. " +
				"Download the matching updater from the official release before installing its App package and terms.");
		}
	}

	internal static UpdaterRefreshAction Evaluate(
		CurrentUpdaterIdentity current,
		UpdateReleaseArtifact available)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(available);
		int versionComparison = ReleaseVersion.Parse(available.Version)
			.CompareTo(ReleaseVersion.Parse(current.Version));

		if (versionComparison > 0)
		{
			return UpdaterRefreshAction.DelegateToDownloadedUpdater;
		}

		if (versionComparison < 0)
		{
			return UpdaterRefreshAction.RunCurrent;
		}

		if ((current.SizeBytes != available.SizeBytes) ||
			!string.Equals(
				current.Sha256,
				available.Sha256,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The update feed reuses the current updater version with different bytes.");
		}

		return UpdaterRefreshAction.RunCurrent;
	}
}
