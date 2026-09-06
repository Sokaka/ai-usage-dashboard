using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.App.Infrastructure;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Persistence;

namespace AiUsageDashboard.App.Persistence;

public sealed class JsonUsageSnapshotStore : IUsageSnapshotStore
{
	private enum CacheAccessAction
	{
		Load,
		Save
	}

	private readonly record struct CacheDiagnosticKey(
		Guid AccountId,
		ProviderKind Provider,
		CacheAccessAction Action);

	private sealed record MetricDocument(
		string? Key,
		string? Label,
		double? UsedPercent,
		string? DisplayValue,
		DateTimeOffset? ResetsAt,
		string? ResetDisplayValue);

	private sealed record SnapshotDocument(
		int SchemaVersion,
		Guid? AccountId,
		ProviderKind? Provider,
		SourceTrust? SourceTrust,
		DateTimeOffset? FetchedAt,
		DateTimeOffset? ObservedAt,
		DateTimeOffset? StaleAfter,
		string? ProviderAccountIdentity,
		string? ProviderAccountDisplayIdentity,
		string? SubscriptionScopeDisplayName,
		string? PlanTier,
		SubscriptionVerificationState? SubscriptionVerificationState,
		List<MetricDocument?>? Metrics);

	private const string CacheDiagnosticOperation = "usage-snapshot-cache";
	private const int CurrentSchemaVersion = 3;
	private const int LegacySchemaVersion = 1;
	private const int PreviousSchemaVersion = 2;
	private const int MaximumCacheFileSize = 256 * 1024;
	private const int MaximumDisplayValueLength = 512;
	private const int MaximumIdentityLength = 320;
	private const int MaximumKeyLength = 200;
	private const int MaximumLabelLength = 200;
	private const int MaximumMetricCount = 64;
	private const int MaximumPlanTierLength = 64;
	private const int MaximumSubscriptionScopeDisplayNameLength = 256;
	private const string ManagedPathDescription = "The usage snapshot cache path";
	private static readonly HashSet<string> LegacyMetricPropertyNames = new(
		new[]
		{
			"displayValue",
			"key",
			"label",
			"resetsAt",
			"usedPercent"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> MetricPropertyNames = new(
		LegacyMetricPropertyNames.Append("resetDisplayValue"),
		StringComparer.Ordinal);
	private static readonly HashSet<string> PreviousSnapshotPropertyNames = new(
		new[]
		{
			"accountId",
			"fetchedAt",
			"metrics",
			"observedAt",
			"provider",
			"providerAccountIdentity",
			"schemaVersion",
			"sourceTrust",
			"staleAfter"
		},
		StringComparer.Ordinal);
	private static readonly HashSet<string> SnapshotPropertyNames = new(
		PreviousSnapshotPropertyNames.Concat(new[]
		{
			"planTier",
			"providerAccountDisplayIdentity",
			"subscriptionScopeDisplayName",
			"subscriptionVerificationState"
		}),
		StringComparer.Ordinal);
	private static readonly KeyedAsyncGate<string> FileGates =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, string> PersistedFingerprints =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
	private readonly object _cacheDiagnosticStateLock = new();
	private readonly Func<ProviderKind, Guid, string> _filePathFactory;
	private readonly HashSet<CacheDiagnosticKey>
		_reportedCacheAccessFailures = new();
	private readonly Func<string, string, Exception?, bool> _tryReportDiagnostic;
	private readonly TimeProvider _timeProvider;

	internal static bool IsFileGateCached(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		return FileGates.ContainsKey(Path.GetFullPath(filePath));
	}

	public JsonUsageSnapshotStore(
		Func<ProviderKind, Guid, string> filePathFactory,
		TimeProvider? timeProvider = null)
		: this(filePathFactory, reportDiagnostic: null, timeProvider)
	{
	}

	internal JsonUsageSnapshotStore(
		Func<ProviderKind, Guid, string> filePathFactory,
		Func<string, string, Exception?, bool>? reportDiagnostic,
		TimeProvider? timeProvider = null)
	{
		_filePathFactory = filePathFactory ??
			throw new ArgumentNullException(nameof(filePathFactory));
		_tryReportDiagnostic = reportDiagnostic ?? ((_, _, _) => true);
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<UsageSnapshot?> LoadAsync(
		AccountProfile account,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(account);
		ValidateAccount(account.Id, account.Provider);

		string filePath;

		try
		{
			filePath = GetFilePath(account.Provider, account.Id);
		}
		catch (Exception exception) when (IsCacheAccessFailure(exception))
		{
			ReportCacheAccessFailure(
				account,
				CacheAccessAction.Load,
				"path-resolution",
				exception);
			return null;
		}

		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			if (!File.Exists(filePath))
			{
				PersistedFingerprints.TryRemove(filePath, out _);
				return null;
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			await using FileStream stream = new(
				filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			long fileLength = stream.Length;

			if ((fileLength == 0) || (fileLength > MaximumCacheFileSize))
			{
				PersistedFingerprints.TryRemove(filePath, out _);
				return null;
			}

			byte[] fileContents = GC.AllocateUninitializedArray<byte>((int)fileLength);
			await stream.ReadExactlyAsync(fileContents, cancellationToken)
				.ConfigureAwait(false);

			using JsonDocument parsedDocument = JsonDocument.Parse(fileContents);

			if (!TryReadSchemaVersion(
					parsedDocument.RootElement,
					out int schemaVersion) ||
				((schemaVersion != LegacySchemaVersion) &&
					(schemaVersion != PreviousSchemaVersion) &&
					(schemaVersion != CurrentSchemaVersion)) ||
				!HasExactProperties(
					parsedDocument.RootElement,
					schemaVersion == CurrentSchemaVersion
						? SnapshotPropertyNames
						: PreviousSnapshotPropertyNames) ||
				!HasValidMetricShapes(parsedDocument.RootElement, schemaVersion))
			{
				PersistedFingerprints.TryRemove(filePath, out _);
				return null;
			}

			SnapshotDocument? document =
				JsonSerializer.Deserialize<SnapshotDocument>(
					fileContents,
					SerializerOptions);

			if (document is null)
			{
				PersistedFingerprints.TryRemove(filePath, out _);
				return null;
			}

			UsageSnapshot? snapshot = CreateSnapshot(
				account,
				document,
				_timeProvider.GetUtcNow());

			if (snapshot is null)
			{
				PersistedFingerprints.TryRemove(filePath, out _);
				return null;
			}

			PersistedFingerprints[filePath] = CreateFingerprint(fileContents);
			return CompleteCacheLoad(account, snapshot);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (
			(exception is JsonException) ||
			(exception is InvalidDataException) ||
			IsCacheAccessFailure(exception))
		{
			PersistedFingerprints.TryRemove(filePath, out _);
			ReportCacheAccessFailure(
				account,
				CacheAccessAction.Load,
				"read",
				exception);
			return null;
		}
	}

	public async Task SaveAsync(
		UsageSnapshot snapshot,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (((snapshot.Status != SnapshotStatus.Ready) &&
				(snapshot.Status != SnapshotStatus.Stale)) ||
			(snapshot.SourceTrust == SourceTrust.Unavailable))
		{
			return;
		}

		UsageSnapshot currentSnapshot = FilterExpiredMetrics(
			snapshot,
			_timeProvider.GetUtcNow());

		if (currentSnapshot.Metrics.Count == 0)
		{
			try
			{
				await DeleteAsync(
					snapshot.Account.Id,
					snapshot.Account.Provider,
					cancellationToken).ConfigureAwait(false);
				ReportCacheAccessRecovered(
					snapshot.Account,
					CacheAccessAction.Save);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception) when (IsCacheAccessFailure(exception))
			{
				ReportCacheAccessFailure(
					snapshot.Account,
					CacheAccessAction.Save,
					"expired-cleanup",
					exception);
			}

			return;
		}

		SnapshotDocument document = CreateDocument(currentSnapshot);
		byte[] serializedDocument = JsonSerializer.SerializeToUtf8Bytes(
			document,
			SerializerOptions);

		if (serializedDocument.Length > MaximumCacheFileSize)
		{
			return;
		}

		string fingerprint = CreateFingerprint(serializedDocument);
		string filePath;

		try
		{
			filePath = GetFilePath(snapshot.Account.Provider, snapshot.Account.Id);
		}
		catch (Exception exception) when (IsCacheAccessFailure(exception))
		{
			ReportCacheAccessFailure(
				snapshot.Account,
				CacheAccessAction.Save,
				"path-resolution",
				exception);
			return;
		}

		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken).ConfigureAwait(false);

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			if (File.Exists(filePath) &&
				PersistedFingerprints.TryGetValue(
					filePath,
					out string? persistedFingerprint) &&
				string.Equals(
					persistedFingerprint,
					fingerprint,
					StringComparison.Ordinal) &&
				await DoesExistingFileMatchFingerprintAsync(
					filePath,
					fingerprint,
					cancellationToken).ConfigureAwait(false))
			{
				ReportCacheAccessRecovered(
					snapshot.Account,
					CacheAccessAction.Save);
				return;
			}

			await SaveDocumentAsync(
				filePath,
				serializedDocument,
				cancellationToken).ConfigureAwait(false);
			PersistedFingerprints[filePath] = fingerprint;
			ReportCacheAccessRecovered(
				snapshot.Account,
				CacheAccessAction.Save);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (IsCacheAccessFailure(exception))
		{
			ReportCacheAccessFailure(
				snapshot.Account,
				CacheAccessAction.Save,
				"persist",
				exception);
		}
	}

	public async Task DeleteAsync(
		Guid accountId,
		ProviderKind provider,
		CancellationToken cancellationToken = default)
	{
		ValidateAccount(accountId, provider);
		string filePath = GetFilePath(provider, accountId);
		using IDisposable fileLease = await FileGates.EnterAsync(
			filePath,
			cancellationToken,
			evictWhenIdle: true).ConfigureAwait(false);

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			File.Delete(filePath);
		}
		catch (DirectoryNotFoundException)
		{
			// The requested cache is already absent when its parent directory does not exist.
		}
		finally
		{
			PersistedFingerprints.TryRemove(filePath, out _);
		}
	}

	private static UsageSnapshot? CreateSnapshot(
		AccountProfile account,
		SnapshotDocument document,
		DateTimeOffset now)
	{
		if (((document.SchemaVersion != LegacySchemaVersion) &&
				(document.SchemaVersion != PreviousSchemaVersion) &&
				(document.SchemaVersion != CurrentSchemaVersion)) ||
			(document.AccountId != account.Id) ||
			(document.Provider != account.Provider) ||
			(document.SourceTrust is null) ||
			!Enum.IsDefined(typeof(SourceTrust), document.SourceTrust.Value) ||
			(document.SourceTrust == SourceTrust.Unavailable) ||
			(document.FetchedAt is null) ||
			(document.FetchedAt == default) ||
			((document.ObservedAt is not null) &&
				(document.ObservedAt == default)) ||
			((document.StaleAfter is not null) &&
				(document.StaleAfter == default)) ||
			(document.Metrics is null) ||
			(document.Metrics.Count == 0) ||
			(document.Metrics.Count > MaximumMetricCount))
		{
			return null;
		}

		string? providerAccountIdentity = ValidateOptionalText(
			document.ProviderAccountIdentity,
			MaximumIdentityLength);

		if ((document.ProviderAccountIdentity is not null) &&
			(providerAccountIdentity is null))
		{
			return null;
		}

		string? providerAccountDisplayIdentity = null;
		string? subscriptionScopeDisplayName = null;
		string? planTier = null;
		SubscriptionVerificationState subscriptionVerificationState =
			SubscriptionVerificationState.Unverified;

		if (document.SchemaVersion == CurrentSchemaVersion)
		{
			providerAccountDisplayIdentity = ValidateOptionalText(
				document.ProviderAccountDisplayIdentity,
				MaximumIdentityLength);
			subscriptionScopeDisplayName = ValidateOptionalText(
				document.SubscriptionScopeDisplayName,
				MaximumSubscriptionScopeDisplayNameLength);
			planTier = ValidateOptionalText(
				document.PlanTier,
				MaximumPlanTierLength);

			if (((document.ProviderAccountDisplayIdentity is not null) &&
					(providerAccountDisplayIdentity is null)) ||
				((document.SubscriptionScopeDisplayName is not null) &&
					(subscriptionScopeDisplayName is null)) ||
				((document.PlanTier is not null) && (planTier is null)) ||
				(document.SubscriptionVerificationState is null) ||
				!Enum.IsDefined(
					typeof(SubscriptionVerificationState),
					document.SubscriptionVerificationState.Value))
			{
				return null;
			}

			subscriptionVerificationState =
				document.SubscriptionVerificationState.Value;
		}

		HashSet<string> metricKeys = new(StringComparer.Ordinal);
		List<UsageMetric> metrics = new(document.Metrics.Count);

		foreach (MetricDocument? metricDocument in document.Metrics)
		{
			UsageMetric? metric = CreateMetric(
				metricDocument,
				document.SchemaVersion);

			if ((metric is null) || !metricKeys.Add(metric.Key))
			{
				return null;
			}

			if ((metric.ResetsAt is null) || (metric.ResetsAt.Value > now))
			{
				metrics.Add(metric);
			}
		}

		if (metrics.Count == 0)
		{
			return null;
		}

		return new UsageSnapshot(
			account,
			metrics,
			document.SourceTrust.Value,
			SnapshotStatus.Stale,
			document.FetchedAt.Value,
			document.ObservedAt,
			document.StaleAfter,
			Error: null,
			ProviderAccountIdentity: providerAccountIdentity,
			ProviderAccountDisplayIdentity: providerAccountDisplayIdentity,
			SubscriptionScopeDisplayName: subscriptionScopeDisplayName,
			PlanTier: planTier,
			SubscriptionVerificationState: subscriptionVerificationState);
	}

	private static UsageMetric? CreateMetric(
		MetricDocument? document,
		int schemaVersion)
	{
		if (document is null)
		{
			return null;
		}

		string? key = ValidateRequiredText(document.Key, MaximumKeyLength);
		string? label = ValidateRequiredText(document.Label, MaximumLabelLength);
		string? displayValue = ValidateRequiredText(
			document.DisplayValue,
			MaximumDisplayValueLength);
		string? resetDisplayValue = schemaVersion == LegacySchemaVersion
			? null
			: ValidateOptionalText(
				document.ResetDisplayValue,
				MaximumDisplayValueLength);

		if ((key is null) ||
			(label is null) ||
			(displayValue is null) ||
			((schemaVersion != LegacySchemaVersion) &&
				(document.ResetDisplayValue is not null) &&
				(resetDisplayValue is null)) ||
			((document.UsedPercent is not null) &&
				(!double.IsFinite(document.UsedPercent.Value) ||
				(document.UsedPercent.Value < 0) ||
				(document.UsedPercent.Value > 100))) ||
			((document.ResetsAt is not null) &&
				(document.ResetsAt == default)))
		{
			return null;
		}

		return new UsageMetric(
			key,
			label,
			document.UsedPercent,
			displayValue,
			document.ResetsAt,
			resetDisplayValue);
	}

	private static SnapshotDocument CreateDocument(UsageSnapshot snapshot)
	{
		ValidateAccount(snapshot.Account.Id, snapshot.Account.Provider);

		if (!Enum.IsDefined(typeof(SourceTrust), snapshot.SourceTrust) ||
			(snapshot.SourceTrust == SourceTrust.Unavailable))
		{
			throw new ArgumentException(
				"用量快取來源無效。",
				nameof(snapshot));
		}

		if ((snapshot.FetchedAt == default) ||
			((snapshot.ObservedAt is not null) &&
				(snapshot.ObservedAt == default)) ||
			((snapshot.StaleAfter is not null) &&
				(snapshot.StaleAfter == default)) ||
			(snapshot.Metrics.Count > MaximumMetricCount))
		{
			throw new ArgumentException(
				"用量快取時間或項目數無效。",
				nameof(snapshot));
		}

		string? providerAccountIdentity = ValidateOptionalText(
			snapshot.ProviderAccountIdentity,
			MaximumIdentityLength);
		string? providerAccountDisplayIdentity = ValidateOptionalText(
			snapshot.ProviderAccountDisplayIdentity,
			MaximumIdentityLength);
		string? subscriptionScopeDisplayName = ValidateOptionalText(
			snapshot.SubscriptionScopeDisplayName,
			MaximumSubscriptionScopeDisplayNameLength);
		string? planTier = ValidateOptionalText(
			snapshot.PlanTier,
			MaximumPlanTierLength);

		if (((snapshot.ProviderAccountIdentity is not null) &&
				(providerAccountIdentity is null)) ||
			((snapshot.ProviderAccountDisplayIdentity is not null) &&
				(providerAccountDisplayIdentity is null)) ||
			((snapshot.SubscriptionScopeDisplayName is not null) &&
				(subscriptionScopeDisplayName is null)) ||
			((snapshot.PlanTier is not null) && (planTier is null)) ||
			!Enum.IsDefined(
				typeof(SubscriptionVerificationState),
				snapshot.SubscriptionVerificationState))
		{
			throw new ArgumentException(
				"用量快取的帳號或訂閱範圍格式無效。",
				nameof(snapshot));
		}

		HashSet<string> metricKeys = new(StringComparer.Ordinal);
		List<MetricDocument?> metricDocuments = new(snapshot.Metrics.Count);

		foreach (UsageMetric metric in snapshot.Metrics)
		{
			ArgumentNullException.ThrowIfNull(metric);
			string? key = ValidateRequiredText(metric.Key, MaximumKeyLength);
			string? label = ValidateRequiredText(metric.Label, MaximumLabelLength);
			string? displayValue = ValidateRequiredText(
				metric.DisplayValue,
				MaximumDisplayValueLength);
			string? resetDisplayValue = ValidateOptionalText(
				metric.ResetDisplayValue,
				MaximumDisplayValueLength);

			if ((key is null) ||
				(label is null) ||
				(displayValue is null) ||
				((metric.ResetDisplayValue is not null) &&
					(resetDisplayValue is null)) ||
				!metricKeys.Add(key) ||
				((metric.UsedPercent is not null) &&
					(!double.IsFinite(metric.UsedPercent.Value) ||
					(metric.UsedPercent.Value < 0) ||
					(metric.UsedPercent.Value > 100))) ||
				((metric.ResetsAt is not null) &&
					(metric.ResetsAt == default)))
			{
				throw new ArgumentException(
					"用量快取項目無效。",
					nameof(snapshot));
			}

			metricDocuments.Add(new MetricDocument(
				key,
				label,
				metric.UsedPercent,
				displayValue,
				metric.ResetsAt,
				resetDisplayValue));
		}

		return new SnapshotDocument(
			CurrentSchemaVersion,
			snapshot.Account.Id,
			snapshot.Account.Provider,
			snapshot.SourceTrust,
			snapshot.FetchedAt,
			snapshot.ObservedAt,
			snapshot.StaleAfter,
			providerAccountIdentity,
			providerAccountDisplayIdentity,
			subscriptionScopeDisplayName,
			planTier,
			snapshot.SubscriptionVerificationState,
			metricDocuments);
	}

	private static UsageSnapshot FilterExpiredMetrics(
		UsageSnapshot snapshot,
		DateTimeOffset now)
	{
		UsageMetric[] currentMetrics = snapshot.Metrics
			.Where(metric =>
				(metric.ResetsAt is null) || (metric.ResetsAt.Value > now))
			.ToArray();

		return currentMetrics.Length == snapshot.Metrics.Count
			? snapshot
			: snapshot with { Metrics = currentMetrics };
	}

	private static JsonSerializerOptions CreateSerializerOptions()
	{
		JsonSerializerOptions options = new()
		{
			MaxDepth = 16,
			PropertyNameCaseInsensitive = false,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			WriteIndented = true
		};
		options.Converters.Add(new JsonStringEnumConverter(
			namingPolicy: null,
			allowIntegerValues: false));
		return options;
	}

	private static bool HasExactProperties(
		JsonElement element,
		IReadOnlySet<string> expectedPropertyNames)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> actualPropertyNames = new(StringComparer.Ordinal);

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!expectedPropertyNames.Contains(property.Name) ||
				!actualPropertyNames.Add(property.Name))
			{
				return false;
			}
		}

		return actualPropertyNames.SetEquals(expectedPropertyNames);
	}

