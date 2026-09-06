using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravitySetupApprovalReceiptStoreTests
{
	[Fact]
	public async Task RoundTrip_IsIdempotentAndNormalizesIdentityCase()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupApprovalReceiptStore store = new(
			Path.Combine(temporaryDirectory.Path, "receipts"));
		Guid attemptId = Guid.NewGuid();

		await store.WriteAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.ReviewedConPty,
			"Person@Example.COM");
		await store.WriteAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.ReviewedConPty,
			"person@example.com");

		AntigravitySetupApprovalReceipt receipt = Assert.IsType<
			AntigravitySetupApprovalReceipt>(await store.ReadAsync(attemptId));
		Assert.Equal(attemptId, receipt.AttemptId);
		Assert.Equal(
			AntigravityMachineSetupSourceKind.ReviewedConPty,
			receipt.SourceKind);
		Assert.Equal(
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint("PERSON@example.com"),
			receipt.TargetIdentityFingerprint);
	}

	[Fact]
	public async Task WriteAsync_WhenAttemptContentConflicts_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupApprovalReceiptStore store = new(
			Path.Combine(temporaryDirectory.Path, "receipts"));
		Guid attemptId = Guid.NewGuid();
		await store.WriteAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			"agy.local-session.v1");

		await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.ReviewedConPty,
			"other@example.com"));

		AntigravitySetupApprovalReceipt receipt = Assert.IsType<
			AntigravitySetupApprovalReceipt>(await store.ReadAsync(attemptId));
		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			receipt.SourceKind);
	}

	[Theory]
	[InlineData(
		"{\"schemaVersion\":1,\"attemptId\":\"{0}\",\"sourceKind\":1,\"targetIdentityFingerprint\":\"{1}\",\"extra\":true}")]
	[InlineData(
		"{\"schemaVersion\":1,\"attemptId\":\"00000000-0000-0000-0000-000000000001\",\"sourceKind\":1,\"targetIdentityFingerprint\":\"{1}\"}")]
	[InlineData(
		"{\"schemaVersion\":99,\"attemptId\":\"{0}\",\"sourceKind\":1,\"targetIdentityFingerprint\":\"{1}\"}")]
	public async Task ReadAsync_WithInvalidDocument_FailsClosed(
		string documentTemplate)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string receiptDirectory = Path.Combine(
			temporaryDirectory.Path,
			"receipts");
		Directory.CreateDirectory(receiptDirectory);
		Guid attemptId = Guid.NewGuid();
		string fingerprint = new('A', 64);
		string document = documentTemplate
			.Replace("{0}", attemptId.ToString(), StringComparison.Ordinal)
			.Replace("{1}", fingerprint, StringComparison.Ordinal);
		await File.WriteAllTextAsync(
			Path.Combine(receiptDirectory, $"{attemptId:N}.json"),
			document);
		AntigravitySetupApprovalReceiptStore store = new(receiptDirectory);

		await Assert.ThrowsAsync<InvalidDataException>(
			() => store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task RemoveAsync_IsIdempotentAndLeavesMissingReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupApprovalReceiptStore store = new(
			Path.Combine(temporaryDirectory.Path, "receipts"));
		Guid attemptId = Guid.NewGuid();
		await store.WriteAsync(
			attemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			"agy.local-session.v1");

		await store.RemoveAsync(attemptId);
		await store.RemoveAsync(attemptId);

		Assert.Null(await store.ReadAsync(attemptId));
	}

	[Fact]
	public async Task RemoveUnreferencedAsync_RemovesOnlyCanonicalOrphans()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string receiptDirectory = Path.Combine(
			temporaryDirectory.Path,
			"receipts");
		AntigravitySetupApprovalReceiptStore store = new(receiptDirectory);
		Guid activeAttemptId = Guid.NewGuid();
		Guid orphanedAttemptId = Guid.NewGuid();
		await store.WriteAsync(
			activeAttemptId,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			"agy.local-session.v1");
		await store.WriteAsync(
			orphanedAttemptId,
			AntigravityMachineSetupSourceKind.ReviewedConPty,
			"orphan@example.com");
		string unknownFilePath = Path.Combine(
			receiptDirectory,
			"not-owned.json");
		await File.WriteAllTextAsync(unknownFilePath, "{}");

		await store.RemoveUnreferencedAsync(
			new HashSet<Guid> { activeAttemptId });

		Assert.NotNull(await store.ReadAsync(activeAttemptId));
		Assert.Null(await store.ReadAsync(orphanedAttemptId));
		Assert.True(File.Exists(unknownFilePath));
	}

	[Fact]
	public async Task PublicOperations_RejectInvalidArguments()
	{
		using TemporaryDirectory temporaryDirectory = new();
		AntigravitySetupApprovalReceiptStore store = new(
			Path.Combine(temporaryDirectory.Path, "receipts"));

		await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(
			Guid.Empty,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			"agy.local-session.v1"));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.ReadAsync(Guid.Empty));
		await Assert.ThrowsAsync<ArgumentException>(
			() => store.RemoveAsync(Guid.Empty));
		Assert.Throws<ArgumentException>(() =>
			AntigravitySetupApprovalReceiptStore
				.ComputeTargetIdentityFingerprint(" "));
	}
}
