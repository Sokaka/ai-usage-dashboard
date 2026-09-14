using System.Reflection;
using System.Reflection.Emit;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class AppVersionInfoTests
{
	[Theory]
	[InlineData("1.2.3+abc123", "1.2.3+abc123")]
	[InlineData("1.2.3-verify.4+abc123", "1.2.3-verify.4+abc123")]
	[InlineData("0.0.0-dev+abc123", "0.0.0-dev+abc123")]
	[InlineData(" 1.2.3 ", "1.2.3")]
	public void GetDisplayVersion_WithInformationalVersion_PreservesReleaseAndBuildIdentity(
		string informationalVersion,
		string expectedVersion)
	{
		Assembly assembly = CreateAssembly(informationalVersion);

		Assert.Equal(expectedVersion, AppVersionInfo.GetDisplayVersion(assembly));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void GetDisplayVersion_WithoutInformationalVersion_LabelsAssemblyFallbackAsIncomplete(
		string? informationalVersion)
	{
		Assembly assembly = CreateAssembly(informationalVersion);

		Assert.Equal(
			"2.3.4.5（版本資訊不完整）",
			AppVersionInfo.GetDisplayVersion(assembly));
	}

	private static Assembly CreateAssembly(string? informationalVersion)
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName("AboutVersionFixture")
			{
				Version = new Version(2, 3, 4, 5)
			},
			AssemblyBuilderAccess.RunAndCollect);

		if (informationalVersion is not null)
		{
			ConstructorInfo constructor = typeof(AssemblyInformationalVersionAttribute)
				.GetConstructor(new[] { typeof(string) }) ??
				throw new InvalidOperationException(
					"The informational version attribute constructor is unavailable.");
			assembly.SetCustomAttribute(new CustomAttributeBuilder(
				constructor, new object[] { informationalVersion }));
		}

		return assembly;
	}
}
