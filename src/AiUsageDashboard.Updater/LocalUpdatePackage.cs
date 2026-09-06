using System.Text;
using System.Text.RegularExpressions;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record LocalUpdatePackage(
	string PackagePath,
	UpdateManifest Manifest)
{
	private const int MaximumChecksumFileSizeBytes = 1024;
	private static readonly Regex PackageFileNamePattern = new(
		@"\AAiUsageDashboard-(?<version>\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?)-win-x64\.zip\z",
		RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));
	private static readonly Regex SidecarPattern = new(
		@"\A(?<hash>[0-9a-f]{64})  (?<fileName>[^\\/\r\n]+)\z",
		RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	internal static async Task<LocalUpdatePackage> ReadAsync(
		string packagePath,
		string sha256Path,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(sha256Path);
		string resolvedPackagePath = Path.GetFullPath(packagePath);
		string resolvedSha256Path = Path.GetFullPath(sha256Path);
		string expectedSha256Path = resolvedPackagePath + ".sha256";

		if (!string.Equals(
				resolvedSha256Path,
				expectedSha256Path,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"Update package checksum must be the adjacent canonical sidecar.");
		}

		FileInfo packageFile = new(resolvedPackagePath);

		if (!packageFile.Exists)
		{
			throw new FileNotFoundException(
				"Update package was not found.",
				resolvedPackagePath);
		}

		byte[] sidecarBytes = await ReadSidecarBytesAsync(
			resolvedSha256Path,
			cancellationToken);
		if (sidecarBytes.Any(value => value > 0x7f))
		{
			throw new InvalidDataException(
				"Update package checksum must contain ASCII text only.");
		}

		string sidecar = Encoding.ASCII.GetString(sidecarBytes);
		Match sidecarMatch = SidecarPattern.Match(sidecar);
		string packageFileName = Path.GetFileName(resolvedPackagePath);

		if (!sidecarMatch.Success ||
			!string.Equals(
				sidecarMatch.Groups["fileName"].Value,
				packageFileName,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update package checksum does not match the canonical sidecar format.");
		}

		Match packageNameMatch = PackageFileNamePattern.Match(packageFileName);
		if (!packageNameMatch.Success)
		{
			throw new InvalidDataException(
				"Update package filename is not canonical.");
		}

		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			packageNameMatch.Groups["version"].Value,
			UpdateManifest.ExpectedRuntimeIdentifier,
			packageFile.Length,
			sidecarMatch.Groups["hash"].Value);
		manifest.Validate();
		return new LocalUpdatePackage(resolvedPackagePath, manifest);
	}

	private static async Task<byte[]> ReadSidecarBytesAsync(
		string sidecarPath,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(sidecarPath))
		{
			throw new FileNotFoundException(
				"Update package checksum was not found.",
				sidecarPath);
		}

		UpdateTransaction.ThrowIfNotOrdinaryFile(sidecarPath, mustExist: true);
		await using FileStream sidecarStream = new(
			sidecarPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		UpdateTransaction.ThrowIfNotOrdinaryFile(sidecarPath, mustExist: true);

		if ((sidecarStream.Length <= 0) ||
			(sidecarStream.Length > MaximumChecksumFileSizeBytes))
		{
			throw new InvalidDataException(
				"Update package checksum has an invalid size.");
		}

		byte[] sidecarBytes = new byte[checked((int)sidecarStream.Length)];
		await sidecarStream.ReadExactlyAsync(sidecarBytes, cancellationToken);
		UpdateTransaction.ThrowIfNotOrdinaryFile(sidecarPath, mustExist: true);
		return sidecarBytes;
	}
}
