using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityLiveR0ProfileFileTests
{
	[Fact]
	public async Task ToProfileAsync_WithAdjacentExactKey_LoadsAndCanZeroKey()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		string profilePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(keyPath)!,
			"profile.json"));
		AntigravityLiveR0ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		await File.WriteAllTextAsync(
			profilePath,
			JsonSerializer.Serialize(profileFile));
		(AntigravityLiveR0Profile profile, byte[] loadedKey) =
			await profileFile.ToProfileAsync(profilePath);

		try
		{
			Assert.Equal(sourceKey, loadedKey);
			Assert.True(
				profile.ExactPromptFingerprintHmacKey.Span.SequenceEqual(
					sourceKey));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
			CryptographicOperations.ZeroMemory(loadedKey);
		}

		Assert.All(loadedKey, value => Assert.Equal((byte)0, value));
	}

	[Fact]
	public async Task ToProfileAsync_WithWrongKeyLength_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] malformedKey = new byte[31];
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			malformedKey);
		string profilePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(keyPath)!,
			"profile.json"));
		AntigravityLiveR0ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(profilePath);
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(malformedKey);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WithKeyInDifferentDirectory_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"profile.json"));
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);
		AntigravityLiveR0ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(profilePath);
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public void JsonSerialization_ContainsOnlyKeyPathAndNotKeyMaterial()
	{
		using TemporaryDirectory temporaryDirectory = new();
		byte[] sourceKey = CreateKey();
		string keyPath = CreatePrivateKeyFile(
			temporaryDirectory,
			"profile.key",
			sourceKey);

		try
		{
			AntigravityLiveR0ProfileFile profileFile = CreateProfileFile(
				temporaryDirectory,
				keyPath);
			string json = JsonSerializer.Serialize(profileFile);
			string keyHex = Convert.ToHexString(sourceKey);

			Assert.Contains(
				nameof(AntigravityLiveR0ProfileFile.ExactPromptFingerprintHmacKeyPath),
				json,
				StringComparison.Ordinal);
			Assert.Contains(
				Path.GetFileName(keyPath),
				json,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				keyHex,
				json,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				"HmacKeyHex",
				json,
				StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public void StrictJson_WithSerializedProfile_RoundTrips()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string keyPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"profile.key"));
		AntigravityLiveR0ProfileFile expected = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(expected);

		AntigravityLiveR0ProfileFile actual =
			AntigravityLiveR0ProfileJson.Deserialize(json);

		Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(actual));
	}

	[Fact]
	public void StrictJson_WithUtf8Bom_RoundTrips()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string keyPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"profile.key"));
		AntigravityLiveR0ProfileFile expected = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(expected);
		byte[] withPreamble = new byte[
			Encoding.UTF8.Preamble.Length + json.Length];
		Encoding.UTF8.Preamble.CopyTo(withPreamble);
		json.AsSpan().CopyTo(
			withPreamble.AsSpan(Encoding.UTF8.Preamble.Length));

		try
		{
			AntigravityLiveR0ProfileFile actual =
				AntigravityLiveR0ProfileJson.Deserialize(withPreamble);

			Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(actual));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(withPreamble);
			CryptographicOperations.ZeroMemory(json);
		}
	}

	[Theory]
	[InlineData("{\"UnknownPrivateCaptureField\":true,")]
	[InlineData("{\"Id\":\"duplicate\",")]
	public void StrictJson_WithUnknownOrDuplicateProperty_FailsClosed(
		string replacementPrefix)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string keyPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"profile.key"));
		AntigravityLiveR0ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);
		string json = JsonSerializer.Serialize(profileFile);
		byte[] malformedJson = System.Text.Encoding.UTF8.GetBytes(
			replacementPrefix + json[1..]);

		try
		{
			Assert.Throws<JsonException>(() =>
				AntigravityLiveR0ProfileJson.Deserialize(malformedJson));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(malformedJson);
		}
	}

	[Fact]
	public async Task ToProfileAsync_WithBroadInheritedFileAcl_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"profile.json"));
		string keyPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"broad.key"));
		byte[] sourceKey = CreateKey();
		await File.WriteAllBytesAsync(keyPath, sourceKey);
		AntigravityLiveR0ProfileFile profileFile = CreateProfileFile(
			temporaryDirectory,
			keyPath);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
			{
				_ = await profileFile.ToProfileAsync(profilePath);
			});
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceKey);
		}
	}

	[Fact]
	public async Task PrivateProfileRead_WithSafeInheritedAcl_ReadsExactBytes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string privateDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-profile"));
		Assert.True(AntigravityPrivateKeyAcl.TryPrepareDirectory(
			privateDirectory,
			out _));
		string profilePath = Path.Combine(privateDirectory, "profile.json");
		byte[] expectedBytes = Encoding.UTF8.GetBytes(
			"{\"PrivateProfileMarker\":true}");
		await File.WriteAllBytesAsync(profilePath, expectedBytes);

		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(profilePath));
		Assert.True(
			AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
				profilePath));
		byte[] actualBytes =
			await AntigravityLiveR0PrivateProfileFile.ReadBoundedAsync(
				profilePath,
				1024);

		try
		{
			Assert.Equal(expectedBytes, actualBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(expectedBytes);
			CryptographicOperations.ZeroMemory(actualBytes);
		}
	}

	[Fact]
	public async Task PrivateProfileRead_WithBroadDirectory_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string profilePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"broad-profile.json"));
		await File.WriteAllTextAsync(profilePath, "{}");

		Assert.False(
			AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
				profilePath));
		await Assert.ThrowsAsync<InvalidDataException>(async () =>
		{
			_ = await AntigravityLiveR0PrivateProfileFile.ReadBoundedAsync(
				profilePath,
				1024);
		});
	}

	private static string CreatePrivateKeyFile(
		TemporaryDirectory temporaryDirectory,
		string fileName,
		byte[] contents)
	{
		string keyDirectory = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-key"));
		Assert.True(AntigravityPrivateKeyAcl.TryPrepareDirectory(
			keyDirectory,
			out _));
		string keyPath = Path.GetFullPath(Path.Combine(
			keyDirectory,
			fileName));
		File.WriteAllBytes(keyPath, contents);
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(keyPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(keyPath));
		return keyPath;
	}

	private static byte[] CreateKey()
	{
		return Enumerable.Range(1, 32)
			.Select(value => (byte)value)
			.ToArray();
	}

	private static AntigravityLiveR0ProfileFile CreateProfileFile(
		TemporaryDirectory temporaryDirectory,
		string keyPath)
	{
		AntigravityCliFingerprint fingerprint = new(
			Path.GetFullPath(Path.Combine(
				temporaryDirectory.Path,
				"agy.exe")),
			"1.1.3",
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			new string('A', 64),
			0,
			"CN=Synthetic",
			new string('B', 40));
		return new AntigravityLiveR0ProfileFile(
			"synthetic-live-r0",
			fingerprint,
			temporaryDirectory.Path,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			120,
			30,
			string.Empty,
			keyPath,
			string.Empty,
			null,
			Array.Empty<string>(),
			64 * 1024,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1),
			TimeSpan.FromMilliseconds(1),
			TimeSpan.FromMilliseconds(10),
			TimeSpan.FromSeconds(1));
	}
}
