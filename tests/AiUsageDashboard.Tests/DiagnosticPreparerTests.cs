using System.Text;
using System.Xml;

using AiUsageDashboard.PrivacyCheck;

namespace AiUsageDashboard.Tests;

public sealed class DiagnosticPreparerTests
{
	[Fact]
	public void Prepare_CleanReports_StagesOnlyAllowlistedFilesWithOriginalBytes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string coveragePath = Path.Combine(inputPath, "coverage", "coverage.cobertura.xml");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(Path.GetDirectoryName(coveragePath)!);
		byte[] reportBytes = Encoding.UTF8.GetPreamble()
			.Concat(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun><Result>合成結果</Result></TestRun>"))
			.ToArray();
		byte[] coverageBytes = Encoding.UTF8.GetBytes("<coverage line-rate=\"1\"><sources><source>D:/a/source</source></sources></coverage>");
		File.WriteAllBytes(Path.Combine(inputPath, "test-results.trx"), reportBytes);
		File.WriteAllBytes(coveragePath, coverageBytes);
		File.WriteAllBytes(Path.Combine(inputPath, "attachment.bin"), [0, 255, 12]);
		File.WriteAllText(Path.Combine(inputPath, "console.log"), CreateSyntheticKey());
		File.WriteAllText(Path.Combine(inputPath, "coverage", "test-results.trx"), "not selected");

		IReadOnlyList<string> preparedFiles = DiagnosticPreparer.Prepare(inputPath, outputPath);

		Assert.Equal(new[] { "coverage/coverage.cobertura.xml", "test-results.trx" }, preparedFiles);
		Assert.Equal(reportBytes, File.ReadAllBytes(Path.Combine(outputPath, "test-results.trx")));
		Assert.Equal(coverageBytes, File.ReadAllBytes(Path.Combine(outputPath, "coverage", "coverage.cobertura.xml")));
		Assert.Equal(reportBytes, File.ReadAllBytes(Path.Combine(inputPath, "test-results.trx")));
		Assert.Equal(2, Directory.GetFiles(outputPath, "*", SearchOption.AllDirectories).Length);
		Assert.Empty(Directory.GetDirectories(temporaryDirectory.Path, ".diagnostics-staging-*"));
	}