	private static bool HasValidMetricShapes(
		JsonElement rootElement,
		int schemaVersion)
	{
		JsonElement metricsElement = rootElement.GetProperty("metrics");
		IReadOnlySet<string> expectedPropertyNames =
			schemaVersion == LegacySchemaVersion
				? LegacyMetricPropertyNames
				: MetricPropertyNames;

		if (metricsElement.ValueKind != JsonValueKind.Array)
		{
			return false;
		}

		foreach (JsonElement metricElement in metricsElement.EnumerateArray())
		{
			if (!HasExactProperties(metricElement, expectedPropertyNames))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsCacheAccessFailure(Exception exception)
	{
		return (exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is NotSupportedException);
	}

	private static string CreateFingerprint(ReadOnlySpan<byte> serializedDocument)
	{
		return Convert.ToHexString(SHA256.HashData(serializedDocument));
	}

	private static async Task<bool> DoesExistingFileMatchFingerprintAsync(
		string filePath,
		string expectedFingerprint,
		CancellationToken cancellationToken)
	{
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			ManagedPathDescription);
		await using FileStream stream = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		long fileLength = stream.Length;
		if (fileLength is <= 0 or > MaximumCacheFileSize)
		{
			return false;
		}

		byte[] fileContents = GC.AllocateUninitializedArray<byte>((int)fileLength);
		await stream.ReadExactlyAsync(fileContents, cancellationToken)
			.ConfigureAwait(false);
		byte[] trailingByte = new byte[1];
		if (await stream.ReadAsync(trailingByte, cancellationToken)
				.ConfigureAwait(false) != 0)
		{
			return false;
		}

		return string.Equals(
			CreateFingerprint(fileContents),
			expectedFingerprint,
			StringComparison.Ordinal);
	}

	private static async Task SaveDocumentAsync(
		string filePath,
		byte[] serializedDocument,
		CancellationToken cancellationToken)
	{
		string? directory = Path.GetDirectoryName(filePath);

		if (directory is null)
		{
			throw new IOException("用量快取路徑沒有父目錄。");
		}

		ManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directory,
			ManagedPathDescription);
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			ManagedPathDescription);
		string temporaryPath = Path.Combine(
			directory,
			$".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryPath,
				ManagedPathDescription);
			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous))
			{
				await stream.WriteAsync(serializedDocument, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryPath,
				ManagedPathDescription);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				ManagedPathDescription);
			if (File.Exists(filePath))
			{
				File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
			}
			else
			{
				File.Move(temporaryPath, filePath);
			}
		}
		finally
		{
			try
			{
				ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					temporaryPath,
					ManagedPathDescription);
				File.Delete(temporaryPath);
			}
			catch (Exception exception) when (IsCacheAccessFailure(exception))
			{
				// Best effort cleanup. Uniquely named temp files are never loaded.
			}
		}
	}

	private static bool TryReadSchemaVersion(
		JsonElement rootElement,
		out int schemaVersion)
	{
		schemaVersion = default;
		return rootElement.TryGetProperty(
			"schemaVersion",
			out JsonElement schemaVersionElement) &&
			(schemaVersionElement.ValueKind == JsonValueKind.Number) &&
			schemaVersionElement.TryGetInt32(out schemaVersion);
	}

	private static string GetCacheAccessActionName(CacheAccessAction action)
	{
		return action switch
		{
			CacheAccessAction.Load => "load",
			CacheAccessAction.Save => "save",
			_ => throw new ArgumentOutOfRangeException(nameof(action))
		};
	}

	private static void ValidateAccount(Guid accountId, ProviderKind provider)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("帳號識別碼不可為空。", nameof(accountId));
		}

		if (!Enum.IsDefined(typeof(ProviderKind), provider))
		{
			throw new ArgumentOutOfRangeException(
				nameof(provider),
				provider,
				"未知的用量提供者。");
		}
	}

	private static string? ValidateOptionalText(string? value, int maximumLength)
	{
		if (value is null)
		{
			return null;
		}

		return ValidateRequiredText(value, maximumLength);
	}

	private static string? ValidateRequiredText(string? value, int maximumLength)
	{
		if (string.IsNullOrWhiteSpace(value) ||
			(value.Length > maximumLength) ||
			!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
			value.Any(char.IsControl))
		{
			return null;
		}

		return value;
	}

	private UsageSnapshot CompleteCacheLoad(
		AccountProfile account,
		UsageSnapshot snapshot)
	{
		ReportCacheAccessRecovered(account, CacheAccessAction.Load);
		return snapshot;
	}

	private string GetFilePath(ProviderKind provider, Guid accountId)
	{
		string filePath = _filePathFactory(provider, accountId);
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		return Path.GetFullPath(filePath);
	}

	private void ReportCacheAccessFailure(
		AccountProfile account,
		CacheAccessAction action,
		string stage,
		Exception exception)
	{
		CacheDiagnosticKey key = new(account.Id, account.Provider, action);

		lock (_cacheDiagnosticStateLock)
		{
			if (!_reportedCacheAccessFailures.Add(key))
			{
				return;
			}

			if (!TryReportDiagnostic(
				$"action={GetCacheAccessActionName(action)};provider={account.Provider};" +
					$"stage={stage};result=unavailable",
				exception))
			{
				_reportedCacheAccessFailures.Remove(key);
			}
		}
	}

	private void ReportCacheAccessRecovered(
		AccountProfile account,
		CacheAccessAction action)
	{
		CacheDiagnosticKey key = new(account.Id, account.Provider, action);

		lock (_cacheDiagnosticStateLock)
		{
			if (!_reportedCacheAccessFailures.Contains(key))
			{
				return;
			}

			if (TryReportDiagnostic(
				$"action={GetCacheAccessActionName(action)};provider={account.Provider};" +
					"result=recovered",
				exception: null))
			{
				_reportedCacheAccessFailures.Remove(key);
			}
		}
	}

	private bool TryReportDiagnostic(string summary, Exception? exception)
	{
		try
		{
			return _tryReportDiagnostic(
				CacheDiagnosticOperation,
				summary,
				exception);
		}
		catch
		{
			// Diagnostics must never alter a cache outcome.
			return false;
		}
	}
}
