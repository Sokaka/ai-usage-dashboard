using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiUsageDashboard.AntigravitySpike;

public sealed record AntigravityStatusLineAccountDisplay(
	string Email,
	DateTimeOffset CapturedAtUtc,
	string? PlanTier = null);

internal static class AntigravityStatusLineObservationSignal
{
	private const string EventNamePrefix =
		@"Local\AiUsageDashboard.Antigravity.StatusLineObserved.v1.";

	internal sealed class Listener : IDisposable
	{
		private readonly EventWaitHandle _event;

		private Listener(EventWaitHandle @event)
		{
			_event = @event;
		}

		internal static Listener? TryCreate(string email)
		{
			try
			{
				if (!TryGetEventName(email, out string eventName))
				{
					return null;
				}

				return new Listener(new EventWaitHandle(
					initialState: false,
					EventResetMode.AutoReset,
					eventName));
			}
			catch
			{
				return null;
			}
		}

		internal bool TryConsume()
		{
			try
			{
				return _event.WaitOne(TimeSpan.Zero);
			}
			catch
			{
				return false;
			}
		}

		public void Dispose()
		{
			_event.Dispose();
		}
	}

	internal static void TryNotify(string email)
	{
		try
		{
			if (!TryGetEventName(email, out string eventName))
			{
				return;
			}

			using EventWaitHandle @event =
				EventWaitHandle.OpenExisting(eventName);
			_ = @event.Set();
		}
		catch
		{
			// The app is not required to be running while AGY invokes the
			// helper. Capture remains successful without a listener.
		}
	}

	private static bool TryGetEventName(
		string email,
		out string eventName)
	{
		eventName = string.Empty;

		if (!AntigravityStatusLineCapture.IsValidAsciiEmail(email))
		{
			return false;
		}

		byte[] normalizedEmail = Encoding.UTF8.GetBytes(
			email.ToUpperInvariant());

		try
		{
			eventName = EventNamePrefix +
				Convert.ToHexString(SHA256.HashData(normalizedEmail));
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(normalizedEmail);
		}
	}
}

public static class AntigravityStatusLineCapture
{
	public const int MaximumPayloadBytes = 256 * 1024;
	internal const int MaximumPlanTierLength = 64;
	public const string MarkerArgument =
		"--ai-usage-dashboard-agy-statusline-v1";
	internal const string SmokeTestArgument =
		"--ai-usage-dashboard-agy-statusline-smoke-test-v1";

