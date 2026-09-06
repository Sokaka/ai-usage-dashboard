using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.App.ViewModels;

namespace AiUsageDashboard.App.Persistence;

internal enum DashboardPreferencesSaveBlockReason
{
	NewerSchema,
	DocumentTooLarge,
	ExistingSettingsUnavailable,
	UnsafePath
}

internal sealed class DashboardPreferencesSaveBlockedException :
	InvalidOperationException
{
	internal DashboardPreferencesSaveBlockReason Reason { get; }

	internal DashboardPreferencesSaveBlockedException(
		DashboardPreferencesSaveBlockReason reason,
		Exception? innerException = null)
		: base(GetMessage(reason), innerException)
	{
		Reason = reason;
	}

	private static string GetMessage(
		DashboardPreferencesSaveBlockReason reason)
	{
		return reason switch
		{
			DashboardPreferencesSaveBlockReason.NewerSchema =>
				"AI Usage 顯示設定檔由較新版本建立。為避免覆寫，目前無法變更顯示設定。",
			DashboardPreferencesSaveBlockReason.DocumentTooLarge =>
				"AI Usage 顯示設定檔超過 64 KiB。為避免覆寫，目前無法變更顯示設定。",
			DashboardPreferencesSaveBlockReason.ExistingSettingsUnavailable =>
				"目前仍無法讀取浮窗設定。為避免覆寫原設定，尚未儲存變更。",
			DashboardPreferencesSaveBlockReason.UnsafePath =>
				"AI Usage 顯示設定檔路徑不安全。為保護本機資料，目前無法變更顯示設定。",
			_ => throw new ArgumentOutOfRangeException(nameof(reason))
		};
	}
}