	[Fact]
	public void Prepare_WithSecretAfterCleanReport_RejectsWithoutPartialOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		string syntheticKey = CreateSyntheticKey();
		File.WriteAllText(Path.Combine(inputPath, "a.cobertura.xml"), "<coverage />");
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), $"<TestRun>\n<Result>{syntheticKey}</Result></TestRun>");

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("test-results.trx", exception.RelativePath);
		Assert.Equal(2, exception.LineNumber);
		Assert.DoesNotContain(syntheticKey, exception.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(outputPath));
		Assert.Empty(Directory.GetDirectories(temporaryDirectory.Path, ".diagnostics-staging-*"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Prepare_WithNumericXmlEntities_RejectsDecodedSecrets(bool inAttribute)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		string syntheticKey = CreateSyntheticKey();
		string encodedKey = string.Concat(syntheticKey.Select(character => $"&#x{(int)character:X};"));
		string xml = inAttribute ? $"<TestRun result=\"{encodedKey}\" />" : $"<TestRun>{encodedKey}</TestRun>";
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), xml);

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("test-results.trx", exception.RelativePath);
		Assert.DoesNotContain(syntheticKey, exception.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(outputPath));
	}

	[Theory]
	[InlineData("<!DOCTYPE TestRun [<!ENTITY value 'synthetic'>]><TestRun>&value;</TestRun>")]
	[InlineData("<!DOCTYPE TestRun SYSTEM 'file:///synthetic-private-file'><TestRun />")]
	[InlineData("<TestRun><Result></TestRun>")]
	public void Prepare_WithDtdOrInvalidXml_RejectsWithSafeErrorAndPreservesCause(string xml)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), xml);

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.IsType<XmlException>(exception.InnerException);
		Assert.Equal("invalid or prohibited diagnostic XML", exception.Rule);
		Assert.DoesNotContain(xml, exception.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithPersonalData_RejectsEvenWhenXmlEncoded()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		string personalPath = "C:" + "/Users/" + "DiagnosticFixturePrivate" + "/result.txt";
		string encodedPath = string.Concat(personalPath.Select(character => $"&#{(int)character};"));
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), $"<TestRun>{encodedPath}</TestRun>");

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.DoesNotContain(personalPath, exception.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithSecretSplitAcrossCdata_RejectsDecodedText()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		string syntheticKey = CreateSyntheticKey();
		string xml = $"<TestRun>{syntheticKey[..1]}<![CDATA[{syntheticKey[1..2]}]]>{syntheticKey[2..]}</TestRun>";
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), xml);

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.DoesNotContain(syntheticKey, exception.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithSensitiveFilename_RedactsPathInError()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		string syntheticKey = CreateSyntheticKey();
		File.WriteAllText(Path.Combine(inputPath, syntheticKey + ".cobertura.xml"), "<coverage />");

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("[redacted-path]", exception.RelativePath);
		Assert.DoesNotContain(syntheticKey, exception.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithMissingInput_ReturnsNoFilesWithoutCreatingOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");

		IReadOnlyList<string> preparedFiles = DiagnosticPreparer.Prepare(
			Path.Combine(temporaryDirectory.Path, "missing"), outputPath);

		Assert.Empty(preparedFiles);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithSensitiveArtifactDisguisedAsCoverage_RejectsWithoutOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		File.WriteAllText(Path.Combine(inputPath, ".env.cobertura.xml"), "<coverage />");

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("sensitive artifact filename", exception.Rule);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithOnlyUnselectedFiles_ReturnsNoFilesWithoutCreatingOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		File.WriteAllBytes(Path.Combine(inputPath, "attachment.dmp"), [0, 255, 12]);

		IReadOnlyList<string> preparedFiles = DiagnosticPreparer.Prepare(inputPath, outputPath);

		Assert.Empty(preparedFiles);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithExistingOutput_RejectsAndPreservesExistingFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(outputPath);
		string sentinelPath = Path.Combine(outputPath, "existing.txt");
		File.WriteAllText(sentinelPath, "keep existing content");

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(Path.Combine(temporaryDirectory.Path, "missing"), outputPath));

		Assert.Equal("output already exists", exception.Rule);
		Assert.Equal("keep existing content", File.ReadAllText(sentinelPath));
	}

	[Fact]
	public async Task Prepare_WithJunction_RejectsWithoutReadingOrChangingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string targetPath = Path.Combine(temporaryDirectory.Path, "target");
		string junctionPath = Path.Combine(inputPath, "linked");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		Directory.CreateDirectory(targetPath);
		string sentinelPath = Path.Combine(targetPath, "test-results.trx");
		File.WriteAllText(sentinelPath, "preserve target");
		await JunctionTestHelper.CreateAsync(junctionPath, targetPath);
		try
		{
			DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
				DiagnosticPreparer.Prepare(inputPath, outputPath));

			Assert.Equal("reparse points are prohibited", exception.Rule);
			Assert.Equal("preserve target", File.ReadAllText(sentinelPath));
			Assert.False(Directory.Exists(outputPath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}

	[Fact]
	public void Prepare_WithOversizedSelectedFile_RejectsBeforeReadingContent()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		using (FileStream stream = File.Create(Path.Combine(inputPath, "test-results.trx")))
		{
			stream.SetLength((64 * 1024 * 1024) + 1);
		}

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("diagnostic file exceeds 64 MiB", exception.Rule);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithTooManySelectedFiles_RejectsWithoutOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		for (int fileIndex = 0; fileIndex < 101; fileIndex++)
		{
			File.WriteAllText(Path.Combine(inputPath, $"{fileIndex}.cobertura.xml"), "<coverage />");
		}

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("diagnostic file count exceeds 100", exception.Rule);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public void Prepare_WithExcessiveXmlDepth_RejectsWithoutOutput()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		string xml = string.Concat(Enumerable.Repeat("<node>", 130)) +
			string.Concat(Enumerable.Repeat("</node>", 130));
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), xml);

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("diagnostic XML depth exceeds 128", exception.Rule);
		Assert.False(Directory.Exists(outputPath));
	}

	[Fact]
	public async Task Prepare_WithOutputParentJunction_RejectsWithoutChangingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string targetPath = Path.Combine(temporaryDirectory.Path, "target");
		string junctionPath = Path.Combine(temporaryDirectory.Path, "linked");
		Directory.CreateDirectory(inputPath);
		Directory.CreateDirectory(targetPath);
		File.WriteAllText(Path.Combine(inputPath, "test-results.trx"), "<TestRun />");
		await JunctionTestHelper.CreateAsync(junctionPath, targetPath);
		try
		{
			DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
				DiagnosticPreparer.Prepare(inputPath, Path.Combine(junctionPath, "output")));

			Assert.Equal("reparse points are prohibited", exception.Rule);
			Assert.Empty(Directory.GetFileSystemEntries(targetPath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}

	[Fact]
	public void Prepare_WithBinarySelectedFile_RejectsInsteadOfSilentlySkipping()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string inputPath = Path.Combine(temporaryDirectory.Path, "input");
		string outputPath = Path.Combine(temporaryDirectory.Path, "output");
		Directory.CreateDirectory(inputPath);
		File.WriteAllBytes(Path.Combine(inputPath, "test-results.trx"), [0, 255, 12]);

		DiagnosticPreparer.PreparationException exception = Assert.Throws<DiagnosticPreparer.PreparationException>(() =>
			DiagnosticPreparer.Prepare(inputPath, outputPath));

		Assert.Equal("diagnostic file is not supported text", exception.Rule);
		Assert.False(Directory.Exists(outputPath));
	}

	private static string CreateSyntheticKey()
	{
		return "s" + "k-" + new string('Z', 32);
	}
}
