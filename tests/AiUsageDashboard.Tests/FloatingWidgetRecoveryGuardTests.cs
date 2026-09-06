using System.Xml.Linq;

namespace AiUsageDashboard.Tests;

public sealed class FloatingWidgetRecoveryGuardTests
{
	private static readonly XNamespace Presentation =
		"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Xaml =
		"http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void RecoveryActionButton_DisablesWhileAccountMutationRuns()
	{
		XElement style = GetStyle("RecoveryActionButtonStyle");

		Assert.Contains(
			style.Elements(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					"IsEnabled",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					"{Binding CanInvokeRecoveryAction}",
					StringComparison.Ordinal));
		XElement busyTrigger = Assert.Single(
			style.Descendants(Presentation + "DataTrigger"),
			trigger =>
				((string?)trigger.Attribute("Binding"))?.Contains(
					"DataContext.IsManagingAccounts",
					StringComparison.Ordinal) == true);

		Assert.Equal("True", (string?)busyTrigger.Attribute("Value"));
		Assert.Contains(
			busyTrigger.Elements(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					"IsEnabled",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					"False",
					StringComparison.Ordinal));
	}

	[Fact]
	public void RecoveryRetryButton_RequiresExecutableActionAndIdleAccountMutation()
	{
		XElement style = GetStyle("RecoveryRetryButtonStyle");
		XElement enableTrigger = Assert.Single(
			style.Descendants(Presentation + "MultiDataTrigger"));
		XElement[] conditions = enableTrigger
			.Descendants(Presentation + "Condition")
			.ToArray();

		Assert.Contains(
			conditions,
			condition =>
				string.Equals(
					(string?)condition.Attribute("Binding"),
					"{Binding CanExecuteRecoveryAction}",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)condition.Attribute("Value"),
					"True",
					StringComparison.Ordinal));
		Assert.Contains(
			conditions,
			condition =>
				((string?)condition.Attribute("Binding"))?.Contains(
					"DataContext.IsManagingAccounts",
					StringComparison.Ordinal) == true &&
				string.Equals(
					(string?)condition.Attribute("Value"),
					"False",
					StringComparison.Ordinal));
		Assert.Contains(
			enableTrigger.Elements(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					"IsEnabled",
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					"True",
					StringComparison.Ordinal));
	}

	private static XElement GetStyle(string key)
	{
		XDocument document = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"FloatingWidgetWindow.xaml"));

		return Assert.Single(
			document.Descendants(Presentation + "Style"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Key"),
				key,
				StringComparison.Ordinal));
	}
}
