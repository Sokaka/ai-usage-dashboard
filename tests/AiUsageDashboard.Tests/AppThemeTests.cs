using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class AppThemeTests
{
	private static readonly XNamespace Presentation =
		"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Xaml =
		"http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly string[] ProviderAccentColorKeys =
	{
		"ClaudeProviderAccentColor",
		"CodexProviderAccentColor",
		"CopilotProviderAccentColor",
		"AntigravityProviderAccentColor",
		"GrokProviderAccentColor",
		"DefaultProviderAccentColor"
	};

	[Theory]
	[InlineData((int)AppTheme.ClassicBlue, false, "Themes/Palette.xaml")]
	[InlineData((int)AppTheme.Midnight, false, "Themes/MidnightPalette.xaml")]
	[InlineData((int)AppTheme.Light, false, "Themes/LightPalette.xaml")]
	[InlineData((int)AppTheme.Sakura, false, "Themes/SakuraPalette.xaml")]
	[InlineData((int)AppTheme.ClassicBlue, true, "Themes/HighContrastPalette.xaml")]
	[InlineData((int)AppTheme.Midnight, true, "Themes/HighContrastPalette.xaml")]
	[InlineData((int)AppTheme.Light, true, "Themes/HighContrastPalette.xaml")]
	[InlineData((int)AppTheme.Sakura, true, "Themes/HighContrastPalette.xaml")]
	public void GetPaletteUri_ReturnsExpectedPalette(
		int themeValue,
		bool isHighContrast,
		string expected)
	{
		Uri actual = AiUsageDashboard.App.App.GetPaletteUri(
			(AppTheme)themeValue,
			isHighContrast);

		Assert.Equal(expected, actual.OriginalString);
		Assert.False(actual.IsAbsoluteUri);
	}

	[Fact]
	public void UpdatePaletteResources_PreservesExistingBrushReferences()
	{
		SolidColorBrush existingBrush = new(Colors.Black);
		ResourceDictionary currentPalette = new()
		{
			["WindowBackgroundColor"] = Colors.Black,
			["WindowBackgroundBrush"] = existingBrush
		};
		ResourceDictionary targetPalette = new()
		{
			["WindowBackgroundColor"] = Colors.White,
			["WindowBackgroundBrush"] = new SolidColorBrush(Colors.White)
		};

		AiUsageDashboard.App.App.UpdatePaletteResources(
			currentPalette,
			targetPalette);

		Assert.Equal(Colors.White, currentPalette["WindowBackgroundColor"]);
		Assert.Same(existingBrush, currentPalette["WindowBackgroundBrush"]);
		Assert.Equal(Colors.White, existingBrush.Color);
	}

	[Fact]
	public void UpdatePaletteResources_WithDetachedDynamicBrushValue_UsesColorResource()
	{
		SolidColorBrush existingBrush = new(Colors.Black);
		ResourceDictionary currentPalette = new()
		{
			["WindowBackgroundColor"] = Colors.Black,
			["WindowBackgroundBrush"] = existingBrush
		};
		ResourceDictionary targetPalette = new()
		{
			["WindowBackgroundColor"] = Colors.White,
			["WindowBackgroundBrush"] = new SolidColorBrush(Colors.Red)
		};

		AiUsageDashboard.App.App.UpdatePaletteResources(
			currentPalette,
			targetPalette);

		Assert.Equal(Colors.White, existingBrush.Color);
	}

	[Fact]
	public void PaletteBrushes_AreMutableForRuntimeThemeChanges()
	{
		ResourceDictionary palette = LoadPalette("Palette.xaml");

		SolidColorBrush brush = Assert.IsType<SolidColorBrush>(
			palette["WindowBackgroundBrush"]);

		Assert.False(brush.IsFrozen);
	}

	[Fact]
	public void HighContrastPalette_LoadsSystemColorsAsColorResources()
	{
		ResourceDictionary palette = LoadPalette("HighContrastPalette.xaml");

		foreach (string resourceKey in GetResourceKeys("HighContrastPalette.xaml"))
		{
			Assert.NotNull(palette[resourceKey]);
			if (resourceKey.EndsWith("Color", StringComparison.Ordinal))
			{
				Assert.IsType<Color>(palette[resourceKey]);
			}
		}

		Assert.Equal(SystemColors.WindowColor, GetColor(palette, "WindowBackgroundColor"));
		Assert.Equal(SystemColors.WindowTextColor, GetColor(palette, "PrimaryTextColor"));
		Assert.Equal(SystemColors.HighlightColor, GetColor(palette, "AccentColor"));
		Assert.Equal(SystemColors.HighlightTextColor, GetColor(palette, "AccentTextColor"));
		Assert.Equal(SystemColors.WindowTextColor, GetColor(palette, "ScrollBarThumbColor"));
	}

	[Fact]
	public void HighContrastPalette_UsesSystemTextColorForInformationalText()
	{
		XDocument palette = XDocument.Load(
			GetPalettePath("HighContrastPalette.xaml"));

		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "InfoColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "WarningTextColor"));
		Assert.Equal(
			"HighlightTextColor",
			GetSystemColorMember(palette, "AccentTextColor"));
		Assert.Equal(
			"HighlightColor",
			GetSystemColorMember(palette, "AccentColor"));
	}

	[Fact]
	public void HighContrastPalette_PreservesSystemControlStateColors()
	{
		XDocument palette = XDocument.Load(
			GetPalettePath("HighContrastPalette.xaml"));

		Assert.Equal(
			"ActiveBorderColor",
			GetSystemColorMember(palette, "BorderColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "BorderStrongColor"));
		Assert.Equal(
			"ActiveBorderColor",
			GetSystemColorMember(palette, "InputBorderColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "InputBorderStrongColor"));
		Assert.Equal(
			"HighlightColor",
			GetSystemColorMember(palette, "ProgressTrackColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "FocusColor"));
	}

	[Fact]
	public void HighContrastPalette_UsesSystemColorsForThemeSpecificAccents()
	{
		XDocument palette = XDocument.Load(
			GetPalettePath("HighContrastPalette.xaml"));

		Assert.Equal(
			"WindowColor",
			GetSystemColorMember(palette, "LogoSurfaceColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "LogoBorderColor"));
		Assert.Equal(
			"HighlightColor",
			GetSystemColorMember(palette, "LogoPrimaryColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "LogoSecondaryColor"));
		Assert.Equal(
			"WindowTextColor",
			GetSystemColorMember(palette, "LogoSparkColor"));

		foreach (string resourceKey in ProviderAccentColorKeys)
		{
			Assert.Equal(
				"WindowTextColor",
				GetSystemColorMember(palette, resourceKey));
		}
	}

	[Theory]
	[InlineData("MidnightPalette.xaml")]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	[InlineData("HighContrastPalette.xaml")]
	public void PaletteResourceKeys_MatchDefaultPalette(string fileName)
	{
		string[] expected = GetResourceKeys("Palette.xaml");
		string[] actual = GetResourceKeys(fileName);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData("Palette.xaml")]
	[InlineData("MidnightPalette.xaml")]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	public void GeneralPalette_TextAndInteractiveColorsMeetMinimumContrast(
		string fileName)
	{
		ResourceDictionary palette = LoadPalette(fileName);
		Color windowBackground = GetColor(palette, "WindowBackgroundColor");
		Color cardBackground = GetColor(palette, "CardBackgroundColor");
		Color controlBackground = GetColor(palette, "ControlBackgroundColor");
		Color controlHoverBackground = GetColor(
			palette,
			"ControlHoverBackgroundColor");
		Color controlPressedBackground = GetColor(
			palette,
			"ControlPressedBackgroundColor");
		Color surfaceOverlay = Composite(
			GetColor(palette, "SurfaceOverlayColor"),
			windowBackground);

		foreach (string resourceKey in new[]
		{
			"PrimaryTextColor",
			"SecondaryTextColor",
			"MutedTextColor",
			"WarningTextColor",
			"DangerTextColor"
		})
		{
			AssertMinimumContrast(
				GetColor(palette, resourceKey),
				cardBackground,
				4.5,
				$"{fileName} {resourceKey} against the card");
		}

		AssertMinimumContrast(
			GetColor(palette, "InputBorderColor"),
			windowBackground,
			3,
			$"{fileName} idle control border against the window");
		AssertMinimumContrast(
			GetColor(palette, "InputBorderStrongColor"),
			windowBackground,
			3,
			$"{fileName} hovered control border against the window");
		AssertMinimumContrast(
			GetColor(palette, "InputBorderStrongColor"),
			GetColor(palette, "InputBorderColor"),
			1.5,
			$"{fileName} hovered control border against the idle border");
		AssertMinimumContrast(
			GetColor(palette, "FocusColor"),
			controlBackground,
			3,
			$"{fileName} focused control border against the control fill");
		AssertMinimumContrast(
			GetColor(palette, "InfoColor"),
			surfaceOverlay,
			3,
			$"{fileName} idle menu indicator against the menu surface");
		AssertMinimumContrast(
			GetColor(palette, "ProgressTrackColor"),
			cardBackground,
			1.2,
			$"{fileName} progress track against the card");
		foreach (string resourceKey in new[]
		{
			"WarningColor",
			"DangerColor"
		})
		{
			AssertMinimumContrast(
				GetColor(palette, resourceKey),
				GetColor(palette, "ProgressTrackColor"),
				3,
				$"{fileName} {resourceKey} against the progress track");
		}
		Assert.Equal(
			GetColor(palette, "ProgressTrackColor"),
			GetColor(palette, "ScrollBarThumbColor"));
		double previousScrollBarContrast = GetContrastRatio(
			GetColor(palette, "ScrollBarThumbColor"),
			windowBackground);
		foreach (string resourceKey in new[] { "ScrollBarThumbHoverColor", "ScrollBarThumbPressedColor" })
		{
			double scrollBarContrast = GetContrastRatio(
				GetColor(palette, resourceKey),
				windowBackground);
			Assert.InRange(scrollBarContrast / previousScrollBarContrast, 1.1, 1.4);
			previousScrollBarContrast = scrollBarContrast;
		}
		AssertMinimumContrast(
			GetColor(palette, "PrimaryTextColor"),
			controlHoverBackground,
			4.5,
			$"{fileName} highlighted text against the hover surface");
		AssertMinimumContrast(
			GetColor(palette, "PrimaryTextColor"),
			controlPressedBackground,
			4.5,
			$"{fileName} highlighted text against the pressed surface");
		foreach (string resourceKey in new[]
		{
			"AccentColor",
			"AccentHoverColor",
			"AccentPressedColor"
		})
		{
			AssertMinimumContrast(
				GetColor(palette, "AccentTextColor"),
				GetColor(palette, resourceKey),
				4.5,
				$"{fileName} accent text against {resourceKey}");
		}
	}

	[Theory]
	[InlineData("Palette.xaml")]
	[InlineData("MidnightPalette.xaml")]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	public void GeneralPalette_ProviderAccentsMeetMinimumContrast(
		string fileName)
	{
		ResourceDictionary palette = LoadPalette(fileName);
		Color cardBackground = GetColor(palette, "CardBackgroundColor");
		Color progressTrack = GetColor(
			palette,
			"ProgressTrackColor");

		foreach (string resourceKey in ProviderAccentColorKeys)
		{
			AssertMinimumContrast(
				GetColor(palette, resourceKey),
				cardBackground,
				3,
				$"{fileName} {resourceKey} against the card");
			AssertMinimumContrast(
				GetColor(palette, resourceKey),
				progressTrack,
				3,
				$"{fileName} {resourceKey} against the progress track");
		}
	}

	[Theory]
	[InlineData("Palette.xaml")]
	[InlineData("MidnightPalette.xaml")]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	public void GeneralPalette_ProviderTitleBrushesMeetMinimumTextContrast(
		string fileName)
	{
		ResourceDictionary palette = LoadPalette(fileName);
		XDocument paletteSource = XDocument.Load(GetPalettePath(fileName));
		Color cardBackground = GetColor(palette, "CardBackgroundColor");

		foreach (string providerName in new[]
		{
			"Claude", "Codex", "Copilot", "Antigravity", "Grok"
		})
		{
			string resourceKey = $"{providerName}ProviderTextBrush";
			SolidColorBrush brush = Assert.IsType<SolidColorBrush>(
				palette[resourceKey]);
			XElement brushElement = Assert.Single(
				paletteSource.Root!.Elements(Presentation + "SolidColorBrush"),
				element => (string?)element.Attribute(Xaml + "Key") == resourceKey);
			Assert.Equal($"{{DynamicResource {providerName}ProviderTextColor}}",
				(string?)brushElement.Attribute("Color"));
			Color textColor = GetColor(palette, $"{providerName}ProviderTextColor");
			textColor.A = (byte)Math.Round(textColor.A * brush.Opacity);
			AssertMinimumContrast(
				Composite(textColor, cardBackground),
				cardBackground,
				4.5,
				$"{fileName} {resourceKey} against the card");
		}
	}

	[Theory]
	[InlineData(
		"MidnightPalette.xaml",
		"#D97757",
		"#818CF8",
		"#F472B6",
		"#06B6D4",
		"#A1A1AA")]
	[InlineData(
		"LightPalette.xaml",
		"#C4522C",
		"#5665F6",
		"#E1127E",
		"#048197",
		"#747483")]
	[InlineData(
		"SakuraPalette.xaml",
		"#C4522C",
		"#5665F6",
		"#E1127E",
		"#048197",
		"#747483")]
	public void ThemedProviderAccents_StayAnchoredToProviderDefaults(
		string fileName,
		string claude,
		string codex,
		string copilot,
		string antigravity,
		string grok)
	{
		ResourceDictionary palette = LoadPalette(fileName);
		Dictionary<string, string> expectedColors = new()
		{
			["ClaudeProviderAccentColor"] = claude,
			["CodexProviderAccentColor"] = codex,
			["CopilotProviderAccentColor"] = copilot,
			["AntigravityProviderAccentColor"] = antigravity,
			["GrokProviderAccentColor"] = grok
		};

		foreach ((string resourceKey, string expected) in expectedColors)
		{
			Assert.Equal(
				(Color)ColorConverter.ConvertFromString(expected),
				GetColor(palette, resourceKey));
		}
	}

	[Fact]
	public void UsageMetricSeverityStyles_KeepGraphicAndTextColorsSeparate()
	{
		XDocument controls = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Themes",
			"Controls.xaml"));
		XElement progressStyle = GetKeyedStyle(
			controls,
			"UsageMetricProgressBarStyle");
		XElement valueTextStyle = GetKeyedStyle(
			controls,
			"UsageMetricValueTextStyle");

		AssertDataTriggerSetter(
			progressStyle,
			"{x:Static viewModels:UsageLevel.Warning}",
			"Foreground",
			"{DynamicResource WarningBrush}");
		AssertDataTriggerSetter(
			progressStyle,
			"{x:Static viewModels:UsageLevel.Critical}",
			"Foreground",
			"{DynamicResource DangerBrush}");
		AssertDataTriggerSetter(
			valueTextStyle,
			"{x:Static viewModels:UsageLevel.Warning}",
			"Foreground",
			"{DynamicResource WarningTextBrush}");
		AssertDataTriggerSetter(
			valueTextStyle,
			"{x:Static viewModels:UsageLevel.Critical}",
			"Foreground",
			"{DynamicResource DangerTextBrush}");
	}

	[Theory]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	public void LightPalettes_UseReviewedWarningColors(string fileName)
	{
		ResourceDictionary palette = LoadPalette(fileName);

		Assert.Equal(
			(Color)ColorConverter.ConvertFromString("#866F00"),
			GetColor(palette, "WarningColor"));
		Assert.Equal(
			(Color)ColorConverter.ConvertFromString("#806800"),
			GetColor(palette, "WarningTextColor"));
	}

	[Theory]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	public void LightPalettes_TextAndBoundariesContrastAgainstLightSurfaces(
		string fileName)
	{
		ResourceDictionary palette = LoadPalette(fileName);
		Color cardBackground = GetColor(palette, "CardBackgroundColor");

		AssertMinimumContrast(
			GetColor(palette, "DisabledTextColor"),
			cardBackground,
			4.5,
			$"{fileName} disabled text against the card");

		foreach (string resourceKey in new[]
		{
			"BorderColor",
			"BorderStrongColor"
		})
		{
			AssertMinimumContrast(
				GetColor(palette, resourceKey),
				cardBackground,
				1.5,
				$"{fileName} {resourceKey} against the card");
		}

		foreach (string resourceKey in new[]
		{
			"InputBorderColor",
			"InputBorderStrongColor"
		})
		{
			AssertMinimumContrast(
				GetColor(palette, resourceKey),
				cardBackground,
				3,
				$"{fileName} {resourceKey} against the card");
		}
	}

	[Theory]
	[InlineData("Palette.xaml")]
	[InlineData("MidnightPalette.xaml")]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	public void GeneralPalette_ProvidesThemeAwareLogoBrushes(string fileName)
	{
		ResourceDictionary palette = LoadPalette(fileName);

		Assert.IsType<SolidColorBrush>(palette["LogoSurfaceBrush"]);
		Assert.IsType<SolidColorBrush>(palette["LogoBorderBrush"]);
		Assert.IsType<SolidColorBrush>(palette["LogoPrimaryBrush"]);
		Assert.IsType<SolidColorBrush>(palette["LogoSecondaryBrush"]);
		Assert.IsType<SolidColorBrush>(palette["LogoSparkBrush"]);
		LinearGradientBrush orbit = Assert.IsType<LinearGradientBrush>(
			palette["LogoOrbitBrush"]);

		Assert.Equal(2, orbit.GradientStops.Count);
		Assert.Equal(
			GetColor(palette, "LogoPrimaryColor"),
			orbit.GradientStops[0].Color);
		Assert.Equal(
			GetColor(palette, "LogoSecondaryColor"),
			orbit.GradientStops[1].Color);
	}

	[Fact]
	public void SakuraPalette_EnablesBlossomDecorationOnlyForSakura()
	{
		Color sakuraDecoration = GetColor(
			LoadPalette("SakuraPalette.xaml"),
			"ThemeDecorationColor");

		Assert.True(sakuraDecoration.A > 0);

		foreach (string fileName in new[]
		{
			"Palette.xaml",
			"MidnightPalette.xaml",
			"LightPalette.xaml"
		})
		{
			Assert.Equal(
				0,
				GetColor(
					LoadPalette(fileName),
					"ThemeDecorationColor").A);
		}
	}

	[Fact]
	public void ThemeLogoStyle_UsesCenteredCircularProgressOrbit()
	{
		XDocument controls = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Themes",
			"Controls.xaml"));
		XElement style = GetKeyedStyle(controls, "ThemeLogoStyle");
		XElement orbit = Assert.Single(
			style.Descendants(Presentation + "Path"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				"LogoOrbit",
				StringComparison.Ordinal));

		Assert.Equal(
			"M 36.02,36.02 A 17,17 0 1 1 40.42,19.6",
			(string?)orbit.Attribute("Data"));
		Assert.Equal(
			"{DynamicResource LogoOrbitBrush}",
			(string?)orbit.Attribute("Stroke"));
		Assert.Equal("Round", (string?)orbit.Attribute("StrokeStartLineCap"));
		Assert.Equal("Round", (string?)orbit.Attribute("StrokeEndLineCap"));

		XElement marker = Assert.Single(
			style.Descendants(Presentation + "Ellipse"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				"LogoProgressMarker",
				StringComparison.Ordinal));
		Assert.Equal("8", (string?)marker.Attribute("Width"));
		Assert.Equal("8", (string?)marker.Attribute("Height"));
		Assert.Equal("36.42,15.6,0,0", (string?)marker.Attribute("Margin"));
		Assert.Equal(
			"{DynamicResource LogoSparkBrush}",
			(string?)marker.Attribute("Fill"));
		Assert.Equal(
			2,
			style.Descendants(Presentation + "Ellipse").Count());

		XElement blossom = Assert.Single(
			style.Descendants(Presentation + "Path"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				"LogoBlossom",
				StringComparison.Ordinal));
		Assert.Equal(
			"{DynamicResource ThemeDecorationBrush}",
			(string?)blossom.Attribute("Fill"));
	}

	[Fact]
	public void ControlStyles_PreserveInputAndCheckedMenuStateBrushes()
	{
		XDocument controls = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Themes",
			"Controls.xaml"));

		foreach (string controlType in new[] { "TextBox", "ComboBox" })
		{
			XElement style = GetImplicitStyle(controls, controlType);
			AssertSetter(
				style,
				"BorderBrush",
				"{DynamicResource InputBorderBrush}");
			AssertTemplateTriggerSetter(
				style,
				"IsMouseOver",
				"Chrome",
				"BorderBrush",
				"{DynamicResource InputBorderStrongBrush}");
			AssertTemplateTriggerSetter(
				style,
				"IsKeyboardFocusWithin",
				"Chrome",
				"BorderBrush",
				"{DynamicResource FocusBrush}");

			if (controlType == "ComboBox")
			{
				AssertTemplateTriggerSetter(
					style,
					"IsDropDownOpen",
					"Chrome",
					"BorderBrush",
					"{DynamicResource InputBorderStrongBrush}");
			}
		}

		XElement menuItemStyle = GetImplicitStyle(controls, "MenuItem");
		XElement checkMark = Assert.Single(
			menuItemStyle.Descendants(Presentation + "Path"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Name"),
				"CheckMark",
				StringComparison.Ordinal));
		Assert.Equal(
			"{DynamicResource InfoBrush}",
			(string?)checkMark.Attribute("Stroke"));
		AssertTemplateTriggerSetter(
			menuItemStyle,
			"IsHighlighted",
			"CheckMark",
			"Stroke",
			"{DynamicResource PrimaryTextBrush}");
	}

	[Theory]
	[InlineData("FloatingWidgetWindow.xaml")]
	[InlineData("AccountEditorWindow.xaml")]
	public void ThemeAwareWindows_ReferencePaletteBrushesDynamically(
		string fileName)
	{
		string xaml = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			fileName));
		string[] paletteBrushKeys = GetResourceKeys("Palette.xaml")
			.Where(resourceKey => resourceKey.EndsWith(
				"Brush",
				StringComparison.Ordinal))
			.ToArray();

		foreach (string resourceKey in paletteBrushKeys)
		{
			Assert.DoesNotContain(
				$"{{StaticResource {resourceKey}}}",
				xaml,
				StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("FloatingWidgetWindow.xaml")]
	public void ProviderAccentBindings_UseDynamicHighContrastOverride(
		string fileName)
	{
		string appDirectory = Path.Combine(
			RepositoryTestPaths.Root, "src", "AiUsageDashboard.App");
		XDocument window = XDocument.Load(Path.Combine(appDirectory, fileName));
		string controlsXaml = File.ReadAllText(Path.Combine(
			appDirectory, "Themes", "Controls.xaml"));
		XElement titleStyle = Assert.Single(
			window.Descendants(Presentation + "Style"),
			element => (string?)element.Attribute(Xaml + "Key") == "AccountTitleStyle");
		Assert.Equal("{x:Type TextBlock}", (string?)titleStyle.Attribute("TargetType"));
		AssertSetter(titleStyle, "Foreground", "{DynamicResource DefaultProviderAccentBrush}");
		XElement[] titleTriggers = Assert.Single(
			titleStyle.Elements(Presentation + "Style.Triggers")).Elements().ToArray();
		XElement highContrastTrigger = Assert.Single(titleTriggers,
			element =>
				(element.Name == Presentation + "DataTrigger") &&
				((string?)element.Attribute("Binding") == "{Binding Tag, RelativeSource={RelativeSource Self}}") &&
				((string?)element.Attribute("Value") == "True"));
		AssertSetter(highContrastTrigger, "Foreground", "{DynamicResource PrimaryTextBrush}");
		Assert.Same(highContrastTrigger, titleTriggers[^1]);

		XElement[] highContrastTaggedElements = window.Descendants()
			.Where(element => ((string?)element.Attribute("Tag"))?.Contains(
				"SystemParameters.HighContrastKey", StringComparison.Ordinal) == true)
			.ToArray();
		Assert.Contains(highContrastTaggedElements,
			element => element.Name == Presentation + "ItemsControl");
		Assert.Contains("{DynamicResource DefaultProviderAccentBrush}",
			controlsXaml, StringComparison.Ordinal);

		foreach (string providerName in new[]
		{
			"Claude", "Codex", "Copilot", "Antigravity", "Grok"
		})
		{
			string providerValue = $"{{x:Static models:ProviderKind.{providerName}}}";
			XElement normalTrigger = Assert.Single(
				titleTriggers,
				element =>
					(element.Name == Presentation + "DataTrigger") &&
					((string?)element.Attribute("Value") == providerValue));
			Assert.Equal("{Binding Provider}", (string?)normalTrigger.Attribute("Binding"));
			AssertSetter(normalTrigger, "Foreground", $"{{DynamicResource {providerName}ProviderTextBrush}}");
			Assert.Contains($"{{DynamicResource {providerName}ProviderAccentBrush}}",
				controlsXaml, StringComparison.Ordinal);
		}

		Assert.DoesNotContain("DataContext.AccentColor", controlsXaml, StringComparison.Ordinal);
		AssertHighContrastOverrideAfterProviderAccents(controlsXaml);
	}

	[Fact]
	public void AccountTitle_ColorsOnlyProviderName()
	{
		XDocument window = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root, "src", "AiUsageDashboard.App", "FloatingWidgetWindow.xaml"));
		XElement title = Assert.Single(
			window.Descendants(Presentation + "TextBlock"),
			element => (string?)element.Attribute(Xaml + "Name") == "AccountTitleText");
		Assert.Equal("{StaticResource AccountTitleStyle}", (string?)title.Attribute("Style"));
		XElement[] titleRuns = title.Descendants(Presentation + "Run").ToArray();
		Assert.Equal(2, titleRuns.Length);
		Assert.Equal("{Binding ProviderName, Mode=OneWay}", (string?)titleRuns[0].Attribute("Text"));
		Assert.Null(titleRuns[0].Attribute("Foreground"));
		Assert.Equal("{Binding AccountHeaderSuffixText, Mode=OneWay}", (string?)titleRuns[1].Attribute("Text"));
		Assert.Equal("{DynamicResource PrimaryTextBrush}", (string?)titleRuns[1].Attribute("Foreground"));
		Assert.Equal("{DynamicResource {x:Static system:SystemParameters.HighContrastKey}}",
			(string?)title.Attribute("Tag"));
		Assert.Null(title.Attribute("Foreground"));
	}

	private static ResourceDictionary LoadPalette(string fileName)
	{
		string xaml = File.ReadAllText(GetPalettePath(fileName));

		return Assert.IsType<ResourceDictionary>(XamlReader.Parse(xaml));
	}

	private static Color GetColor(
		ResourceDictionary palette,
		string resourceKey)
	{
		return Assert.IsType<Color>(palette[resourceKey]);
	}

	private static string[] GetResourceKeys(string fileName)
	{
		XDocument palette = XDocument.Load(GetPalettePath(fileName));

		return palette.Root!
			.Elements()
			.Select(element => (string?)element.Attribute(Xaml + "Key"))
			.Where(resourceKey => resourceKey is not null)
			.Cast<string>()
			.OrderBy(resourceKey => resourceKey, StringComparer.Ordinal)
			.ToArray();
	}

	private static Color Composite(Color foreground, Color background)
	{
		double alpha = foreground.A / 255d;
		return Color.FromArgb(
			byte.MaxValue,
			BlendColorComponent(foreground.R, background.R, alpha),
			BlendColorComponent(foreground.G, background.G, alpha),
			BlendColorComponent(foreground.B, background.B, alpha));
	}

	private static byte BlendColorComponent(
		byte foreground,
		byte background,
		double alpha)
	{
		return (byte)Math.Round(
			(foreground * alpha) + (background * (1 - alpha)),
			MidpointRounding.AwayFromZero);
	}

	private static XElement GetImplicitStyle(
		XDocument document,
		string targetType)
	{
		return Assert.Single(
			document.Root!.Elements(Presentation + "Style"),
			element =>
				element.Attribute(Xaml + "Key") is null &&
				string.Equals(
					(string?)element.Attribute("TargetType"),
					$"{{x:Type {targetType}}}",
					StringComparison.Ordinal));
	}

	private static XElement GetKeyedStyle(
		XDocument document,
		string resourceKey)
	{
		return Assert.Single(
			document.Root!.Elements(Presentation + "Style"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Key"),
				resourceKey,
				StringComparison.Ordinal));
	}

	private static void AssertSetter(
		XElement scope,
		string property,
		string value)
	{
		Assert.Single(
			scope.Elements(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					property,
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					value,
					StringComparison.Ordinal));
	}

	private static void AssertTemplateTriggerSetter(
		XElement style,
		string triggerProperty,
		string targetName,
		string setterProperty,
		string value)
	{
		XElement trigger = Assert.Single(
			style.Descendants(Presentation + "Trigger"),
			element =>
				string.Equals(
					(string?)element.Attribute("Property"),
					triggerProperty,
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)element.Attribute("Value"),
					"True",
					StringComparison.Ordinal));

		Assert.Single(
			trigger.Elements(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("TargetName"),
					targetName,
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Property"),
					setterProperty,
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					value,
					StringComparison.Ordinal));
	}

	private static void AssertDataTriggerSetter(
		XElement style,
		string triggerValue,
		string setterProperty,
		string setterValue)
	{
		XElement trigger = Assert.Single(
			style.Descendants(Presentation + "DataTrigger"),
			element => string.Equals(
				(string?)element.Attribute("Value"),
				triggerValue,
				StringComparison.Ordinal));

		AssertSetter(trigger, setterProperty, setterValue);
	}

	private static void AssertHighContrastOverrideAfterProviderAccents(
		string xaml)
	{
		int lastProviderAccent = xaml.LastIndexOf(
			"ProviderAccentBrush}",
			StringComparison.Ordinal);
		int highContrastOverride = xaml.IndexOf(
			"Value=\"{DynamicResource AccentBrush}\"",
			lastProviderAccent,
			StringComparison.Ordinal);

		Assert.True(lastProviderAccent >= 0);
		Assert.True(
			highContrastOverride > lastProviderAccent,
			"High Contrast must override provider accents after their triggers.");
	}

	private static void AssertMinimumContrast(
		Color foreground,
		Color background,
		double minimum,
		string description)
	{
		double actual = GetContrastRatio(foreground, background);

		Assert.True(
			actual >= minimum,
			$"{description}: expected at least {minimum:F2}:1, actual {actual:F2}:1.");
	}

	private static double GetContrastRatio(
		Color first,
		Color second)
	{
		double firstLuminance = GetRelativeLuminance(first);
		double secondLuminance = GetRelativeLuminance(second);
		double lighter = Math.Max(firstLuminance, secondLuminance);
		double darker = Math.Min(firstLuminance, secondLuminance);

		return (lighter + 0.05) / (darker + 0.05);
	}

	private static double GetRelativeLuminance(Color color)
	{
		return
			(0.2126 * GetLinearColorComponent(color.R)) +
			(0.7152 * GetLinearColorComponent(color.G)) +
			(0.0722 * GetLinearColorComponent(color.B));
	}

	private static double GetLinearColorComponent(byte component)
	{
		double normalized = component / 255d;
		return normalized <= 0.04045
			? normalized / 12.92
			: Math.Pow((normalized + 0.055) / 1.055, 2.4);
	}

	private static string GetSystemColorMember(
		XDocument palette,
		string resourceKey)
	{
		XElement color = Assert.Single(
			palette.Root!.Elements(Xaml + "Static"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Key"),
				resourceKey,
				StringComparison.Ordinal));
		string member = Assert.IsType<string>((string?)color.Attribute("Member"));
		int typeSeparator = member.IndexOf(':');

		Assert.True(typeSeparator > 0);
		Assert.Equal(
			"clr-namespace:System.Windows;assembly=PresentationFramework",
			color.GetNamespaceOfPrefix(member[..typeSeparator])?.NamespaceName);
		const string typeName = "SystemColors.";
		string memberReference = member[(typeSeparator + 1)..];
		Assert.StartsWith(typeName, memberReference, StringComparison.Ordinal);
		return memberReference[typeName.Length..];
	}

	private static string GetPalettePath(string fileName)
	{
		return Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Themes",
			fileName);
	}
}