internal sealed class JsonDashboardPreferencesStore :
	IDashboardPreferencesStore,
	IDashboardPreferencesRecoveryStore,
	IWidgetPreferencesStore
{
	[Flags]
	private enum DashboardPreferencesField
	{
		None = 0,
		UsageSortMode = 1 << 0,
		UsageDisplayMode = 1 << 1,
		IsWidgetVisible = 1 << 2,
		IsCollapsed = 1 << 3,
		IsTopmost = 1 << 4,
		MonitorDeviceName = 1 << 5,
		Corner = 1 << 6,
		StartupSurface = 1 << 7,
		Theme = 1 << 8,
		IsHeightFollowingCardCount = 1 << 9,
		AllShellPreferences = IsWidgetVisible |
			IsCollapsed |
			IsTopmost |
			MonitorDeviceName |
			Corner |
			StartupSurface |
			Theme |
			IsHeightFollowingCardCount
	}

	private sealed record DashboardPreferencesRecoveryState(
		bool IsActive,
		DashboardPreferencesField ChangedFields,
		UsageSortMode? LastObservedUsageSortMode,
		UsageDisplayMode? LastObservedUsageDisplayMode,
		DashboardShellPreferences? LastObservedShellPreferences,
		long Generation);

	private sealed record PortableImportRecoveryCheckpoint(
		DashboardPreferencesRecoveryState RecoveryState,
		PortableSettingsImportFileState? LoadedFileState,
		DashboardPreferencesSnapshot? PersistedPreferences,
		DashboardPreferencesSnapshot RuntimePreferences);

	private sealed record PreferencesDocumentReadResult(
		PreferencesDocument? Document,
		PortableSettingsImportFileState? FileState);

	private sealed class PreferencesDocument
	{
		public int SchemaVersion { get; init; }

		public UsageDisplayMode UsageDisplayMode { get; init; } =
			UsageDisplayMode.Used;

		public UsageSortMode UsageSortMode { get; init; } = UsageSortMode.Manual;

		public bool IsWidgetVisible { get; init; } = true;

		public bool IsCollapsed { get; init; }

		public bool IsTopmost { get; init; } = true;

		public string MonitorDeviceName { get; init; } = string.Empty;

		public FloatingWidgetCorner Corner { get; init; } =
			FloatingWidgetCorner.BottomRight;

		public DashboardStartupSurface StartupSurface { get; init; } =
			DashboardStartupSurface.Widget;

		public AppTheme Theme { get; init; } = AppTheme.ClassicBlue;

		public bool IsHeightFollowingCardCount { get; init; }
	}

	internal event Action? RecoveryRequested;

	private const int CurrentSchemaVersion = 6;
	private const int HeightFollowingCardCountSchemaVersion = 6;
	private const int LegacySortOnlySchemaVersion = 1;
	private const int PreviousSchemaVersion = 5;
	private const long MaximumDocumentSizeBytes = 64 * 1024;
	private const int MaximumMonitorDeviceNameLength = 260;
	private const string ManagedPathDescription = "The dashboard preferences path";
	private const int ShellPreferencesSchemaVersion = 2;
	private const int ThemeSchemaVersion = 4;
	private const int UsageDisplayModeSchemaVersion = 3;
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		Converters = { new JsonStringEnumConverter() },
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};
	private readonly string _filePath;
	private readonly Action<FileStream> _flushTemporaryFileToDisk;
	private readonly SettingsPersistenceGate _persistenceGate;
	private readonly PortableSettingsImportWriteTracker?
		_portableSettingsImportWriteTracker;
	private readonly Action<string, string> _publishTemporaryFileAtomically;
	// 啟動讀取失敗時 UI 會使用 fallback；recovery 只能合併後續實際變更的欄位，
	// 不得以 fallback snapshot 整包覆寫重新讀到的磁碟設定。
	private DashboardPreferencesField _dashboardPreferencesChangedFields;
	private long _dashboardPreferencesRecoveryGeneration;
	private bool _isDashboardShellPreferencesRecoveryActive;
	private UsageDisplayMode? _lastObservedUsageDisplayMode;
	private UsageSortMode? _lastObservedUsageSortMode;
	private DashboardShellPreferences?
		_lastObservedDashboardShellPreferences;
	private PortableImportRecoveryCheckpoint?
		_portableImportRecoveryCheckpoint;
	private PortableSettingsImportFileState? _lastKnownFileState;
	private DashboardPreferencesSnapshot? _lastKnownPersistedPreferences;

	internal JsonDashboardPreferencesStore(
		string filePath,
		SettingsPersistenceGate? persistenceGate = null,
		Action<FileStream>? flushTemporaryFileToDisk = null,
		Action<string, string>? publishTemporaryFileAtomically = null,
		PortableSettingsImportWriteTracker?
			portableSettingsImportWriteTracker = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
		_persistenceGate = persistenceGate ?? new SettingsPersistenceGate();
		_flushTemporaryFileToDisk = flushTemporaryFileToDisk ?? FlushFileToDisk;
		_publishTemporaryFileAtomically = publishTemporaryFileAtomically ??
			PublishFileAtomically;
		_portableSettingsImportWriteTracker =
			portableSettingsImportWriteTracker;
	}

	bool IDashboardPreferencesRecoveryStore.IsRecoveryActive =>
		Volatile.Read(ref _isDashboardShellPreferencesRecoveryActive);

	public async Task<UsageDisplayMode> LoadUsageDisplayModeAsync(
		CancellationToken cancellationToken = default)
	{
		bool shouldRequestRecovery = false;
		UsageDisplayMode displayMode = await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				try
				{
					PreferencesDocumentReadResult readResult =
						await LoadDocumentAsync(
							operationCancellationToken);
					BeginDashboardPreferencesRecoveryForExternalFileChange(
						readResult);
					UsageDisplayMode loadedDisplayMode =
						GetUsageDisplayModeOrDefault(readResult.Document);

					if (_isDashboardShellPreferencesRecoveryActive)
					{
						shouldRequestRecovery = true;
						AdoptLoadedUsageDisplayMode(loadedDisplayMode);
					}
					else
					{
						UpdateLastKnownPersistedPreferences(readResult);
					}

					return loadedDisplayMode;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception exception) when (
					(exception is IOException) ||
					(exception is JsonException) ||
					(exception is UnauthorizedAccessException))
				{
					BeginDashboardPreferencesRecovery();
					shouldRequestRecovery = true;
					AdoptLoadedUsageDisplayMode(UsageDisplayMode.Used);
					return UsageDisplayMode.Used;
				}
			},
			cancellationToken);

		if (shouldRequestRecovery)
		{
			RecoveryRequested?.Invoke();
		}

		return displayMode;
	}

	public async Task<UsageSortMode> LoadUsageSortModeAsync(
		CancellationToken cancellationToken = default)
	{
		bool shouldRequestRecovery = false;
		UsageSortMode sortMode = await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				try
				{
					PreferencesDocumentReadResult readResult =
						await LoadDocumentAsync(
							operationCancellationToken);
					BeginDashboardPreferencesRecoveryForExternalFileChange(
						readResult);
					UsageSortMode loadedSortMode =
						GetUsageSortModeOrDefault(readResult.Document);

					if (_isDashboardShellPreferencesRecoveryActive)
					{
						shouldRequestRecovery = true;
						AdoptLoadedUsageSortMode(loadedSortMode);
					}
					else
					{
						UpdateLastKnownPersistedPreferences(readResult);
					}

					return loadedSortMode;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception exception) when (
					(exception is IOException) ||
					(exception is JsonException) ||
					(exception is UnauthorizedAccessException))
				{
					BeginDashboardPreferencesRecovery();
					shouldRequestRecovery = true;
					AdoptLoadedUsageSortMode(UsageSortMode.Manual);
					return UsageSortMode.Manual;
				}
			},
			cancellationToken);

		if (shouldRequestRecovery)
		{
			RecoveryRequested?.Invoke();
		}

		return sortMode;
	}

	public async Task SaveUsageDisplayModeAsync(
		UsageDisplayMode displayMode,
		CancellationToken cancellationToken = default)
	{
		if (!Enum.IsDefined(displayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(displayMode),
				displayMode,
				"未知的用量顯示方式。");
		}

		bool shouldRequestRecovery = false;

		try
		{
			await _persistenceGate.RunExclusiveAsync(
				async operationCancellationToken =>
				{
					shouldRequestRecovery =
						_isDashboardShellPreferencesRecoveryActive;
					PreferencesDocumentReadResult existingReadResult =
						await LoadExistingDocumentForSaveAsync(
							operationCancellationToken);
					if (BeginDashboardPreferencesRecoveryForExternalFileChange(
							existingReadResult))
					{
						shouldRequestRecovery = true;
					}

					PreferencesDocument? existingDocument =
						existingReadResult.Document;
					PreferencesDocument document = CreateDocument(
						GetUsageSortModeOrDefault(existingDocument),
						displayMode,
						GetShellPreferencesOrDefault(existingDocument));
					await WriteDocumentAsync(document, operationCancellationToken);
					ObserveUsageDisplayMode(displayMode);
				},
				cancellationToken);
		}
		finally
		{
			if (shouldRequestRecovery)
			{
				RecoveryRequested?.Invoke();
			}
		}
	}

	public async Task SaveUsageSortModeAsync(
		UsageSortMode sortMode,
		CancellationToken cancellationToken = default)
	{
		if (!Enum.IsDefined(sortMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(sortMode),
				sortMode,
				"未知的用量排序方式。");
		}

		bool shouldRequestRecovery = false;

		try
		{
			await _persistenceGate.RunExclusiveAsync(
				async operationCancellationToken =>
				{
					shouldRequestRecovery =
						_isDashboardShellPreferencesRecoveryActive;
					PreferencesDocumentReadResult existingReadResult =
						await LoadExistingDocumentForSaveAsync(
							operationCancellationToken);
					if (BeginDashboardPreferencesRecoveryForExternalFileChange(
							existingReadResult))
					{
						shouldRequestRecovery = true;
					}

					PreferencesDocument? existingDocument =
						existingReadResult.Document;
					PreferencesDocument document = CreateDocument(
						sortMode,
						GetUsageDisplayModeOrDefault(existingDocument),
						GetShellPreferencesOrDefault(existingDocument));
					await WriteDocumentAsync(document, operationCancellationToken);
					ObserveUsageSortMode(sortMode);
				},
				cancellationToken);
		}
		finally
		{
			if (shouldRequestRecovery)
			{
				RecoveryRequested?.Invoke();
			}
		}
	}

	public async Task SaveUsagePreferencesAsync(
		UsageSortMode sortMode,
		UsageDisplayMode displayMode,
		CancellationToken cancellationToken = default)
	{
		if (!Enum.IsDefined(sortMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(sortMode),
				sortMode,
				"未知的用量排序方式。");
		}

		if (!Enum.IsDefined(displayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(displayMode),
				displayMode,
				"未知的用量顯示方式。");
		}

		bool shouldRequestRecovery = false;

		try
		{
			await _persistenceGate.RunExclusiveAsync(
				async operationCancellationToken =>
				{
					shouldRequestRecovery =
						_isDashboardShellPreferencesRecoveryActive;
					PreferencesDocumentReadResult existingReadResult =
						await LoadExistingDocumentForSaveAsync(
							operationCancellationToken);
					if (BeginDashboardPreferencesRecoveryForExternalFileChange(
							existingReadResult))
					{
						shouldRequestRecovery = true;
					}

					PreferencesDocument? existingDocument =
						existingReadResult.Document;
					PreferencesDocument document = CreateDocument(
						sortMode,
						displayMode,
						GetShellPreferencesOrDefault(existingDocument));
					await WriteDocumentAsync(document, operationCancellationToken);
					ObserveUsageSortMode(sortMode);
					ObserveUsageDisplayMode(displayMode);
				},
				cancellationToken);
		}
		finally
		{
			if (shouldRequestRecovery)
			{
				RecoveryRequested?.Invoke();
			}
		}
	}

	public async Task<DashboardShellPreferences> LoadDashboardShellPreferencesAsync(
		CancellationToken cancellationToken = default)
	{
		return await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				try
				{
					PreferencesDocumentReadResult readResult =
						await LoadDocumentAsync(
						operationCancellationToken);
					UpdateLastKnownPersistedPreferences(readResult);
					ResetDashboardPreferencesRecovery();
					return GetShellPreferencesOrDefault(readResult.Document);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (JsonException)
				{
					ResetDashboardPreferencesRecovery();
					return DashboardShellPreferences.Default;
				}
				catch (Exception exception) when (
					(exception is IOException) ||
					(exception is UnauthorizedAccessException))
				{
					BeginDashboardPreferencesRecovery();
					return DashboardShellPreferences.Default;
				}
			},
			cancellationToken);
	}

	public async Task SavePortablePreferencesAsync(
		UsageSortMode sortMode,
		UsageDisplayMode displayMode,
		DashboardShellPreferences shellPreferences,
		CancellationToken cancellationToken = default)
	{
		if (!Enum.IsDefined(sortMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(sortMode),
				sortMode,
				"未知的用量排序方式。");
		}

		if (!Enum.IsDefined(displayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(displayMode),
				displayMode,
				"未知的用量顯示方式。");
		}

		ArgumentNullException.ThrowIfNull(shellPreferences);
		ValidateShellPreferences(shellPreferences);

		await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				await LoadExistingDocumentForDashboardShellSaveAsync(
					operationCancellationToken);
				PreferencesDocument document = CreateDocument(
					sortMode,
					displayMode,
					shellPreferences);
				await WriteDocumentAsync(document, operationCancellationToken);
				ResetDashboardPreferencesRecovery();
			},
			cancellationToken);
	}

	public async Task SaveDashboardShellPreferencesAsync(
		DashboardShellPreferences preferences,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(preferences);
		ValidateShellPreferences(preferences);

		await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				bool wasRecoveryActive =
					_isDashboardShellPreferencesRecoveryActive;

				if (wasRecoveryActive)
				{
					ObserveDashboardShellPreferences(preferences);
				}

				PreferencesDocumentReadResult existingReadResult =
					await LoadExistingDocumentForDashboardShellSaveAsync(
						operationCancellationToken);

				if (!wasRecoveryActive)
				{
					BeginDashboardPreferencesRecoveryForExternalFileChange(
						existingReadResult);
					ObserveDashboardShellPreferences(preferences);
				}

				PreferencesDocument? existingDocument = existingReadResult.Document;
				DashboardShellPreferences preferencesToSave = preferences;

				if (_isDashboardShellPreferencesRecoveryActive &&
					TryGetShellPreferences(
						existingDocument,
						out DashboardShellPreferences existingPreferences))
				{
					if ((_dashboardPreferencesChangedFields &
						DashboardPreferencesField.AllShellPreferences) ==
						DashboardPreferencesField.None)
					{
						return;
					}

					preferencesToSave = MergeChangedShellPreferences(
						existingPreferences,
						preferences,
						_dashboardPreferencesChangedFields);
				}

				PreferencesDocument document = CreateDocument(
					GetUsageSortModeOrDefault(existingDocument),
					GetUsageDisplayModeOrDefault(existingDocument),
					preferencesToSave);
				await WriteDocumentAsync(document, operationCancellationToken);

				if (_isDashboardShellPreferencesRecoveryActive &&
					!TryGetShellPreferences(existingDocument, out _))
				{
					ResetDashboardPreferencesRecovery();
				}
			},
			cancellationToken);
	}

	public async Task<DashboardPreferencesRecoveryPrepareResult>
		PrepareRecoveryAsync(
			DashboardPreferencesSnapshot currentPreferences,
			CancellationToken cancellationToken = default)
	{
		ValidateDashboardPreferencesSnapshot(currentPreferences);
		return await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				if (!_isDashboardShellPreferencesRecoveryActive)
				{
					return new DashboardPreferencesRecoveryPrepareResult(
						DashboardPreferencesRecoveryStatus.NotRequired,
						_dashboardPreferencesRecoveryGeneration);
				}

				ObserveDashboardPreferences(currentPreferences);

				try
				{
					await LoadExistingDocumentForSaveAsync(
						operationCancellationToken);
				}
				catch (Exception exception) when (
					(exception is IOException) ||
					(exception is UnauthorizedAccessException))
				{
					return new DashboardPreferencesRecoveryPrepareResult(
						DashboardPreferencesRecoveryStatus.Pending,
						_dashboardPreferencesRecoveryGeneration);
				}

				return new DashboardPreferencesRecoveryPrepareResult(
					DashboardPreferencesRecoveryStatus.Ready,
					_dashboardPreferencesRecoveryGeneration);
			},
			cancellationToken);
	}

	public async Task<DashboardPreferencesRecoveryCommitResult>
		CommitRecoveryAsync(
			long expectedGeneration,
			DashboardPreferencesSnapshot currentPreferences,
			CancellationToken cancellationToken = default)
	{
		ValidateDashboardPreferencesSnapshot(currentPreferences);
		return await _persistenceGate.RunExclusiveAsync(
			async operationCancellationToken =>
			{
				if (!_isDashboardShellPreferencesRecoveryActive)
				{
					return new DashboardPreferencesRecoveryCommitResult(
						DashboardPreferencesRecoveryStatus.NotRequired,
						_dashboardPreferencesRecoveryGeneration,
						Preferences: null);
				}

				if (_dashboardPreferencesRecoveryGeneration != expectedGeneration)
				{
					return new DashboardPreferencesRecoveryCommitResult(
						DashboardPreferencesRecoveryStatus.Pending,
						_dashboardPreferencesRecoveryGeneration,
						Preferences: null);
				}

				ObserveDashboardPreferences(currentPreferences);
				PreferencesDocumentReadResult existingReadResult;

				try
				{
					existingReadResult = await LoadExistingDocumentForSaveAsync(
						operationCancellationToken);
				}
				catch (Exception exception) when (
					(exception is IOException) ||
					(exception is UnauthorizedAccessException))
				{
					return new DashboardPreferencesRecoveryCommitResult(
						DashboardPreferencesRecoveryStatus.Pending,
						_dashboardPreferencesRecoveryGeneration,
						Preferences: null);
				}

				PreferencesDocument? existingDocument = existingReadResult.Document;
				UsageSortMode sortMode = HasChanged(
					_dashboardPreferencesChangedFields,
					DashboardPreferencesField.UsageSortMode)
					? currentPreferences.UsageSortMode
					: GetUsageSortModeOrDefault(existingDocument);
				UsageDisplayMode displayMode = HasChanged(
					_dashboardPreferencesChangedFields,
					DashboardPreferencesField.UsageDisplayMode)
					? currentPreferences.UsageDisplayMode
					: GetUsageDisplayModeOrDefault(existingDocument);
				DashboardShellPreferences shellPreferences =
					TryGetShellPreferences(
						existingDocument,
						out DashboardShellPreferences existingShellPreferences)
						? MergeChangedShellPreferences(
							existingShellPreferences,
							currentPreferences.ShellPreferences,
							_dashboardPreferencesChangedFields)
						: currentPreferences.ShellPreferences;
				DashboardPreferencesSnapshot recoveredPreferences = new(
					sortMode,
					displayMode,
					shellPreferences);

				if (_dashboardPreferencesChangedFields !=
					DashboardPreferencesField.None)
				{
					try
					{
						await WriteDocumentAsync(
							CreateDocument(sortMode, displayMode, shellPreferences),
							operationCancellationToken);
					}
					catch (Exception exception) when (
						(exception is IOException) ||
						(exception is UnauthorizedAccessException))
					{
						return new DashboardPreferencesRecoveryCommitResult(
							DashboardPreferencesRecoveryStatus.Pending,
							_dashboardPreferencesRecoveryGeneration,
							Preferences: null);
					}
				}
				else
				{
					_lastKnownFileState = existingReadResult.FileState;
					_lastKnownPersistedPreferences = recoveredPreferences;
				}

				return new DashboardPreferencesRecoveryCommitResult(
					DashboardPreferencesRecoveryStatus.Ready,
					_dashboardPreferencesRecoveryGeneration,
					recoveredPreferences);
			},
			cancellationToken);
	}

	public async Task<bool> CompleteRecoveryAsync(
		long expectedGeneration,
		CancellationToken cancellationToken = default)
	{
		return await _persistenceGate.RunExclusiveAsync(
			operationCancellationToken =>
			{
				operationCancellationToken.ThrowIfCancellationRequested();
				if (!_isDashboardShellPreferencesRecoveryActive ||
					(_dashboardPreferencesRecoveryGeneration != expectedGeneration))
				{
					return Task.FromResult(false);
				}

				ResetDashboardPreferencesRecovery();
				return Task.FromResult(true);
			},
			cancellationToken);
	}

	internal Task BeginPortableImportTransactionAsync(
		DashboardPreferencesSnapshot runtimePreferences,
		CancellationToken cancellationToken)
	{
		ValidateDashboardPreferencesSnapshot(runtimePreferences);
		cancellationToken.ThrowIfCancellationRequested();
		_portableImportRecoveryCheckpoint = new PortableImportRecoveryCheckpoint(
			CaptureDashboardPreferencesRecoveryState(),
			_lastKnownFileState,
			_lastKnownPersistedPreferences,
			runtimePreferences);
		_dashboardPreferencesRecoveryGeneration++;
		return Task.CompletedTask;
	}

	internal Task<bool> SynchronizeAfterPortableImportRestoreAsync(
		PortableSettingsImportRestoreResult restoreResult,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(restoreResult);
		cancellationToken.ThrowIfCancellationRequested();
		long currentGeneration = _dashboardPreferencesRecoveryGeneration;
		PortableSettingsImportTargetRestoreResult targetResult =
			restoreResult.GetTargetResult(_filePath);
		PortableImportRecoveryCheckpoint? checkpoint =
			_portableImportRecoveryCheckpoint;
		bool canRestoreCheckpointPrecisely =
			(checkpoint is not null) &&
			targetResult.DoesFinalTargetMatchRollbackBaseline &&
			(targetResult.RollbackBaselineState is
				PortableSettingsImportFileState rollbackBaselineState) &&
			(checkpoint.LoadedFileState is
				PortableSettingsImportFileState loadedFileState) &&
			(loadedFileState == rollbackBaselineState);

		if (checkpoint is not null)
		{
			RestoreDashboardPreferencesRecoveryState(
				checkpoint.RecoveryState);
			_lastKnownFileState = checkpoint.LoadedFileState;
			_lastKnownPersistedPreferences = checkpoint.PersistedPreferences;
		}
		else
		{
			BeginDashboardPreferencesRecovery();
		}

		if (!canRestoreCheckpointPrecisely)
		{
			bool wasRecoveryActive =
				_isDashboardShellPreferencesRecoveryActive;
			BeginDashboardPreferencesRecovery();

			if (!wasRecoveryActive && (checkpoint is not null))
			{
				DashboardPreferencesSnapshot observationBaseline =
					checkpoint.PersistedPreferences ??
					checkpoint.RuntimePreferences;
				SeedDashboardPreferencesObservation(observationBaseline);
				ObserveDashboardPreferences(checkpoint.RuntimePreferences);
			}

			_lastKnownFileState = null;
			_lastKnownPersistedPreferences = null;
		}

		_dashboardPreferencesRecoveryGeneration = Math.Max(
			_dashboardPreferencesRecoveryGeneration,
			currentGeneration) + 1;
		return Task.FromResult(_isDashboardShellPreferencesRecoveryActive);
	}

	internal void CompletePortableImportTransaction()
	{
		_portableImportRecoveryCheckpoint = null;
		_dashboardPreferencesRecoveryGeneration++;
	}

	private static PreferencesDocument CreateDocument(
		UsageSortMode sortMode,
		UsageDisplayMode displayMode,
		DashboardShellPreferences shellPreferences)
	{
		return new PreferencesDocument
		{
			SchemaVersion = CurrentSchemaVersion,
			UsageDisplayMode = displayMode,
			UsageSortMode = sortMode,
			IsWidgetVisible = shellPreferences.IsWidgetVisible,
			IsCollapsed = shellPreferences.IsCollapsed,
			IsTopmost = shellPreferences.IsTopmost,
			MonitorDeviceName = shellPreferences.MonitorDeviceName,
			Corner = shellPreferences.Corner,
			StartupSurface = shellPreferences.StartupSurface,
			Theme = shellPreferences.Theme,
			IsHeightFollowingCardCount =
				shellPreferences.IsHeightFollowingCardCount
		};
	}

	private static UsageDisplayMode GetUsageDisplayModeOrDefault(
		PreferencesDocument? document)
	{
		return (document is not null) &&
			(document.SchemaVersion is UsageDisplayModeSchemaVersion or
				ThemeSchemaVersion or
				PreviousSchemaVersion or
				CurrentSchemaVersion) &&
			Enum.IsDefined(document!.UsageDisplayMode)
				? document.UsageDisplayMode
				: UsageDisplayMode.Used;
	}

	private static UsageSortMode GetUsageSortModeOrDefault(
		PreferencesDocument? document)
	{
		return IsSupportedDocument(document) &&
			Enum.IsDefined(document!.UsageSortMode)
				? document.UsageSortMode
				: UsageSortMode.Manual;
	}

	private static DashboardShellPreferences GetShellPreferencesOrDefault(
		PreferencesDocument? document)
	{
		return TryGetShellPreferences(
			document,
			out DashboardShellPreferences preferences)
			? preferences
			: DashboardShellPreferences.Default;
	}

	private static bool TryGetShellPreferences(
		PreferencesDocument? document,
		out DashboardShellPreferences preferences)
	{
		if ((document is null) ||
			(document.SchemaVersion is not (
				ShellPreferencesSchemaVersion or
				UsageDisplayModeSchemaVersion or
				ThemeSchemaVersion or
				PreviousSchemaVersion or
				CurrentSchemaVersion)) ||
			!Enum.IsDefined(document.Corner) ||
			!Enum.IsDefined(document.StartupSurface) ||
			((document.SchemaVersion is ThemeSchemaVersion or
				PreviousSchemaVersion or
				CurrentSchemaVersion) &&
				!Enum.IsDefined(document.Theme)) ||
			(document.MonitorDeviceName is null) ||
			(document.MonitorDeviceName.Length > MaximumMonitorDeviceNameLength))
		{
			preferences = DashboardShellPreferences.Default;
			return false;
		}

		preferences = new DashboardShellPreferences(
			document.IsWidgetVisible,
			document.IsCollapsed,
			document.IsTopmost,
			document.MonitorDeviceName,
			document.Corner,
			document.StartupSurface,
			document.SchemaVersion is ThemeSchemaVersion or
				PreviousSchemaVersion or
				CurrentSchemaVersion
				? document.Theme
				: AppTheme.ClassicBlue,
			document.SchemaVersion >= HeightFollowingCardCountSchemaVersion &&
				document.IsHeightFollowingCardCount);
		return true;
	}

	private static bool IsSupportedDocument(PreferencesDocument? document)
	{
		return (document is not null) &&
			(document.SchemaVersion is LegacySortOnlySchemaVersion or
				ShellPreferencesSchemaVersion or
				UsageDisplayModeSchemaVersion or
				ThemeSchemaVersion or
				PreviousSchemaVersion or
				CurrentSchemaVersion);
	}

	private static void FlushFileToDisk(FileStream stream)
	{
		stream.Flush(flushToDisk: true);
	}

	private static void PublishFileAtomically(
		string sourceFilePath,
		string destinationFilePath)
	{
		File.Move(sourceFilePath, destinationFilePath, overwrite: true);
	}

	private static void ValidateShellPreferences(
		DashboardShellPreferences preferences)
	{
		if (!Enum.IsDefined(preferences.Corner))
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferences),
				preferences.Corner,
				"未知的浮窗停靠位置。");
		}

		if (!Enum.IsDefined(preferences.StartupSurface))
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferences),
				preferences.StartupSurface,
				"未知的啟動介面。");
		}

		ArgumentNullException.ThrowIfNull(preferences.MonitorDeviceName);

		if (preferences.MonitorDeviceName.Length > MaximumMonitorDeviceNameLength)
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferences),
				"螢幕識別名稱過長。");
		}

		if (!Enum.IsDefined(preferences.Theme))
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferences),
				preferences.Theme,
				"未知的應用程式主題。");
		}
	}

	private static void ValidateDashboardPreferencesSnapshot(
		DashboardPreferencesSnapshot preferences)
	{
		ArgumentNullException.ThrowIfNull(preferences);

		if (!Enum.IsDefined(preferences.UsageSortMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferences),
				preferences.UsageSortMode,
				"未知的用量排序方式。");
		}

		if (!Enum.IsDefined(preferences.UsageDisplayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferences),
				preferences.UsageDisplayMode,
				"未知的用量顯示方式。");
		}

		ValidateShellPreferences(preferences.ShellPreferences);
	}

	private static DashboardPreferencesField GetChangedShellPreferencesFields(
		DashboardShellPreferences previousPreferences,
		DashboardShellPreferences currentPreferences)
	{
		DashboardPreferencesField changedFields = DashboardPreferencesField.None;

		if (previousPreferences.IsWidgetVisible !=
			currentPreferences.IsWidgetVisible)
		{
			changedFields |= DashboardPreferencesField.IsWidgetVisible;
		}

		if (previousPreferences.IsCollapsed != currentPreferences.IsCollapsed)
		{
			changedFields |= DashboardPreferencesField.IsCollapsed;
		}

		if (previousPreferences.IsTopmost != currentPreferences.IsTopmost)
		{
			changedFields |= DashboardPreferencesField.IsTopmost;
		}

		if (!string.Equals(
			previousPreferences.MonitorDeviceName,
			currentPreferences.MonitorDeviceName,
			StringComparison.Ordinal))
		{
			changedFields |= DashboardPreferencesField.MonitorDeviceName;
		}

		if (previousPreferences.Corner != currentPreferences.Corner)
		{
			changedFields |= DashboardPreferencesField.Corner;
		}

		if (previousPreferences.StartupSurface != currentPreferences.StartupSurface)
		{
			changedFields |= DashboardPreferencesField.StartupSurface;
		}

		if (previousPreferences.Theme != currentPreferences.Theme)
		{
			changedFields |= DashboardPreferencesField.Theme;
		}

		if (previousPreferences.IsHeightFollowingCardCount !=
			currentPreferences.IsHeightFollowingCardCount)
		{
			changedFields |=
				DashboardPreferencesField.IsHeightFollowingCardCount;
		}

		return changedFields;
	}

	private static DashboardShellPreferences MergeChangedShellPreferences(
		DashboardShellPreferences existingPreferences,
		DashboardShellPreferences currentPreferences,
		DashboardPreferencesField changedFields)
	{
		return existingPreferences with
		{
			IsWidgetVisible = HasChanged(
				changedFields,
				DashboardPreferencesField.IsWidgetVisible)
				? currentPreferences.IsWidgetVisible
				: existingPreferences.IsWidgetVisible,
			IsCollapsed = HasChanged(
				changedFields,
				DashboardPreferencesField.IsCollapsed)
				? currentPreferences.IsCollapsed
				: existingPreferences.IsCollapsed,
			IsTopmost = HasChanged(
				changedFields,
				DashboardPreferencesField.IsTopmost)
				? currentPreferences.IsTopmost
				: existingPreferences.IsTopmost,
			MonitorDeviceName = HasChanged(
				changedFields,
				DashboardPreferencesField.MonitorDeviceName)
				? currentPreferences.MonitorDeviceName
				: existingPreferences.MonitorDeviceName,
			Corner = HasChanged(changedFields, DashboardPreferencesField.Corner)
				? currentPreferences.Corner
				: existingPreferences.Corner,
			StartupSurface = HasChanged(
				changedFields,
				DashboardPreferencesField.StartupSurface)
				? currentPreferences.StartupSurface
				: existingPreferences.StartupSurface,
			Theme = HasChanged(changedFields, DashboardPreferencesField.Theme)
				? currentPreferences.Theme
				: existingPreferences.Theme,
			IsHeightFollowingCardCount = HasChanged(
				changedFields,
				DashboardPreferencesField.IsHeightFollowingCardCount)
				? currentPreferences.IsHeightFollowingCardCount
				: existingPreferences.IsHeightFollowingCardCount
		};
	}

	private static DashboardPreferencesSnapshot CreateSnapshot(
		PreferencesDocument? document)
	{
		return new DashboardPreferencesSnapshot(
			GetUsageSortModeOrDefault(document),
			GetUsageDisplayModeOrDefault(document),
			GetShellPreferencesOrDefault(document));
	}

	private static PortableSettingsImportFileState CreateAbsentFileState()
	{
		return new PortableSettingsImportFileState(
			Exists: false,
			ContentFingerprint: null);
	}

	private static PortableSettingsImportFileState CreateExistingFileState(
		byte[] contents)
	{
		return new PortableSettingsImportFileState(
			Exists: true,
			ContentFingerprint: Convert.ToHexString(SHA256.HashData(contents))
				.ToLowerInvariant());
	}

	private void UpdateLastKnownPersistedPreferences(
		PreferencesDocumentReadResult readResult)
	{
		_lastKnownFileState = readResult.FileState;
		_lastKnownPersistedPreferences = readResult.FileState is null
			? null
			: CreateSnapshot(readResult.Document);
	}

	private bool BeginDashboardPreferencesRecoveryForExternalFileChange(
		PreferencesDocumentReadResult readResult)
	{
		if (_isDashboardShellPreferencesRecoveryActive ||
			(_lastKnownFileState is not
				PortableSettingsImportFileState lastKnownFileState) ||
			(_lastKnownPersistedPreferences is not
				DashboardPreferencesSnapshot persistedPreferences) ||
			(readResult.FileState is not
				PortableSettingsImportFileState currentFileState) ||
			(lastKnownFileState == currentFileState))
		{
			return false;
		}

		BeginDashboardPreferencesRecovery();
		SeedDashboardPreferencesObservation(persistedPreferences);
		return true;
	}

	private static bool HasChanged(
		DashboardPreferencesField changedFields,
		DashboardPreferencesField field)
	{
		return (changedFields & field) != 0;
	}

	private async Task<PreferencesDocumentReadResult> LoadDocumentAsync(
		CancellationToken cancellationToken)
	{
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			_filePath,
			ManagedPathDescription);
		if (!File.Exists(_filePath))
		{
			return new PreferencesDocumentReadResult(
				Document: null,
				FileState: CreateAbsentFileState());
		}

		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			_filePath,
			ManagedPathDescription);
		await using FileStream stream = new(
			_filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4096,
			useAsync: true);
		if (stream.Length > MaximumDocumentSizeBytes)
		{
			return new PreferencesDocumentReadResult(
				Document: null,
				FileState: null);
		}

		byte[] contents = new byte[(int)stream.Length];
		await stream.ReadExactlyAsync(contents, cancellationToken);
		PortableSettingsImportFileState fileState =
			CreateExistingFileState(contents);

		try
		{
			return new PreferencesDocumentReadResult(
				JsonSerializer.Deserialize<PreferencesDocument>(
					contents,
					SerializerOptions),
				fileState);
		}
		catch (JsonException)
		{
			return new PreferencesDocumentReadResult(
				Document: null,
				fileState);
		}
	}

	private async Task WriteDocumentAsync(
		PreferencesDocument document,
		CancellationToken cancellationToken)
	{
		string? directoryPath = Path.GetDirectoryName(_filePath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException("顯示設定檔缺少所在資料夾。");
		}

		CreateManagedDirectoryForSave(
			directoryPath,
			ManagedPathDescription);
		EnsureManagedFilePathIsSafeForSave(
			_filePath,
			ManagedPathDescription);
		string temporaryFilePath = Path.Combine(
			directoryPath,
			$".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
		PortableSettingsImportFileState writtenFileState;
		try
		{
			EnsureManagedFilePathIsSafeForSave(
				temporaryFilePath,
				ManagedPathDescription);
			await using (FileStream stream = new(
				temporaryFilePath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await JsonSerializer.SerializeAsync(
					stream,
					document,
					SerializerOptions,
					cancellationToken);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				_flushTemporaryFileToDisk(stream);
				stream.Position = 0;
				byte[] contentHash = await SHA256.HashDataAsync(
					stream,
					cancellationToken).ConfigureAwait(false);
				writtenFileState = new PortableSettingsImportFileState(
					Exists: true,
					ContentFingerprint: Convert.ToHexString(contentHash)
						.ToLowerInvariant());
			}

			// This same-directory publish is the commit boundary. Before it, the
			// existing preferences remain authoritative; after it, the fully
			// flushed temporary document is authoritative.
			if (_portableSettingsImportWriteTracker is not null)
			{
				await _portableSettingsImportWriteTracker.RecordPreparedWriteAsync(
					_filePath,
					temporaryFilePath,
					cancellationToken).ConfigureAwait(false);
			}

			EnsureManagedFilePathIsSafeForSave(
				temporaryFilePath,
				ManagedPathDescription);
			EnsureManagedFilePathIsSafeForSave(
				_filePath,
				ManagedPathDescription);
			_publishTemporaryFileAtomically(temporaryFilePath, _filePath);
			_lastKnownFileState = writtenFileState;
			_lastKnownPersistedPreferences = CreateSnapshot(document);
		}
		finally
		{
			try
			{
				ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					temporaryFilePath,
					ManagedPathDescription);
				if (File.Exists(temporaryFilePath))
				{
					File.Delete(temporaryFilePath);
				}
			}
			catch (Exception exception) when (
				(exception is IOException) ||
				(exception is UnauthorizedAccessException) ||
				(exception is NotSupportedException))
			{
				// Best effort cleanup. Uniquely named temp files are never loaded.
			}
		}
	}

	private async Task<PreferencesDocumentReadResult>
		LoadExistingDocumentForSaveAsync(
		CancellationToken cancellationToken)
	{
		EnsureManagedFilePathIsSafeForSave(
			_filePath,
			ManagedPathDescription);
		if (!File.Exists(_filePath))
		{
			return new PreferencesDocumentReadResult(
				Document: null,
				FileState: CreateAbsentFileState());
		}

		EnsureManagedFilePathIsSafeForSave(
			_filePath,
			ManagedPathDescription);
		await using FileStream stream = new(
			_filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 4096,
			useAsync: true);
		if (stream.Length > MaximumDocumentSizeBytes)
		{
			throw new DashboardPreferencesSaveBlockedException(
				DashboardPreferencesSaveBlockReason.DocumentTooLarge);
		}

		byte[] contents = new byte[(int)stream.Length];
		await stream.ReadExactlyAsync(contents, cancellationToken);
		PortableSettingsImportFileState fileState =
			CreateExistingFileState(contents);

		try
		{
			using JsonDocument document = JsonDocument.Parse(contents);

			if ((document.RootElement.ValueKind == JsonValueKind.Object) &&
				document.RootElement.TryGetProperty(
					"schemaVersion",
					out JsonElement schemaVersionElement) &&
				(schemaVersionElement.ValueKind == JsonValueKind.Number) &&
				schemaVersionElement.TryGetInt32(out int schemaVersion) &&
				(schemaVersion > CurrentSchemaVersion))
			{
				throw new DashboardPreferencesSaveBlockedException(
					DashboardPreferencesSaveBlockReason.NewerSchema);
			}

			return new PreferencesDocumentReadResult(
				document.RootElement.Deserialize<PreferencesDocument>(
					SerializerOptions),
				fileState);
		}
		catch (JsonException)
		{
			// An invalid preference document can be replaced by a known-good document.
			return new PreferencesDocumentReadResult(
				Document: null,
				FileState: fileState);
		}
	}

	private async Task<PreferencesDocumentReadResult>
		LoadExistingDocumentForDashboardShellSaveAsync(
			CancellationToken cancellationToken)
	{
		try
		{
			PreferencesDocumentReadResult readResult =
				await LoadExistingDocumentForSaveAsync(cancellationToken);
			return readResult;
		}
		catch (Exception exception) when (
			_isDashboardShellPreferencesRecoveryActive &&
			((exception is IOException) ||
				(exception is UnauthorizedAccessException)))
		{
			throw new DashboardPreferencesSaveBlockedException(
				DashboardPreferencesSaveBlockReason.ExistingSettingsUnavailable,
				exception);
		}
	}

	private static void CreateManagedDirectoryForSave(
		string directoryPath,
		string pathDescription)
	{
		ManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			pathDescription,
			CreateUnsafePathException);
	}

	private static void EnsureManagedFilePathIsSafeForSave(
		string filePath,
		string pathDescription)
	{
		ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			filePath,
			pathDescription,
			CreateUnsafePathException);
	}

	private static Exception CreateUnsafePathException(string message)
	{
		return new DashboardPreferencesSaveBlockedException(
			DashboardPreferencesSaveBlockReason.UnsafePath,
			new IOException(message));
	}

	private DashboardPreferencesRecoveryState
		CaptureDashboardPreferencesRecoveryState()
	{
		return new DashboardPreferencesRecoveryState(
			_isDashboardShellPreferencesRecoveryActive,
			_dashboardPreferencesChangedFields,
			_lastObservedUsageSortMode,
			_lastObservedUsageDisplayMode,
			_lastObservedDashboardShellPreferences,
			_dashboardPreferencesRecoveryGeneration);
	}

	private void RestoreDashboardPreferencesRecoveryState(
		DashboardPreferencesRecoveryState state)
	{
		_isDashboardShellPreferencesRecoveryActive = state.IsActive;
		_dashboardPreferencesChangedFields = state.ChangedFields;
		_lastObservedUsageSortMode = state.LastObservedUsageSortMode;
		_lastObservedUsageDisplayMode = state.LastObservedUsageDisplayMode;
		_lastObservedDashboardShellPreferences =
			state.LastObservedShellPreferences;
		_dashboardPreferencesRecoveryGeneration = state.Generation;
	}

	private void BeginDashboardPreferencesRecovery()
	{
		if (_isDashboardShellPreferencesRecoveryActive)
		{
			return;
		}

		_isDashboardShellPreferencesRecoveryActive = true;
		_dashboardPreferencesChangedFields = DashboardPreferencesField.None;
		_lastObservedUsageSortMode = null;
		_lastObservedUsageDisplayMode = null;
		_lastObservedDashboardShellPreferences = null;
		_dashboardPreferencesRecoveryGeneration++;
	}

	private void SeedDashboardPreferencesObservation(
		DashboardPreferencesSnapshot preferences)
	{
		_lastObservedUsageSortMode = preferences.UsageSortMode;
		_lastObservedUsageDisplayMode = preferences.UsageDisplayMode;
		_lastObservedDashboardShellPreferences = preferences.ShellPreferences;
	}

	// Partial load 的回傳值會由 UI 採納；只推進該欄位的 observation baseline，
	// 不得把磁碟上的其他欄位視為已同步，也不得標記成 runtime 變更。
	private void AdoptLoadedUsageSortMode(UsageSortMode sortMode)
	{
		if (_isDashboardShellPreferencesRecoveryActive)
		{
			_lastObservedUsageSortMode = sortMode;
		}
	}

	private void AdoptLoadedUsageDisplayMode(UsageDisplayMode displayMode)
	{
		if (_isDashboardShellPreferencesRecoveryActive)
		{
			_lastObservedUsageDisplayMode = displayMode;
		}
	}

	private void ObserveDashboardPreferences(
		DashboardPreferencesSnapshot preferences)
	{
		ObserveUsageSortMode(preferences.UsageSortMode);
		ObserveUsageDisplayMode(preferences.UsageDisplayMode);
		ObserveDashboardShellPreferences(preferences.ShellPreferences);
	}

	private void ObserveUsageSortMode(UsageSortMode sortMode)
	{
		if (!_isDashboardShellPreferencesRecoveryActive)
		{
			return;
		}

		if ((_lastObservedUsageSortMode is UsageSortMode previousSortMode) &&
			(previousSortMode != sortMode))
		{
			_dashboardPreferencesChangedFields |=
				DashboardPreferencesField.UsageSortMode;
			_dashboardPreferencesRecoveryGeneration++;
		}

		_lastObservedUsageSortMode = sortMode;
	}

	private void ObserveUsageDisplayMode(UsageDisplayMode displayMode)
	{
		if (!_isDashboardShellPreferencesRecoveryActive)
		{
			return;
		}

		if ((_lastObservedUsageDisplayMode is UsageDisplayMode previousDisplayMode) &&
			(previousDisplayMode != displayMode))
		{
			_dashboardPreferencesChangedFields |=
				DashboardPreferencesField.UsageDisplayMode;
			_dashboardPreferencesRecoveryGeneration++;
		}

		_lastObservedUsageDisplayMode = displayMode;
	}

	private void ObserveDashboardShellPreferences(
		DashboardShellPreferences preferences)
	{
		if (!_isDashboardShellPreferencesRecoveryActive)
		{
			return;
		}

		if (_lastObservedDashboardShellPreferences is not null)
		{
			DashboardPreferencesField changedFields =
				GetChangedShellPreferencesFields(
					_lastObservedDashboardShellPreferences,
					preferences);
			if (changedFields != DashboardPreferencesField.None)
			{
				_dashboardPreferencesChangedFields |= changedFields;
				_dashboardPreferencesRecoveryGeneration++;
			}
		}

		_lastObservedDashboardShellPreferences = preferences;
	}

	private void ResetDashboardPreferencesRecovery()
	{
		_isDashboardShellPreferencesRecoveryActive = false;
		_dashboardPreferencesChangedFields = DashboardPreferencesField.None;
		_lastObservedUsageSortMode = null;
		_lastObservedUsageDisplayMode = null;
		_lastObservedDashboardShellPreferences = null;
		_dashboardPreferencesRecoveryGeneration++;
	}
}