	private const int SchemaVersion = 1;
	private const int MaximumCaptureFileBytes = 4 * 1024;
	private static readonly TimeSpan MinimumSameAccountWriteInterval =
		TimeSpan.FromSeconds(30);
	internal static readonly TimeSpan MaximumFutureClockSkew =
		TimeSpan.FromMinutes(5);
	internal const string CaptureFileName = "account-display-v1.json";
	internal const string LockFileName = ".account-display-v1.lock";
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	public static string GetCaptureFilePath()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(
			localApplicationData,
			"AiUsageDashboard",
			"private",
			"antigravity-statusline",
			CaptureFileName);
	}

	public static bool TryParsePayload(
		ReadOnlySpan<byte> payload,
		DateTimeOffset capturedAtUtc,
		out AntigravityStatusLineAccountDisplay? accountDisplay)
	{
		accountDisplay = null;

		try
		{
			if ((payload.Length == 0) ||
				(payload.Length > MaximumPayloadBytes) ||
				(capturedAtUtc == default))
			{
				return false;
			}

			// Utf8JsonReader validates JSON syntax, while this additionally makes
			// invalid UTF-8 fail even when it occurs in an ignored JSON value.
			_ = StrictUtf8.GetCharCount(payload);
			Utf8JsonReader reader = new(
				payload,
				new JsonReaderOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});

			if (!reader.Read() ||
				(reader.TokenType != JsonTokenType.StartObject))
			{
				return false;
			}

			bool sawEmail = false;
			bool sawPlanTier = false;
			bool isPlanTierUsable = true;
			string? email = null;
			string? planTier = null;

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					if (reader.Read())
					{
						return false;
					}

					if (!sawEmail || !IsValidAsciiEmail(email))
					{
						return false;
					}

					string? normalizedPlanTier = null;
					if (isPlanTierUsable)
					{
						_ = TryNormalizePlanTier(
							planTier,
							out normalizedPlanTier);
					}

					accountDisplay = new(
						email!,
						capturedAtUtc.ToUniversalTime(),
						normalizedPlanTier);
					return true;
				}

				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					return false;
				}

				bool isEmail = reader.ValueTextEquals("email");
				bool isPlanTier = reader.ValueTextEquals("plan_tier");

				if (!reader.Read())
				{
					return false;
				}

				if (isEmail)
				{
					if (sawEmail ||
						(reader.TokenType != JsonTokenType.String))
					{
						return false;
					}

					sawEmail = true;
					email = reader.GetString();
				}
				else if (isPlanTier)
				{
					if (sawPlanTier)
					{
						isPlanTierUsable = false;
					}

					sawPlanTier = true;
					if (reader.TokenType == JsonTokenType.String)
					{
						string? candidate = reader.GetString();
						if (TryNormalizePlanTier(candidate, out string? normalized))
						{
							planTier = normalized;
						}
						else
						{
							isPlanTierUsable = false;
						}
					}
					else if (reader.TokenType == JsonTokenType.Null)
					{
						planTier = null;
					}
					else
					{
						isPlanTierUsable = false;
						if ((reader.TokenType == JsonTokenType.StartObject) ||
							(reader.TokenType == JsonTokenType.StartArray))
						{
							reader.Skip();
						}
					}
				}
				else if ((reader.TokenType == JsonTokenType.StartObject) ||
					(reader.TokenType == JsonTokenType.StartArray))
				{
					reader.Skip();
				}
			}
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is DecoderFallbackException) ||
			(exception is InvalidOperationException) ||
			(exception is JsonException))
		{
			// The status-line helper is intentionally silent and fail-closed.
		}

		return false;
	}

	public static bool TryWrite(
		AntigravityStatusLineAccountDisplay accountDisplay)
	{
		return TryWrite(GetCaptureFilePath(), accountDisplay);
	}

	public static bool TryRead(
		out AntigravityStatusLineAccountDisplay? accountDisplay)
	{
		return TryRead(GetCaptureFilePath(), out accountDisplay);
	}

	internal static bool TryWrite(
		string captureFilePath,
		AntigravityStatusLineAccountDisplay accountDisplay)
	{
		return TryWrite(
			captureFilePath,
			accountDisplay,
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			AntigravityStatusLineObservationSignal.TryNotify);
	}

	internal static bool TryWrite(
		string captureFilePath,
		AntigravityStatusLineAccountDisplay accountDisplay,
		Func<string, bool> tryProtectNewFile,
		Action<string>? notifyObservation = null,
		Func<DateTimeOffset>? getUtcNow = null)
	{
		ArgumentNullException.ThrowIfNull(accountDisplay);
		ArgumentNullException.ThrowIfNull(tryProtectNewFile);

		try
		{
			DateTimeOffset nowUtc = (getUtcNow?.Invoke() ??
				DateTimeOffset.UtcNow).ToUniversalTime();

			if (!OperatingSystem.IsWindows() ||
				!IsValidAsciiEmail(accountDisplay.Email) ||
				!TryNormalizePlanTier(accountDisplay.PlanTier, out _) ||
				(accountDisplay.CapturedAtUtc == default) ||
				(nowUtc == default) ||
				IsTooFarInFuture(accountDisplay.CapturedAtUtc, nowUtc) ||
				!Path.IsPathFullyQualified(captureFilePath) ||
				!string.Equals(
					Path.GetFileName(captureFilePath),
					CaptureFileName,
					StringComparison.Ordinal))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(captureFilePath);
			string? privateDirectory = Path.GetDirectoryName(fullPath);

			if ((privateDirectory is null) ||
				!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
					privateDirectory,
					out _,
					out _))
			{
				return false;
			}

			using FileStream? lockStream = TryAcquirePrivateLock(
				privateDirectory,
				tryProtectNewFile);

			if (lockStream is null)
			{
				return false;
			}

			if (TryRead(
					fullPath,
					nowUtc,
					out AntigravityStatusLineAccountDisplay? old))
			{
				bool isSameAccount = string.Equals(
					old!.Email,
					accountDisplay.Email,
					StringComparison.OrdinalIgnoreCase);
				bool isSameDisplay = isSameAccount && string.Equals(
					old.PlanTier,
					accountDisplay.PlanTier,
					StringComparison.Ordinal);
				bool shouldReplaceFutureChangedDisplay =
					!isSameDisplay &&
					(old.CapturedAtUtc > nowUtc) &&
					(accountDisplay.CapturedAtUtc.ToUniversalTime() <= nowUtc);

				if (((old.CapturedAtUtc >= accountDisplay.CapturedAtUtc) &&
						!shouldReplaceFutureChangedDisplay) ||
					(isSameDisplay &&
						(accountDisplay.CapturedAtUtc - old.CapturedAtUtc <
							MinimumSameAccountWriteInterval)))
				{
					if (isSameAccount)
					{
						TryNotifyObservation(
							notifyObservation,
							accountDisplay.Email);
					}

					return true;
				}
			}

			byte[] serialized = Serialize(accountDisplay);
			string temporaryPath = Path.Combine(
				privateDirectory,
				$".{CaptureFileName}.{Guid.NewGuid():N}.tmp");

			try
			{
				using (FileStream stream = new(
					temporaryPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					4096,
					FileOptions.WriteThrough))
				{
					stream.Write(serialized);
					stream.Flush(true);
				}

				if (!tryProtectNewFile(temporaryPath))
				{
					return false;
				}

				if (File.Exists(fullPath))
				{
					if (!AntigravityPrivateKeyAcl.IsPrivateFile(fullPath))
					{
						return false;
					}

					File.Replace(temporaryPath, fullPath, null, true);
				}
				else
				{
					File.Move(temporaryPath, fullPath);
				}

				bool savedSuccessfully =
					AntigravityPrivateKeyAcl.IsPrivateFile(fullPath) &&
					TryRead(
						fullPath,
						nowUtc,
						out AntigravityStatusLineAccountDisplay? saved) &&
					(saved == (accountDisplay with
					{
						CapturedAtUtc = accountDisplay.CapturedAtUtc.ToUniversalTime()
					}));

				if (savedSuccessfully)
				{
					TryNotifyObservation(
						notifyObservation,
						accountDisplay.Email);
				}

				return savedSuccessfully;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(serialized);
				TryDeleteRegularPrivateTemporaryFile(
					temporaryPath,
					privateDirectory);
			}
		}
		catch
		{
			return false;
		}
	}

	private static void TryNotifyObservation(
		Action<string>? notifyObservation,
		string email)
	{
		try
		{
			notifyObservation?.Invoke(email);
		}
		catch
		{
			// Freshness is display-only metadata. A listener failure must not
			// turn a successfully secured capture into a helper failure.
		}
	}

	internal static bool TryRead(
		string captureFilePath,
		out AntigravityStatusLineAccountDisplay? accountDisplay)
	{
		return TryRead(
			captureFilePath,
			DateTimeOffset.UtcNow,
			out accountDisplay);
	}

	internal static bool TryRead(
		string captureFilePath,
		DateTimeOffset nowUtc,
		out AntigravityStatusLineAccountDisplay? accountDisplay)
	{
		accountDisplay = null;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				(nowUtc == default) ||
				!Path.IsPathFullyQualified(captureFilePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(captureFilePath);

			if (!AntigravityPrivateKeyAcl.IsPrivateFile(fullPath))
			{
				return false;
			}

			FileInfo file = new(fullPath);

			if ((file.Length == 0) ||
				(file.Length > MaximumCaptureFileBytes))
			{
				return false;
			}

			byte[] bytes = File.ReadAllBytes(fullPath);

			try
			{
				if (!TryParseCaptureFile(bytes, out accountDisplay) ||
					accountDisplay is null ||
					IsTooFarInFuture(
						accountDisplay.CapturedAtUtc,
						nowUtc.ToUniversalTime()))
				{
					accountDisplay = null;
					return false;
				}

				return true;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}
		catch
		{
			return false;
		}
	}

	private static bool IsTooFarInFuture(
		DateTimeOffset timestamp,
		DateTimeOffset nowUtc)
	{
		DateTimeOffset timestampUtc = timestamp.ToUniversalTime();
		return timestampUtc > nowUtc &&
			(timestampUtc - nowUtc) > MaximumFutureClockSkew;
	}

	private static FileStream? TryAcquirePrivateLock(
		string directoryPath,
		Func<string, bool> tryProtectNewFile)
	{
		string lockPath = Path.Combine(directoryPath, LockFileName);

		for (int attempt = 0; attempt < 10; attempt++)
		{
			try
			{
				if (!File.Exists(lockPath))
				{
					using (FileStream created = new(
						lockPath,
						FileMode.CreateNew,
						FileAccess.Write,
						FileShare.None))
					{
						created.Flush(true);
					}

					if (!tryProtectNewFile(lockPath))
					{
						TryDeleteRegularPrivateOrSafelyInheritedFile(
							lockPath,
							directoryPath);
						return null;
					}
				}

				if (!AntigravityPrivateKeyAcl.IsPrivateFile(lockPath) &&
					(!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
						lockPath) ||
						!tryProtectNewFile(lockPath)))
				{
					return null;
				}

				return new FileStream(
					lockPath,
					FileMode.Open,
					FileAccess.ReadWrite,
					FileShare.None);
			}
			catch (IOException) when (attempt < 9)
			{
				Thread.Sleep(20);
			}
		}

		return null;
	}

	private static byte[] Serialize(
		AntigravityStatusLineAccountDisplay accountDisplay)
	{
		ArrayBufferWriter<byte> buffer = new(256);
		using (Utf8JsonWriter writer = new(buffer))
		{
			writer.WriteStartObject();
			writer.WriteNumber("schemaVersion", SchemaVersion);
			writer.WriteString("email", accountDisplay.Email);
			writer.WriteString(
				"capturedAtUtc",
				accountDisplay.CapturedAtUtc.ToUniversalTime());
			if (accountDisplay.PlanTier is not null)
			{
				writer.WriteString("planTier", accountDisplay.PlanTier);
			}
			writer.WriteEndObject();
		}

		return buffer.WrittenSpan.ToArray();
	}

	private static bool TryParseCaptureFile(
		ReadOnlySpan<byte> bytes,
		out AntigravityStatusLineAccountDisplay? accountDisplay)
	{
		accountDisplay = null;

		try
		{
			_ = StrictUtf8.GetCharCount(bytes);
			Utf8JsonReader reader = new(bytes, new JsonReaderOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = 4
			});

			if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
			{
				return false;
			}

			int? schemaVersion = null;
			string? email = null;
			string? planTier = null;
			DateTimeOffset? capturedAtUtc = null;
			HashSet<string> propertyNames = new(StringComparer.Ordinal);

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					if (reader.Read() ||
						schemaVersion != SchemaVersion ||
						!IsValidAsciiEmail(email) ||
						!TryNormalizePlanTier(
							planTier,
							out string? normalizedPlanTier) ||
						!capturedAtUtc.HasValue ||
						(capturedAtUtc.Value == default))
					{
						return false;
					}

					accountDisplay = new(
						email!,
						capturedAtUtc.Value.ToUniversalTime(),
						normalizedPlanTier);
					return true;
				}

				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					return false;
				}

				string? propertyName = reader.GetString();
				if ((propertyName is null) || !propertyNames.Add(propertyName) ||
					!reader.Read())
				{
					return false;
				}

				switch (propertyName)
				{
					case "schemaVersion" when
						reader.TokenType == JsonTokenType.Number &&
						reader.TryGetInt32(out int value):
						schemaVersion = value;
						break;
					case "email" when
						reader.TokenType == JsonTokenType.String:
						email = reader.GetString();
						break;
					case "capturedAtUtc" when
						reader.TokenType == JsonTokenType.String &&
						reader.TryGetDateTimeOffset(out DateTimeOffset value):
						capturedAtUtc = value;
						break;
					case "planTier" when reader.TokenType == JsonTokenType.String:
						planTier = reader.GetString();
						break;
					case "planTier" when reader.TokenType == JsonTokenType.Null:
						planTier = null;
						break;
					default:
						return false;
				}
			}
		}
		catch (Exception exception) when (
			(exception is DecoderFallbackException) ||
			(exception is InvalidOperationException) ||
			(exception is JsonException))
		{
		}

		return false;
	}

	internal static bool TryNormalizePlanTier(
		string? planTier,
		out string? normalizedPlanTier)
	{
		normalizedPlanTier = null;

		if (planTier is null)
		{
			return true;
		}

		if (string.IsNullOrWhiteSpace(planTier) ||
			(planTier.Length > MaximumPlanTierLength) ||
			!string.Equals(planTier, planTier.Trim(), StringComparison.Ordinal) ||
			planTier.Any(char.IsControl))
		{
			return false;
		}

		normalizedPlanTier = planTier;
		return true;
	}

	internal static bool IsValidAsciiEmail(string? email)
	{
		if (string.IsNullOrEmpty(email) || (email.Length > 320))
		{
			return false;
		}

		foreach (char character in email)
		{
			if ((character < '!') || (character > '~'))
			{
				return false;
			}
		}

		int atIndex = email.IndexOf('@');
		if ((atIndex <= 0) ||
			(atIndex != email.LastIndexOf('@')) ||
			(atIndex > 64) ||
			(atIndex == email.Length - 1))
		{
			return false;
		}

		ReadOnlySpan<char> local = email.AsSpan(0, atIndex);
		ReadOnlySpan<char> domain = email.AsSpan(atIndex + 1);
		const string localPunctuation = ".!#$%&'*+-/=?^_`{|}~";

		if ((local[0] == '.') || (local[^1] == '.') ||
			local.Contains("..", StringComparison.Ordinal) ||
			(domain.Length > 253) ||
			(domain[0] == '.') || (domain[^1] == '.') ||
			domain.Contains("..", StringComparison.Ordinal))
		{
			return false;
		}

		foreach (char character in local)
		{
			if (!char.IsAsciiLetterOrDigit(character) &&
				!localPunctuation.Contains(character, StringComparison.Ordinal))
			{
				return false;
			}
		}

		int labelStart = 0;
		for (int index = 0; index <= domain.Length; index++)
		{
			if ((index < domain.Length) && (domain[index] != '.'))
			{
				continue;
			}

			ReadOnlySpan<char> label = domain[labelStart..index];
			if ((label.Length == 0) || (label.Length > 63) ||
				(label[0] == '-') || (label[^1] == '-'))
			{
				return false;
			}

			foreach (char character in label)
			{
				if (!char.IsAsciiLetterOrDigit(character) &&
					(character != '-'))
				{
					return false;
				}
			}

			labelStart = index + 1;
		}

		return true;
	}

	private static void TryDeleteRegularPrivateTemporaryFile(
		string temporaryPath,
		string expectedDirectory)
	{
		TryDeleteRegularPrivateOrSafelyInheritedFile(
			temporaryPath,
			expectedDirectory);
	}

	private static void TryDeleteRegularPrivateOrSafelyInheritedFile(
		string filePath,
		string expectedDirectory)
	{
		try
		{
			if (File.Exists(filePath) &&
				string.Equals(
					Path.GetDirectoryName(Path.GetFullPath(filePath)),
					Path.GetFullPath(expectedDirectory),
					StringComparison.OrdinalIgnoreCase) &&
				(AntigravityPrivateKeyAcl.IsPrivateFile(filePath) ||
					AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
						filePath)))
			{
				File.Delete(filePath);
			}
		}
		catch
		{
		}
	}
}
