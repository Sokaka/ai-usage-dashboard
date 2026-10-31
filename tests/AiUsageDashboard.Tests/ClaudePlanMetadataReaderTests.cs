using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudePlanMetadataReaderTests
{
	private sealed class CapturingTraceListener : TraceListener
	{
		internal ConcurrentQueue<string> Messages { get; } = new();

		public override void Write(string? message)
		{
			Messages.Enqueue(message ?? string.Empty);
		}

		public override void WriteLine(string? message)
		{
			Write(message);
		}
	}

	[Theory]
	[InlineData("default_claude_max_5x", "max")]
	[InlineData("default_claude_max_20x", "ultra")]
	public async Task ReadDisplayPlanAsync_WithVerifiedMaxAndKnownTier_ReturnsDisplayOnlyHint(
		string rateLimitTier,
		string expectedDisplayPlan)
	{
		using TemporaryDirectory directory = new();
		byte[] originalBytes = Encoding.UTF8.GetBytes(
			"{\"claudeAiOauth\":{\"accessToken\":\"synthetic-secret\"," +
			"\"subscriptionType\":\"max\",\"rateLimitTier\":\"" + rateLimitTier + "\"}}");
		string credentialsPath = Path.Combine(directory.Path, ".credentials.json");
		await File.WriteAllBytesAsync(credentialsPath, originalBytes);
		ClaudeSubscriptionContext context = CreateContext();

		string? result = await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, context, CancellationToken.None);

		Assert.Equal(expectedDisplayPlan, result);
		Assert.Equal("max", context.PlanTier);
		Assert.Equal(originalBytes, await File.ReadAllBytesAsync(credentialsPath));
	}

	[Theory]
	[InlineData("pro", SubscriptionVerificationState.Verified)]
	[InlineData("team", SubscriptionVerificationState.Verified)]
	[InlineData("ultra", SubscriptionVerificationState.Verified)]
	[InlineData("max", SubscriptionVerificationState.Unverified)]
	[InlineData("max", SubscriptionVerificationState.DefiniteScopeMismatch)]
	public async Task ReadDisplayPlanAsync_WithIneligibleContext_DoesNotReadMetadata(
		string planTier,
		SubscriptionVerificationState verificationState)
	{
		using TemporaryDirectory directory = new();
		string credentialsPath = await WriteCredentialsAsync(
			directory.Path,
			"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}");
		using FileStream lockedCredentials = new(
			credentialsPath, FileMode.Open, FileAccess.Read, FileShare.None);
		ClaudeSubscriptionContext context = CreateContext() with
		{
			PlanTier = planTier,
			VerificationState = verificationState
		};

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, context, CancellationToken.None));
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("{\"claudeAiOauth\":null}")]
	[InlineData("{\"claudeAiOauth\":[]}")]
	[InlineData("{\"claudeAiOauth\":{}}")]
	[InlineData("{\"claudeAiOauth\":{\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"pro\",\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"MAX\",\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_100x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\" default_claude_max_20x \"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":20,\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":{},\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":20}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":{\"value\":\"default_claude_max_20x\"}}}")]
	[InlineData("{\"other\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"nested\":{\"rateLimitTier\":\"default_claude_max_20x\"}}}")]
	public async Task ReadDisplayPlanAsync_WithAbsentUnknownOrWronglyTypedFields_ReturnsNull(
		string credentialsJson)
	{
		using TemporaryDirectory directory = new();
		await WriteCredentialsAsync(directory.Path, credentialsJson);

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
	}

	[Theory]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_5x\",\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	[InlineData("{\"claudeAiOauth\":{},\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}")]
	public async Task ReadDisplayPlanAsync_WithDuplicateRelevantField_ReturnsNull(
		string credentialsJson)
	{
		using TemporaryDirectory directory = new();
		await WriteCredentialsAsync(directory.Path, credentialsJson);

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithMissingFileOrDirectory_ReturnsNullWithoutCreatingIt()
	{
		using TemporaryDirectory directory = new();
		string missingDirectory = Path.Combine(directory.Path, "missing");

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			missingDirectory, CreateContext(), CancellationToken.None));
		Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
	}

	[Theory]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"},\"accessToken\":\"synthetic-secret\" invalid}")]
	[InlineData("{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}} trailing-synthetic-secret")]
	public async Task ReadDisplayPlanAsync_WithInvalidJson_LogsSanitizedWarningAndReturnsNull(
		string credentialsJson)
	{
		using TemporaryDirectory directory = new();
		string credentialsPath = await WriteCredentialsAsync(directory.Path, credentialsJson);
		using CapturingTraceListener listener = new();
		Trace.Listeners.Add(listener);
		try
		{
			Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
				directory.Path, CreateContext(), CancellationToken.None));
			string diagnostic = string.Join(" ", listener.Messages);
			Assert.Contains(".credentials.json", diagnostic);
			Assert.Contains(nameof(System.Text.Json.JsonException), diagnostic);
			Assert.DoesNotContain("synthetic-secret", diagnostic);
			Assert.DoesNotContain(directory.Path, diagnostic);
			Assert.Equal(credentialsJson, await File.ReadAllTextAsync(credentialsPath));
		}
		finally
		{
			Trace.Listeners.Remove(listener);
		}
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithUnknownTokenValues_SkipsThemAndKeepsDisplayHint()
	{
		using TemporaryDirectory directory = new();
		await WriteCredentialsAsync(directory.Path, """
			{
			  "unknown": {"rateLimitTier":"synthetic-secret"},
			  "claudeAiOauth": {
			    "accessToken": "synthetic-secret",
			    "refreshToken": {"arbitrary":["synthetic-secret",1,true,null]},
			    "subscriptionType":"max",
			    "rateLimitTier":"default_claude_max_20x"
			  }
			}
			""");

		Assert.Equal("ultra", await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithOversizedFile_ReturnsNullAndPreservesFile()
	{
		using TemporaryDirectory directory = new();
		string credentialsPath = Path.Combine(directory.Path, ".credentials.json");
		byte[] oversizedBytes = new byte[(1024 * 1024) + 1];
		await File.WriteAllBytesAsync(credentialsPath, oversizedBytes);

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
		Assert.Equal(oversizedBytes, await File.ReadAllBytesAsync(credentialsPath));
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithExcessiveJsonDepth_ReturnsNull()
	{
		using TemporaryDirectory directory = new();
		string credentialsJson =
			"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"},\"unknown\":" +
			new string('[', 17) + "0" + new string(']', 17) + "}";
		await WriteCredentialsAsync(directory.Path, credentialsJson);

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_AfterTierChanges_DoesNotReusePreviousHint()
	{
		using TemporaryDirectory directory = new();
		foreach ((string rateLimitTier, string? expectedDisplayPlan) in new[]
		{
			("default_claude_max_5x", "max"),
			("default_claude_max_20x", "ultra"),
			("unknown", (string?)null)
		})
		{
			await WriteCredentialsAsync(directory.Path,
				"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"" + rateLimitTier + "\"}}");

			Assert.Equal(expectedDisplayPlan, await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
				directory.Path, CreateContext(), CancellationToken.None));
		}
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithExclusiveFileLock_ReturnsNull()
	{
		using TemporaryDirectory directory = new();
		string credentialsPath = await WriteCredentialsAsync(directory.Path,
			"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}");
		using FileStream lockedCredentials = new(
			credentialsPath, FileMode.Open, FileAccess.Read, FileShare.None);

		Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			directory.Path, CreateContext(), CancellationToken.None));
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithJunctionAncestor_RejectsWithoutChangingTarget()
	{
		using TemporaryDirectory directory = new();
		string targetDirectory = Path.Combine(directory.Path, "target");
		string junctionDirectory = Path.Combine(directory.Path, "junction");
		Directory.CreateDirectory(targetDirectory);
		string credentialsPath = await WriteCredentialsAsync(targetDirectory,
			"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}");
		byte[] originalBytes = await File.ReadAllBytesAsync(credentialsPath);
		await JunctionTestHelper.CreateAsync(junctionDirectory, targetDirectory);
		try
		{
			Assert.Null(await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
				junctionDirectory, CreateContext(), CancellationToken.None));
			Assert.Equal(originalBytes, await File.ReadAllBytesAsync(credentialsPath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionDirectory);
		}
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithDifferentCardDirectories_ReadsOnlySelectedCard()
	{
		using TemporaryDirectory directory = new();
		string firstDirectory = Path.Combine(directory.Path, "first");
		string secondDirectory = Path.Combine(directory.Path, "second");
		Directory.CreateDirectory(firstDirectory);
		Directory.CreateDirectory(secondDirectory);
		await WriteCredentialsAsync(firstDirectory,
			"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_5x\"}}");
		await WriteCredentialsAsync(secondDirectory,
			"{\"claudeAiOauth\":{\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}");

		Assert.Equal("max", await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			firstDirectory, CreateContext(), CancellationToken.None));
		Assert.Equal("ultra", await ClaudePlanMetadataReader.ReadDisplayPlanAsync(
			secondDirectory, CreateContext(), CancellationToken.None));
	}

	[Fact]
	public async Task ReadDisplayPlanAsync_WithCancellation_PropagatesCancellation()
	{
		using TemporaryDirectory directory = new();
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			ClaudePlanMetadataReader.ReadDisplayPlanAsync(
				directory.Path, CreateContext(), cancellation.Token));
		Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
	}

	private static ClaudeSubscriptionContext CreateContext()
	{
		return ClaudeSubscriptionContext.CreateVerified(
			"claude@example.com", "org-123", "max", null);
	}

	private static async Task<string> WriteCredentialsAsync(
		string directory,
		string credentialsJson)
	{
		string credentialsPath = Path.Combine(directory, ".credentials.json");
		await File.WriteAllTextAsync(credentialsPath, credentialsJson);
		return credentialsPath;
	}
}
