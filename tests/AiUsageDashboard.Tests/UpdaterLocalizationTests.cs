using System.Diagnostics;
using System.Globalization;
using System.Text;

using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

using UpdaterProgram = AiUsageDashboard.Updater.Program;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterLocalizationTests
{
	[Theory]
	[InlineData("English", true, UpdaterDisplayLanguage.English)]
	[InlineData("TraditionalChinese", true, UpdaterDisplayLanguage.TraditionalChinese)]
	[InlineData(null, false, UpdaterDisplayLanguage.English)]
	[InlineData("", false, UpdaterDisplayLanguage.English)]
	[InlineData("english", false, UpdaterDisplayLanguage.English)]
	[InlineData(" English ", false, UpdaterDisplayLanguage.English)]
	[InlineData("zh-TW", false, UpdaterDisplayLanguage.English)]
	[InlineData("0", false, UpdaterDisplayLanguage.English)]
	[InlineData("English,TraditionalChinese", false, UpdaterDisplayLanguage.English)]
	public void LanguageContractAcceptsOnlyCanonicalNames(
		string? value,
		bool expectedSuccess,
		UpdaterDisplayLanguage expectedLanguage)
	{
		Assert.Equal(expectedSuccess, UpdaterDisplayLanguageContract.TryParse(value, out UpdaterDisplayLanguage language));
		Assert.Equal(expectedLanguage, language);
	}

	[Theory]
	[InlineData("English", UpdaterDisplayLanguage.English)]
	[InlineData("TraditionalChinese", UpdaterDisplayLanguage.TraditionalChinese)]
	public void ValidEnvironmentLanguageDoesNotReadUnavailablePreferences(
		string environmentLanguage,
		UpdaterDisplayLanguage expectedLanguage)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path, "{not-json");
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using FileStream exclusiveLock = new(preferencesPath, FileMode.Open, FileAccess.Read, FileShare.None);
		using StringWriter diagnostics = new();

		Assert.Equal(expectedLanguage, UpdaterDisplayLanguageResolver.Resolve(directory.Path, environmentLanguage, diagnostics));
		Assert.Equal(string.Empty, diagnostics.ToString());
		exclusiveLock.Dispose();
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Theory]
	[InlineData("")]
	[InlineData("zh-TW")]
	[InlineData("English,TraditionalChinese")]
	public void InvalidEnvironmentFallsBackToSavedLanguageWithDiagnostics(string environmentLanguage)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path,
			"""{"schemaVersion":8,"language":"TraditionalChinese","unchanged":"synthetic-settings"}""");
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.TraditionalChinese,
			UpdaterDisplayLanguageResolver.Resolve(directory.Path, environmentLanguage, diagnostics));
		Assert.Contains(UpdaterDisplayLanguageContract.LanguageEnvironmentVariableName, diagnostics.ToString());
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Theory]
	[InlineData("English", UpdaterDisplayLanguage.English)]
	[InlineData("TraditionalChinese", UpdaterDisplayLanguage.TraditionalChinese)]
	public void SavedLanguageIsReadWithoutMutatingSettingsOrProcessCultures(
		string savedLanguage,
		UpdaterDisplayLanguage expectedLanguage)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path,
			$$"""{"schemaVersion":8,"language":"{{savedLanguage}}","theme":"Midnight","unknown":"preserve me"}""");
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		CultureInfo originalCulture = CultureInfo.CurrentCulture;
		CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
		using StringWriter diagnostics = new();

		UpdaterDisplayLanguage language = UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics);
		_ = UpdaterText.ForLanguage(language).UpdateCompleted("1.0.18+synthetic");

		Assert.Equal(expectedLanguage, language);
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
		Assert.Equal(string.Empty, diagnostics.ToString());
		Assert.Same(originalCulture, CultureInfo.CurrentCulture);
		Assert.Same(originalUiCulture, CultureInfo.CurrentUICulture);
	}

	[Theory]
	[InlineData("{\"schemaVersion\":7,\"theme\":\"ClassicBlue\"}")]
	[InlineData("{\"schemaVersion\":7,\"language\":\"TraditionalChinese\"}")]
	[InlineData("{\"schemaVersion\":8}")]
	[InlineData("{\"language\":\"TraditionalChinese\"}")]
	public void LegacyOrMissingLanguageDefaultsToEnglishWithoutChangingFile(string document)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path, document);
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Equal(string.Empty, diagnostics.ToString());
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Fact]
	public void MissingPreferencesDefaultsToEnglishWithoutCreatingFiles()
	{
		using TemporaryDirectory directory = new();
		using StringWriter diagnostics = new();
		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
		Assert.Equal(string.Empty, diagnostics.ToString());
	}

	[Fact]
	public void FutureSchemaReadsOnlyKnownLanguageWithoutRewritingTheDocument()
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path,
			"""{"schemaVersion":99,"language":"TraditionalChinese","future":{"unknown":"preserve me"}}""");
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.TraditionalChinese,
			UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Equal(string.Empty, diagnostics.ToString());
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Theory]
	[InlineData("null")]
	[InlineData("1")]
	[InlineData("\"Unknown\"")]
	[InlineData("\"english\"")]
	public void InvalidSavedLanguageDefaultsToEnglishAndReportsOnlyReadContext(string languageJson)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path,
			$$"""{"schemaVersion":8,"language":{{languageJson}},"syntheticPrivateField":"do-not-print-this"}""");
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Contains(preferencesPath, diagnostics.ToString());
		Assert.Contains("using English", diagnostics.ToString());
		Assert.DoesNotContain("do-not-print-this", diagnostics.ToString());
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{")]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("{\"schemaVersion\":\"8\",\"language\":\"TraditionalChinese\"}")]
	[InlineData("{\"schemaVersion\":-1,\"language\":\"TraditionalChinese\"}")]
	public void InvalidPreferencesUseEnglishAndPreserveOriginalBytes(string document)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path, document);
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Contains(preferencesPath, diagnostics.ToString());
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Fact]
	public void OversizedPreferencesAreRejectedWithoutChangingFile()
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path,
			new string(' ', UpdaterDisplayLanguageResolver.MaximumPreferencesSizeBytes + 1));
		byte[] originalBytes = File.ReadAllBytes(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Contains("exceeds 65536 bytes", diagnostics.ToString());
		Assert.Equal(originalBytes, File.ReadAllBytes(preferencesPath));
	}

	[Fact]
	public void PreferencesAtSizeLimitRemainReadable()
	{
		using TemporaryDirectory directory = new();
		const string document = """{"schemaVersion":8,"language":"TraditionalChinese"}""";
		string paddedDocument = document + new string(' ',
			UpdaterDisplayLanguageResolver.MaximumPreferencesSizeBytes - Encoding.UTF8.GetByteCount(document));
		string preferencesPath = WritePreferences(directory.Path, paddedDocument);
		using StringWriter diagnostics = new();

		Assert.Equal((long)UpdaterDisplayLanguageResolver.MaximumPreferencesSizeBytes, new FileInfo(preferencesPath).Length);
		Assert.Equal(UpdaterDisplayLanguage.TraditionalChinese, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Equal(string.Empty, diagnostics.ToString());
	}

	[Fact]
	public void ExcessiveJsonDepthUsesEnglishWithObservableFailure()
	{
		using TemporaryDirectory directory = new();
		string document = "{\"language\":\"TraditionalChinese\",\"unknown\":" + new string('[', 64) + "0" + new string(']', 64) + "}";
		string preferencesPath = WritePreferences(directory.Path, document);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Contains(preferencesPath, diagnostics.ToString());
		Assert.Equal(document, File.ReadAllText(preferencesPath));
	}

	[Fact]
	public void UnavailablePreferencesUseEnglishAndReportTheOperation()
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path, """{"language":"TraditionalChinese"}""");
		using FileStream exclusiveLock = new(preferencesPath, FileMode.Open, FileAccess.Read, FileShare.None);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Contains(preferencesPath, diagnostics.ToString());
		Assert.Contains("Could not read", diagnostics.ToString());
	}

	[Fact]
	public void PreferencesDirectoryIsNotOpenedAsAFile()
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = Path.Combine(directory.Path, "preferences.json");
		Directory.CreateDirectory(preferencesPath);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.English, UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Contains(preferencesPath, diagnostics.ToString());
		Assert.Empty(Directory.EnumerateFileSystemEntries(preferencesPath));
	}

	[Theory]
	[InlineData("{\"schemaVersion\":8,\"language\":\"English\"}")]
	[InlineData("{")]
	public void DisplayLanguageReadReleasesPreferencesBeforeReplacement(string originalDocument)
	{
		using TemporaryDirectory directory = new();
		string preferencesPath = WritePreferences(directory.Path, originalDocument);
		using StringWriter firstDiagnostics = new();
		Assert.Equal(UpdaterDisplayLanguage.English,
			UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, firstDiagnostics));
		string replacementPath = Path.Combine(directory.Path, "replacement.json");
		File.WriteAllText(replacementPath, """{"schemaVersion":8,"language":"TraditionalChinese"}""", new UTF8Encoding(false));
		File.Move(replacementPath, preferencesPath, overwrite: true);
		using StringWriter diagnostics = new();

		Assert.Equal(UpdaterDisplayLanguage.TraditionalChinese,
			UpdaterDisplayLanguageResolver.Resolve(directory.Path, null, diagnostics));
		Assert.Equal(string.Empty, diagnostics.ToString());
	}

	[Theory]
	[InlineData(UpdaterDisplayLanguage.English)]
	[InlineData(UpdaterDisplayLanguage.TraditionalChinese)]
	public void UpdateResultMessagesUseOneLanguageAndPreserveVersionStrings(UpdaterDisplayLanguage language)
	{
		const string version = "1.0.18+synthetic.opaque";
		UpdaterText text = UpdaterText.ForLanguage(language);
		string[] messages =
		[
			text.DialogTitle,
			text.UpdateCompleted(version),
			text.UpdateCompletedAndRestarted(version),
			text.AlreadyCurrent,
			text.AlreadyCurrentAndStarted,
			text.DelegatedUpdateCompleted,
			text.DelegatedUpdateFailed,
			text.UpdateCancelled,
			text.CanonicalUpdaterNotCurrent
		];

		foreach (string message in messages)
		{
			Assert.False(string.IsNullOrWhiteSpace(message));
			Assert.Equal(language == UpdaterDisplayLanguage.TraditionalChinese, ContainsChinese(message));
		}
		Assert.Contains(version, text.UpdateCompleted(version));
		Assert.Contains(version, text.UpdateCompletedAndRestarted(version));
		Assert.Equal("AI Usage 1.0.18+synthetic.opaque has been updated and restarted.",
			UpdaterText.ForLanguage(UpdaterDisplayLanguage.English).UpdateCompletedAndRestarted(version));
		Assert.Equal("AI Usage 1.0.18+synthetic.opaque 已更新並重新啟動。",
			UpdaterText.ForLanguage(UpdaterDisplayLanguage.TraditionalChinese).UpdateCompletedAndRestarted(version));
	}

	[Theory]
	[InlineData(UpdaterDisplayLanguage.English)]
	[InlineData(UpdaterDisplayLanguage.TraditionalChinese)]
	public void FailureAndWarningMessagesPreserveOpaqueDetailsExactly(UpdaterDisplayLanguage language)
	{
		const string details = "opaque provider retry cue.\r\nD:/synthetic/中文/app.exe (detail-id=42)";
		const string shortcutPath = "D:/synthetic/中文/AI Usage.lnk";
		UpdaterText text = UpdaterText.ForLanguage(language);
		string[] messages =
		[
			text.UpdateCouldNotStart(details),
			text.UpdateFailed(details),
			text.RestartFailed(details),
			text.CurrentAppStartFailed(details),
			text.RegistrationFailed(details),
			text.ParentIdentityUnavailable(details),
			text.CanonicalUpdaterVerificationFailed(details),
			text.PromotionSchedulingFailed(details),
			text.PendingPromotionRetryFailed(details),
			text.ShortcutCreationFailed(shortcutPath, details)
		];

		foreach (string message in messages)
		{
			Assert.EndsWith(details, message, StringComparison.Ordinal);
			string prefix = message[..^details.Length];
			if (!prefix.Contains(shortcutPath, StringComparison.Ordinal))
			{
				Assert.Equal(language == UpdaterDisplayLanguage.TraditionalChinese, ContainsChinese(prefix));
			}
		}
		Assert.Contains(shortcutPath, messages[^1]);
		Assert.Contains(shortcutPath, text.ShortcutConflict(shortcutPath));
	}

	[Theory]
	[InlineData(UpdaterDisplayLanguage.English)]
	[InlineData(UpdaterDisplayLanguage.TraditionalChinese)]
	public void ShortcutRegistrationFailureUsesSelectedLanguageAndKeepsRawDetails(UpdaterDisplayLanguage language)
	{
		const string details = "synthetic failure\r\nopaque diagnostics";
		ManagedStartMenuShortcut shortcut = new(
			() => throw new IOException(details),
			displayLanguage: language);

		string warning = Assert.IsType<string>(shortcut.EnsurePresent("D:/synthetic/app"));
		Assert.EndsWith(details, warning, StringComparison.Ordinal);
		Assert.Equal(language == UpdaterDisplayLanguage.TraditionalChinese, ContainsChinese(warning));
		Assert.Contains(ManagedStartMenuShortcut.ShortcutFileName, warning);
	}

	[Theory]
	[InlineData(UpdaterDisplayLanguage.English)]
	[InlineData(UpdaterDisplayLanguage.TraditionalChinese)]
	public void DelegationTransmitsLanguageOnlyInChildEnvironmentAndKeepsLegacyArguments(UpdaterDisplayLanguage language)
	{
		using TemporaryDirectory directory = new();
		Uri feedUri = new("https://downloads.example.test/stable.json");
		UpdaterCommandLineOptions options = UpdaterCommandLine.Parse(
			[], directory.Path, directory.Path, feedUri, "stable") with
		{
			DisplayLanguage = language
		};
		string? originalEnvironment = Environment.GetEnvironmentVariable(UpdaterDisplayLanguageContract.LanguageEnvironmentVariableName);
		CultureInfo originalCulture = CultureInfo.CurrentCulture;
		CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

		ProcessStartInfo startInfo = UpdaterProgram.CreateDelegatedUpdaterStartInfo(
			Path.Combine(directory.Path, "AiUsageDashboard.Updater.exe"), options);

		Assert.False(startInfo.UseShellExecute);
		Assert.Equal(language.ToString(), startInfo.Environment[UpdaterDisplayLanguageContract.LanguageEnvironmentVariableName]);
		Assert.Equal(originalEnvironment, Environment.GetEnvironmentVariable(UpdaterDisplayLanguageContract.LanguageEnvironmentVariableName));
		Assert.Same(originalCulture, CultureInfo.CurrentCulture);
		Assert.Same(originalUiCulture, CultureInfo.CurrentUICulture);
		Assert.DoesNotContain("--language", startInfo.ArgumentList);
		Assert.DoesNotContain("--locale", startInfo.ArgumentList);
		UpdaterCommandLineOptions childOptions = UpdaterCommandLine.Parse(
			startInfo.ArgumentList.ToArray(), directory.Path, directory.Path, feedUri, options.Channel);
		Assert.False(childOptions.ShouldNotifyUser);
		Assert.False(childOptions.ShouldRefreshUpdater);
		Assert.True(childOptions.ShouldPromptForLicenses);
		Assert.Equal(UpdaterDisplayLanguage.English, childOptions.DisplayLanguage);
	}

	[Fact]
	public void LanguageSwitchingHasNoSharedMutablePresentationState()
	{
		UpdaterText english = UpdaterText.ForLanguage(UpdaterDisplayLanguage.English);
		string originalEnglish = english.UpdateCompletedAndRestarted("1.0.18");
		UpdaterText traditionalChinese = UpdaterText.ForLanguage(UpdaterDisplayLanguage.TraditionalChinese);
		Assert.NotEqual(originalEnglish, traditionalChinese.UpdateCompletedAndRestarted("1.0.18"));
		Assert.Equal(originalEnglish, UpdaterText.ForLanguage(UpdaterDisplayLanguage.English).UpdateCompletedAndRestarted("1.0.18"));
		Assert.Equal(originalEnglish, english.UpdateCompletedAndRestarted("1.0.18"));
	}

	[Fact]
	public void UndefinedLanguageFailsAtPresentationAndDelegationBoundaries()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => UpdaterText.ForLanguage((UpdaterDisplayLanguage)int.MaxValue));
		using TemporaryDirectory directory = new();
		UpdaterCommandLineOptions options = UpdaterCommandLine.Parse(
			[], directory.Path, directory.Path, new Uri("https://downloads.example.test/stable.json"), "stable") with
		{
			DisplayLanguage = (UpdaterDisplayLanguage)int.MaxValue
		};
		Assert.Throws<ArgumentOutOfRangeException>(() => UpdaterProgram.CreateDelegatedUpdaterStartInfo(
			Path.Combine(directory.Path, "AiUsageDashboard.Updater.exe"), options));
	}

	private static string WritePreferences(string userDataRoot, string document)
	{
		string path = Path.Combine(userDataRoot, "preferences.json");
		File.WriteAllText(path, document, new UTF8Encoding(false));
		return path;
	}

	private static bool ContainsChinese(string message)
	{
		return message.Any(character => (character >= '\u4e00') && (character <= '\u9fff'));
	}
}
