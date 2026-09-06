using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CopilotCredentialStoreTests
{
	private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
	{
		private readonly Dictionary<string, (string UserName, string Secret)>
			_credentials = new(StringComparer.Ordinal);

		public (string UserName, string Secret)? Read(string targetName)
		{
			return _credentials.TryGetValue(
				targetName,
				out (string UserName, string Secret) credential)
				? credential
				: null;
		}

		public void Write(string targetName, string userName, string secret)
		{
			_credentials[targetName] = (userName, secret);
		}

		public void Delete(string targetName)
		{
			_credentials.Remove(targetName);
		}
	}

	[Fact]
	public void TargetNames_AreAccountScopedAndSeparateActiveFromPending()
	{
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		string firstTarget = WindowsCopilotCredentialStore.CreateTargetName(
			firstAccountId);
		string firstPendingTarget =
			WindowsCopilotCredentialStore.CreatePendingTargetName(firstAccountId);
		string secondTarget = WindowsCopilotCredentialStore.CreateTargetName(
			secondAccountId);
		string secondPendingTarget =
			WindowsCopilotCredentialStore.CreatePendingTargetName(secondAccountId);

		Assert.Equal(
			$"AiUsageDashboard/Copilot/{firstAccountId:N}",
			firstTarget);
		Assert.Equal(
			$"AiUsageDashboard/Copilot/Pending/{firstAccountId:N}",
			firstPendingTarget);
		Assert.Equal(
			$"AiUsageDashboard/Copilot/{secondAccountId:N}",
			secondTarget);
		Assert.Equal(
			$"AiUsageDashboard/Copilot/Pending/{secondAccountId:N}",
			secondPendingTarget);
		Assert.Equal(
			4,
			new[]
			{
				firstTarget,
				firstPendingTarget,
				secondTarget,
				secondPendingTarget
			}.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void StageAndCommit_ForFirstAccount_DoesNotAffectSecondAccount()
	{
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		string firstIdentity = CreateIdentity("NODE_FIRST", "first-login");
		string secondIdentity = CreateIdentity("NODE_SECOND", "second-login");
		FakeWindowsCredentialApi api = new();
		WindowsCopilotCredentialStore store = new(api);

		store.Stage(
			firstAccountId,
			new CopilotStoredCredential(firstIdentity, "token-first"));
		store.Stage(
			secondAccountId,
			new CopilotStoredCredential(secondIdentity, "token-second"));

		Assert.True(store.CommitStaged(firstAccountId, firstIdentity));
		AssertCredential(
			store.Read(firstAccountId),
			firstIdentity,
			"token-first");
		Assert.Null(store.Read(secondAccountId));
		Assert.Null(api.Read(
			WindowsCopilotCredentialStore.CreatePendingTargetName(firstAccountId)));
		AssertApiCredential(
			api.Read(WindowsCopilotCredentialStore.CreatePendingTargetName(
				secondAccountId)),
			secondIdentity,
			"token-second");
	}

	[Fact]
	public void RecoverStaged_ForFirstAccount_DoesNotAffectSecondAccount()
	{
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		string firstIdentity = CreateIdentity("NODE_FIRST", "first-login");
		string secondIdentity = CreateIdentity("NODE_SECOND", "second-login");
		FakeWindowsCredentialApi api = new();
		WindowsCopilotCredentialStore store = new(api);
		store.Stage(
			firstAccountId,
			new CopilotStoredCredential(firstIdentity, "token-first"));
		store.Stage(
			secondAccountId,
			new CopilotStoredCredential(secondIdentity, "token-second"));

		store.RecoverStaged(firstAccountId, firstIdentity);

		AssertCredential(
			store.Read(firstAccountId),
			firstIdentity,
			"token-first");
		Assert.Null(store.Read(secondAccountId));
		Assert.Null(api.Read(
			WindowsCopilotCredentialStore.CreatePendingTargetName(firstAccountId)));
		AssertApiCredential(
			api.Read(WindowsCopilotCredentialStore.CreatePendingTargetName(
				secondAccountId)),
			secondIdentity,
			"token-second");
	}

	[Fact]
	public void Delete_ForFirstAccount_RemovesOnlyItsActiveAndPendingCredentials()
	{
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		string firstIdentity = CreateIdentity("NODE_FIRST", "first-login");
		string secondIdentity = CreateIdentity("NODE_SECOND", "second-login");
		FakeWindowsCredentialApi api = new();
		WindowsCopilotCredentialStore store = new(api);
		StageCommitAndRestage(
			store,
			firstAccountId,
			firstIdentity,
			"token-first-active",
			"token-first-pending");
		StageCommitAndRestage(
			store,
			secondAccountId,
			secondIdentity,
			"token-second-active",
			"token-second-pending");

		store.Delete(firstAccountId);

		Assert.Null(store.Read(firstAccountId));
		Assert.Null(api.Read(
			WindowsCopilotCredentialStore.CreatePendingTargetName(firstAccountId)));
		AssertCredential(
			store.Read(secondAccountId),
			secondIdentity,
			"token-second-active");
		AssertApiCredential(
			api.Read(WindowsCopilotCredentialStore.CreatePendingTargetName(
				secondAccountId)),
			secondIdentity,
			"token-second-pending");
	}

	[Fact]
	public void StoredCredential_ToString_DoesNotExposeAccessToken()
	{
		const string accessToken = "synthetic-secret-value";
		CopilotStoredCredential credential = new(
			CreateIdentity("NODE_FIRST", "first-login"),
			accessToken);

		string text = credential.ToString();

		Assert.Equal(nameof(CopilotStoredCredential), text);
		Assert.DoesNotContain(accessToken, text, StringComparison.Ordinal);
	}

	[Fact]
	public void InvalidCredentialExceptions_DoNotExposeAccessToken()
	{
		Guid accountId = Guid.NewGuid();
		string identity = CreateIdentity("NODE_FIRST", "first-login");
		const string storedInvalidToken = "stored secret with whitespace";
		const string stagedInvalidToken = "staged secret with whitespace";
		FakeWindowsCredentialApi api = new();
		WindowsCopilotCredentialStore store = new(api);
		api.Write(
			WindowsCopilotCredentialStore.CreateTargetName(accountId),
			identity,
			storedInvalidToken);

		CopilotClientException readFailure = Assert.Throws<CopilotClientException>(
			() => store.Read(accountId));
		ArgumentException stageFailure = Assert.Throws<ArgumentException>(() =>
			store.Stage(
				accountId,
				new CopilotStoredCredential(identity, stagedInvalidToken)));

		Assert.DoesNotContain(
			storedInvalidToken,
			readFailure.ToString(),
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			stagedInvalidToken,
			stageFailure.ToString(),
			StringComparison.Ordinal);
	}

	private static void StageCommitAndRestage(
		WindowsCopilotCredentialStore store,
		Guid accountId,
		string identity,
		string activeToken,
		string pendingToken)
	{
		store.Stage(
			accountId,
			new CopilotStoredCredential(identity, activeToken));
		Assert.True(store.CommitStaged(accountId, identity));
		store.Stage(
			accountId,
			new CopilotStoredCredential(identity, pendingToken));
	}

	private static void AssertCredential(
		CopilotStoredCredential? actual,
		string expectedIdentity,
		string expectedToken)
	{
		Assert.NotNull(actual);
		Assert.Equal(expectedIdentity, actual.ProviderAccountIdentity);
		Assert.Equal(expectedToken, actual.AccessToken);
	}

	private static void AssertApiCredential(
		(string UserName, string Secret)? actual,
		string expectedIdentity,
		string expectedToken)
	{
		Assert.NotNull(actual);
		Assert.Equal(expectedIdentity, actual.Value.UserName);
		Assert.Equal(expectedToken, actual.Value.Secret);
	}

	private static string CreateIdentity(string nodeId, string login)
	{
		return CopilotAccountIdentityRules.Create(new CopilotAccountIdentity(
			"github.com",
			nodeId,
			1001,
			login));
	}
}
