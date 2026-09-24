using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class PointerFocusReleaseRegressionTests
{
	private const string ReleaseProperty =
		"interaction:PointerFocusRelease.ReleaseAfterPointerAction";
	private static readonly XNamespace Presentation =
		"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Xaml =
		"http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void PointerMouseUp_ClearsLogicalFocusEvenWhenEventWasHandled()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			FrameworkElement[] operationElements =
			[
				new Button(),
				new ToggleButton(),
				new CheckBox(),
				new MenuItem()
			];

			foreach (FrameworkElement operationElement in operationElements)
			{
				focusScope.Children.Add(operationElement);
				EnablePointerFocusRelease(operationElement);
				FocusManager.SetFocusedElement(focusScope, operationElement);

				RaiseMouseButtonEvent(
					operationElement,
					UIElement.PreviewMouseLeftButtonDownEvent,
					isHandled: true);
				RaiseMouseButtonEvent(
					operationElement,
					UIElement.MouseLeftButtonUpEvent,
					isHandled: true);
				DrainDispatcher();

				Assert.Null(FocusManager.GetFocusedElement(focusScope));
				focusScope.Children.Remove(operationElement);
			}
		});
	}

	[Fact]
	public void ProgrammaticClick_PreservesLogicalFocus()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button button = new();
			focusScope.Children.Add(button);
			EnablePointerFocusRelease(button);
			FocusManager.SetFocusedElement(focusScope, button);

			button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
			DrainDispatcher();

			Assert.Same(
				button,
				FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void AsyncFocusTransition_DoesNotTouchFocusAfterNewUserInput()
	{
		Assert.Equal(
			FloatingWidgetWindow.FocusTransitionMode.Release,
			FloatingWidgetWindow.ResolveAsyncFocusTransition(
				FloatingWidgetWindow.FocusTransitionMode.Release,
				capturedInteractionGeneration: 4,
				currentInteractionGeneration: 4));
		Assert.Equal(
			FloatingWidgetWindow.FocusTransitionMode.Transfer,
			FloatingWidgetWindow.ResolveAsyncFocusTransition(
				FloatingWidgetWindow.FocusTransitionMode.Transfer,
				capturedInteractionGeneration: 4,
				currentInteractionGeneration: 4));
		Assert.Equal(
			FloatingWidgetWindow.FocusTransitionMode.PreserveCurrent,
			FloatingWidgetWindow.ResolveAsyncFocusTransition(
				FloatingWidgetWindow.FocusTransitionMode.Release,
				capturedInteractionGeneration: 4,
				currentInteractionGeneration: 5));
		Assert.Equal(
			FloatingWidgetWindow.FocusTransitionMode.PreserveCurrent,
			FloatingWidgetWindow.ResolveAsyncFocusTransition(
				FloatingWidgetWindow.FocusTransitionMode.Transfer,
				capturedInteractionGeneration: 4,
				currentInteractionGeneration: 5));
	}

	[Theory]
	[InlineData(MouseButton.Left)]
	[InlineData(MouseButton.Right)]
	public void ContextMenuLeafDelayedClick_ReportsPointerOnlyForThatClick(
		MouseButton button)
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button placementTarget = new();
			focusScope.Children.Add(placementTarget);
			ContextMenu contextMenu = new()
			{
				PlacementTarget = placementTarget
			};
			MenuItem menuItem = new();
			contextMenu.Items.Add(menuItem);
			EnablePointerFocusRelease(menuItem);
			List<bool> pointerOrigins = [];
			menuItem.Click += (sender, e) =>
				pointerOrigins.Add(WasInvokedByPointer(menuItem));
			FocusManager.SetFocusedElement(focusScope, placementTarget);
			RoutedEvent pointerDownEvent = button == MouseButton.Left
				? UIElement.PreviewMouseLeftButtonDownEvent
				: UIElement.PreviewMouseRightButtonDownEvent;
			RoutedEvent pointerUpEvent = button == MouseButton.Left
				? UIElement.MouseLeftButtonUpEvent
				: UIElement.MouseRightButtonUpEvent;

			RaiseMouseButtonEvent(
				menuItem,
				pointerDownEvent,
				isHandled: true,
				button);
			InvokeMenuItemClick(menuItem);
			RaiseMouseButtonEvent(
				menuItem,
				pointerUpEvent,
				isHandled: true,
				button);
			DrainDispatcher();

			Assert.Equal([true], pointerOrigins);
			Assert.False(WasInvokedByPointer(menuItem));
			Assert.Null(FocusManager.GetFocusedElement(focusScope));

			FocusManager.SetFocusedElement(focusScope, placementTarget);
			menuItem.RaiseEvent(new RoutedEventArgs(
				MenuItem.ClickEvent,
				menuItem));
			DrainDispatcher();

			Assert.Equal([true, false], pointerOrigins);
			Assert.Same(
				placementTarget,
				FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void ComboBoxPopupItemPointerSelection_ReleasesLogicalFocus()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			ComboBox comboBox = new();
			ComboBoxItem item = new();
			comboBox.Items.Add(item);
			focusScope.Children.Add(comboBox);
			EnablePointerFocusRelease(comboBox);
			FocusManager.SetFocusedElement(focusScope, comboBox);

			RaiseMouseButtonEvent(
				item,
				UIElement.PreviewMouseLeftButtonDownEvent,
				isHandled: true);

			DependencyProperty pointerPressProperty =
				GetPointerFocusReleaseProperty(
					"IsPointerPressActiveProperty",
					BindingFlags.NonPublic | BindingFlags.Static);
			Assert.True((bool)comboBox.GetValue(pointerPressProperty));

			MethodInfo dropDownClosedHandler = GetPointerFocusReleaseType()
				.GetMethod(
					"ComboBox_DropDownClosed",
					BindingFlags.NonPublic | BindingFlags.Static)!;
			dropDownClosedHandler.Invoke(
				null,
				[comboBox, EventArgs.Empty]);
			DrainDispatcher();

			Assert.Null(FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void ContextMenuLeafPointerAction_ReleasesPlacementTargetFocus()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button placementTarget = new();
			focusScope.Children.Add(placementTarget);
			ContextMenu contextMenu = new()
			{
				PlacementTarget = placementTarget
			};
			MenuItem leaf = new();
			contextMenu.Items.Add(leaf);
			EnablePointerFocusRelease(leaf);
			FocusManager.SetFocusedElement(focusScope, placementTarget);

			RaiseMouseButtonEvent(
				leaf,
				UIElement.PreviewMouseLeftButtonDownEvent,
				isHandled: true);
			InvokePointerFocusReleaseHandler(
				"ContextMenu_Closed",
				contextMenu,
				new RoutedEventArgs());
			DrainDispatcher();

			Assert.Null(FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void ContextMenuLeafRightPointerAction_ReleasesPlacementTargetFocus()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button placementTarget = new();
			focusScope.Children.Add(placementTarget);
			ContextMenu contextMenu = new()
			{
				PlacementTarget = placementTarget
			};
			MenuItem leaf = new();
			contextMenu.Items.Add(leaf);
			EnablePointerFocusRelease(leaf);
			FocusManager.SetFocusedElement(focusScope, placementTarget);

			RaiseMouseButtonEvent(
				leaf,
				UIElement.PreviewMouseRightButtonDownEvent,
				isHandled: true,
				MouseButton.Right);
			RaiseMouseButtonEvent(
				leaf,
				UIElement.MouseRightButtonUpEvent,
				isHandled: true,
				MouseButton.Right);
			DrainDispatcher();

			Assert.Null(FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void MenuItemRightPointerOutsideContextMenu_DoesNotArmOrRelease()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button placementTarget = new();
			Menu menu = new();
			MenuItem menuItem = new();
			menu.Items.Add(menuItem);
			focusScope.Children.Add(placementTarget);
			focusScope.Children.Add(menu);
			EnablePointerFocusRelease(menuItem);
			FocusManager.SetFocusedElement(focusScope, placementTarget);

			RaiseMouseButtonEvent(
				menuItem,
				UIElement.PreviewMouseRightButtonDownEvent,
				isHandled: true,
				MouseButton.Right);
			RaiseMouseButtonEvent(
				menuItem,
				UIElement.MouseRightButtonUpEvent,
				isHandled: true,
				MouseButton.Right);
			DrainDispatcher();

			Assert.False(WasInvokedByPointer(menuItem));
			Assert.Same(
				placementTarget,
				FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void ContextMenuSubmenuHeaderPointerAction_PreservesPlacementTargetFocus()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button placementTarget = new();
			focusScope.Children.Add(placementTarget);
			ContextMenu contextMenu = new()
			{
				PlacementTarget = placementTarget
			};
			MenuItem submenuHeader = new();
			submenuHeader.Items.Add(new MenuItem());
			contextMenu.Items.Add(submenuHeader);
			EnablePointerFocusRelease(submenuHeader);
			FocusManager.SetFocusedElement(focusScope, placementTarget);

			RaiseMouseButtonEvent(
				submenuHeader,
				UIElement.PreviewMouseLeftButtonDownEvent,
				isHandled: true);
			RaiseMouseButtonEvent(
				submenuHeader,
				UIElement.MouseLeftButtonUpEvent,
				isHandled: true);
			DrainDispatcher();

			Assert.Same(
				placementTarget,
				FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void ScopedPointerRelease_DoesNotClearNewlyFocusedInput()
	{
		RunOnStaThread(() =>
		{
			Grid focusScope = new();
			FocusManager.SetIsFocusScope(focusScope, true);
			Button completedAction = new();
			TextBox newInput = new();
			focusScope.Children.Add(completedAction);
			focusScope.Children.Add(newInput);
			FocusManager.SetFocusedElement(focusScope, newInput);

			InvokePointerFocusReleaseHandler(
				"ReleaseFocusIfStillOwned",
				completedAction,
				null!);

			Assert.Same(
				newInput,
				FocusManager.GetFocusedElement(focusScope));
		});
	}

	[Fact]
	public void ConsecutiveModals_DeferReleaseUntilFinalFocusRestoration()
	{
		RunOnStaThread(() =>
		{
			Window ownerWindow = new()
			{
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.ToolWindow,
				Left = -10000,
				Top = -10000,
				Width = 1,
				Height = 1
			};
			Button placementTarget = new();
			ownerWindow.Content = placementTarget;
			ownerWindow.Show();

			try
			{
				ownerWindow.UpdateLayout();
				Assert.True(ownerWindow.IsVisible);
				Assert.True(ownerWindow.IsActive);
				DependencyObject focusScope =
					FocusManager.GetFocusScope(placementTarget);
				FocusManager.SetFocusedElement(
					focusScope,
					placementTarget);
				ContextMenu contextMenu = new()
				{
					PlacementTarget = placementTarget
				};
				Window modalWindow = new()
				{
					Owner = ownerWindow,
					ShowInTaskbar = false,
					WindowStyle = WindowStyle.ToolWindow,
					Left = -10000,
					Top = -10000,
					Width = 1,
					Height = 1,
					Content = new Border()
				};
				bool? focusWasPreservedWhileModal = null;
				Exception? modalFailure = null;
				_ = Dispatcher.CurrentDispatcher.BeginInvoke(
					DispatcherPriority.Loaded,
					new Action(
						() =>
						{
							try
							{
								if (!modalWindow.IsVisible || ownerWindow.IsActive)
								{
									throw new InvalidOperationException(
										"The modal owner did not become inactive.");
								}

								InvokePointerFocusReleaseHandler(
									"QueueFocusRelease",
									contextMenu,
									placementTarget);
								_ = Dispatcher.CurrentDispatcher.BeginInvoke(
									DispatcherPriority.ApplicationIdle,
									new Action(
										() =>
										{
											try
											{
												focusWasPreservedWhileModal =
													ReferenceEquals(
														placementTarget,
														FocusManager.GetFocusedElement(
															focusScope));
											}
											catch (Exception exception)
											{
												modalFailure = exception;
											}
											finally
											{
												modalWindow.Close();
											}
										}));
							}
							catch (Exception exception)
							{
								modalFailure = exception;
								modalWindow.Close();
							}
						}));

				_ = modalWindow.ShowDialog();

				if (modalFailure is not null)
				{
					ExceptionDispatchInfo.Capture(modalFailure).Throw();
				}

				Assert.True(focusWasPreservedWhileModal == true);
				Assert.Same(
					placementTarget,
					FocusManager.GetFocusedElement(focusScope));

				Window secondModalWindow = new()
				{
					Owner = ownerWindow,
					ShowInTaskbar = false,
					WindowStyle = WindowStyle.ToolWindow,
					Left = -10000,
					Top = -10000,
					Width = 1,
					Height = 1,
					Content = new Border()
				};
				_ = Dispatcher.CurrentDispatcher.BeginInvoke(
					DispatcherPriority.ApplicationIdle,
					new Action(secondModalWindow.Close));
				_ = secondModalWindow.ShowDialog();
				Assert.Same(
					placementTarget,
					FocusManager.GetFocusedElement(focusScope));
				DrainDispatcher();

				Assert.Null(FocusManager.GetFocusedElement(focusScope));

				FocusManager.SetFocusedElement(
					focusScope,
					placementTarget);
				Window unrelatedModalWindow = new()
				{
					Owner = ownerWindow,
					ShowInTaskbar = false,
					WindowStyle = WindowStyle.ToolWindow,
					Left = -10000,
					Top = -10000,
					Width = 1,
					Height = 1,
					Content = new Border()
				};
				_ = Dispatcher.CurrentDispatcher.BeginInvoke(
					DispatcherPriority.ApplicationIdle,
					new Action(unrelatedModalWindow.Close));
				_ = unrelatedModalWindow.ShowDialog();
				DrainDispatcher();

				Assert.Same(
					placementTarget,
					FocusManager.GetFocusedElement(focusScope));
			}
			finally
			{
				ownerWindow.Close();
			}
		});
	}

	[Fact]
	public void OperationStyles_CoverEveryPointerActionWithoutDisablingKeyboardInput()
	{
		XDocument controls = LoadAppXaml("Themes", "Controls.xaml");
		XDocument floatingWindow = LoadAppXaml("FloatingWidgetWindow.xaml");
		XDocument setupControls = LoadSetupXaml(
			"Themes",
			"SetupControls.xaml");

		AssertReleaseSetter(GetKeyedStyle(controls, "DefaultButtonStyle"));
		AssertReleaseSetter(GetImplicitStyle(controls, "ComboBox"));
		AssertReleaseSetter(GetImplicitStyle(controls, "CheckBox"));
		AssertReleaseSetter(GetImplicitStyle(controls, "MenuItem"));
		AssertReleaseSetter(GetKeyedStyle(floatingWindow, "PinButtonStyle"));
		AssertReleaseSetter(
			GetKeyedStyle(setupControls, "SetupPrimaryButtonStyle"));
		AssertReleaseSetter(
			GetKeyedStyle(setupControls, "SetupGhostButtonStyle"));

		XDocument[] appWindows =
		[
			LoadAppXaml("AccountEditorWindow.xaml"),
			LoadAppXaml("CodexWorkspacePromptWindow.xaml"),
			LoadAppXaml("CodexResetCreditsWindow.xaml"),
			LoadAppXaml("AboutWindow.xaml"),
			floatingWindow
		];
		XDocument setupWindow = LoadSetupXaml("SetupWindow.xaml");
		XDocument[] windows = appWindows.Append(setupWindow).ToArray();
		XElement[] buttonBaseActions = windows
			.SelectMany(GetButtonBaseActions)
			.ToArray();
		XElement[] comboBoxActions = windows
			.SelectMany(document => document.Descendants(
				Presentation + "ComboBox"))
			.Where(element => element.Attribute("SelectionChanged") is not null)
			.ToArray();
		XElement[] menuItemActions = windows
			.SelectMany(document => document.Descendants(
				Presentation + "MenuItem"))
			.Where(element => element.Attribute("Click") is not null)
			.ToArray();

		Assert.NotEmpty(buttonBaseActions);
		Assert.Single(comboBoxActions);
		Assert.Equal(26, menuItemActions.Length);
		string[] updateActionAutomationIds =
		[
			"CheckForUpdates",
			"UpdatePrimaryAction",
			"UpdateReleaseHistory",
			"UpdateSnooze",
			"DisableAutomaticUpdateChecks"
		];
		Assert.All(
			updateActionAutomationIds,
			automationId => Assert.Contains(
				buttonBaseActions,
				element => string.Equals(
					(string?)element.Attribute(
						"AutomationProperties.AutomationId"),
					automationId,
					StringComparison.Ordinal)));
		Assert.All(
			buttonBaseActions
				.Concat(comboBoxActions)
				.Concat(menuItemActions),
			element =>
			{
				Assert.NotEqual("False", (string?)element.Attribute("Focusable"));
				Assert.NotEqual("False", (string?)element.Attribute("IsTabStop"));
			});

		Dictionary<string, XElement> appStyles = CreateResourceStyleMap(
			controls,
			floatingWindow);
		Dictionary<string, XElement> setupStyles = CreateResourceStyleMap(
			setupControls);
		Assert.All(
			appWindows.SelectMany(GetPointerActions),
			element => Assert.True(
				DoesEffectiveStyleReleasePointerFocus(element, appStyles),
				$"{element.Name.LocalName} action is not connected to the pointer focus release policy."));
		Assert.All(
			GetPointerActions(setupWindow),
			element => Assert.True(
				DoesEffectiveStyleReleasePointerFocus(element, setupStyles),
				$"{element.Name.LocalName} action is not connected to the pointer focus release policy."));
	}

	[Fact]
	public void EffectiveStyleResolver_RespectsDerivedFalseOverride()
	{
		XElement baseStyle = new(
			Presentation + "Style",
			new XAttribute(Xaml + "Key", "BaseButtonStyle"),
			new XAttribute("TargetType", "{x:Type Button}"),
			new XElement(
				Presentation + "Setter",
				new XAttribute("Property", ReleaseProperty),
				new XAttribute("Value", "True")));
		XElement derivedStyle = new(
			Presentation + "Style",
			new XAttribute(Xaml + "Key", "DerivedButtonStyle"),
			new XAttribute("TargetType", "{x:Type Button}"),
			new XAttribute("BasedOn", "{StaticResource BaseButtonStyle}"),
			new XElement(
				Presentation + "Setter",
				new XAttribute("Property", ReleaseProperty),
				new XAttribute("Value", "False")));
		Dictionary<string, XElement> styles = new(StringComparer.Ordinal)
		{
			["BaseButtonStyle"] = baseStyle,
			["DerivedButtonStyle"] = derivedStyle
		};

		Assert.False(DoesStyleReleasePointerFocus(
			derivedStyle,
			styles,
			new HashSet<string>(StringComparer.Ordinal)));
	}

	[Fact]
	public void PointerPolicy_ReleasesKeyboardAndLogicalFocusAfterCompletedActions()
	{
		string source = LoadAppSource("PointerFocusRelease.cs");
		string setupProject = LoadSetupSource(
			"AiUsageDashboard.Antigravity.Setup.csproj");

		Assert.Contains(
			"UIElement.PreviewMouseLeftButtonDownEvent",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"UIElement.MouseLeftButtonUpEvent",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"handledEventsToo: true",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"comboBox.DropDownClosed += ComboBox_DropDownClosed;",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"EventManager.RegisterClassHandler(",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"ComboBoxItem_PreviewMouseLeftButtonDown",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"contextMenu.Closed += ContextMenu_Closed;",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"FocusManager.SetFocusedElement(focusScope, null);",
			source,
			StringComparison.Ordinal);
		Assert.Contains("Keyboard.ClearFocus();", source, StringComparison.Ordinal);
		Assert.Contains(
			"SetIsPointerPressActive(operationElement, false);",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"OperationElement_PreviewKeyDown",
			source,
			StringComparison.Ordinal);
		Assert.Contains(
			"..\\AiUsageDashboard.App\\PointerFocusRelease.cs",
			setupProject,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ContextMenuPolicy_ReleasesLeafActionsButNotSubmenuHeaders()
	{
		XDocument floatingWindow = LoadAppXaml("FloatingWidgetWindow.xaml");
		string policySource = LoadAppSource("PointerFocusRelease.cs");
		XElement[] menuItems = floatingWindow
			.Descendants(Presentation + "MenuItem")
			.ToArray();
		XElement[] leafActions = menuItems
			.Where(element => element.Attribute("Click") is not null)
			.ToArray();
		XElement[] submenuHeaders = menuItems
			.Where(element =>
				element.Attribute("Click") is null &&
				element.Elements(Presentation + "MenuItem").Any())
			.ToArray();

		Assert.Equal(26, leafActions.Length);
		XElement aboutAction = Assert.Single(leafActions, element =>
			(string?)element.Attribute("AutomationProperties.AutomationId") == "OpenAbout");
		Assert.Empty(aboutAction.Elements(Presentation + "MenuItem"));
		Assert.Equal(3, submenuHeaders.Length);
		Assert.Contains(
			"!menuItem.HasItems",
			policySource,
			StringComparison.Ordinal);
		Assert.Contains(
			"menuItem.HasItems",
			policySource,
			StringComparison.Ordinal);
		Assert.Contains(
			"contextMenu.PlacementTarget",
			policySource,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				@"ContextMenu_PreviewKeyDown[\s\S]*?" +
					@"ReleasePlacementTargetOnClosedProperty,\s*false",
				RegexOptions.CultureInvariant),
			policySource);
	}

	[Fact]
	public void ViewTransitions_SuppressPointerHandoffAndPreserveKeyboardHandoff()
	{
		string floatingSource = LoadAppSource("FloatingWidgetWindow.xaml.cs");
		string setupSource = LoadSetupSource("SetupWindow.xaml.cs");

		Assert.Contains(
			"focusTransitionMode == FocusTransitionMode.Transfer",
			floatingSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"PointerFocusRelease.ReleaseFocusIfStillOwned(",
			floatingSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"focusTransitionMode: FocusTransitionMode.Release",
			floatingSource,
			StringComparison.Ordinal);
		Assert.True(
			Regex.Matches(
				floatingSource,
				@"PointerFocusRelease\.WasInvokedByPointer\(sender\)",
				RegexOptions.CultureInvariant).Count >= 4);
		Assert.Contains(
			"ResolveAsyncFocusTransition(",
			floatingSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"InputManager.Current.PreProcessInput += InputManager_PreProcessInput;",
			floatingSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;",
			floatingSource,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				@"internal void BeginPortableSettingsImportUndo\(\)\s*" +
					@"\{\s*BeginPortableSettingsImportUndo\(\s*" +
					@"FocusTransitionMode\.PreserveCurrent,",
				RegexOptions.CultureInvariant),
			floatingSource);

		string[] setupTransitionHandlers =
		[
			"ApproveButton_Click",
			"CancelButton_Click",
			"OpenAiUsageButton_Click",
			"RetryButton_Click",
			"StartButton_Click"
		];

		Assert.All(
			setupTransitionHandlers,
			handler => Assert.Matches(
				new Regex(
					@"private\s+async\s+void\s+" +
						Regex.Escape(handler) +
						@"\s*\([^{}]*\)\s*\{\s*" +
						@"CaptureOperationFocusMode\(sender\);",
					RegexOptions.CultureInvariant),
				setupSource));
		Assert.Contains(
			"if (!_shouldTransferOperationFocus)",
			setupSource,
			StringComparison.Ordinal);
		Assert.Matches(
			new Regex(
				@"if \(!_shouldTransferOperationFocus\)\s*\{\s*" +
					@"return ReferenceEquals\(visiblePanel, ProgressPanel\)\s*" +
					@"\? ProgressIndicator\s*:\s*null;",
				RegexOptions.CultureInvariant),
			setupSource);
		Assert.Matches(
			new Regex(
				@"OnPreviewKeyDown\(KeyEventArgs e\)\s*\{\s*" +
					@"_shouldTransferOperationFocus = true;",
				RegexOptions.CultureInvariant),
			setupSource);
	}

	private static IEnumerable<XElement> GetButtonBaseActions(
		XDocument document)
	{
		return document.Descendants().Where(element =>
		{
			string elementName = element.Name.LocalName;
			bool isButtonBase = elementName is
				"Button" or "ToggleButton" or "CheckBox";
			bool hasAction = element.Attribute("Click") is not null ||
				element.Attribute("Checked") is not null ||
				element.Attribute("Unchecked") is not null;
			return isButtonBase && hasAction;
		});
	}

	private static IEnumerable<XElement> GetPointerActions(XDocument document)
	{
		return GetButtonBaseActions(document)
			.Concat(document.Descendants(Presentation + "ComboBox")
				.Where(element =>
					element.Attribute("SelectionChanged") is not null))
			.Concat(document.Descendants(Presentation + "MenuItem")
				.Where(element => element.Attribute("Click") is not null));
	}

	private static void EnablePointerFocusRelease(DependencyObject element)
	{
		DependencyProperty property = GetPointerFocusReleaseProperty(
			"ReleaseAfterPointerActionProperty",
			BindingFlags.Public | BindingFlags.Static);
		element.SetValue(property, true);
	}

	private static bool WasInvokedByPointer(object sender)
	{
		MethodInfo method = GetPointerFocusReleaseType().GetMethod(
			"WasInvokedByPointer",
			BindingFlags.NonPublic | BindingFlags.Static)!;
		return (bool)method.Invoke(null, [sender])!;
	}

	private static void InvokeMenuItemClick(MenuItem menuItem)
	{
		MethodInfo clickItem = typeof(MenuItem).GetMethod(
			"ClickItem",
			BindingFlags.NonPublic | BindingFlags.Instance,
			binder: null,
			types: Type.EmptyTypes,
			modifiers: null)!;
		clickItem.Invoke(menuItem, null);
	}

	private static Type GetPointerFocusReleaseType()
	{
		return typeof(FloatingWidgetWindow).Assembly.GetType(
			"AiUsageDashboard.Presentation.PointerFocusRelease",
			throwOnError: true)!;
	}

	private static DependencyProperty GetPointerFocusReleaseProperty(
		string fieldName,
		BindingFlags bindingFlags)
	{
		FieldInfo propertyField = GetPointerFocusReleaseType().GetField(
			fieldName,
			bindingFlags)!;
		return (DependencyProperty)propertyField.GetValue(null)!;
	}

	private static void InvokePointerFocusReleaseHandler(
		string methodName,
		params object[] arguments)
	{
		MethodInfo handler = GetPointerFocusReleaseType().GetMethod(
			methodName,
			BindingFlags.NonPublic | BindingFlags.Static)!;
		handler.Invoke(null, arguments);
	}

	private static void RaiseMouseButtonEvent(
		UIElement target,
		RoutedEvent routedEvent,
		bool isHandled,
		MouseButton button = MouseButton.Left)
	{
		MouseButtonEventArgs eventArgs = new(
			Mouse.PrimaryDevice,
			Environment.TickCount,
			button)
		{
			RoutedEvent = routedEvent,
			Source = target,
			Handled = isHandled
		};
		target.RaiseEvent(eventArgs);
	}

	private static void DrainDispatcher()
	{
		Dispatcher.CurrentDispatcher.Invoke(
			DispatcherPriority.ApplicationIdle,
			new Action(() => { }));
	}

	private static void RunOnStaThread(Action action)
	{
		Exception? failure = null;
		Dispatcher? dispatcher = null;
		using ManualResetEventSlim dispatcherReady = new(false);
		Thread thread = new(() =>
		{
			dispatcher = Dispatcher.CurrentDispatcher;
			dispatcherReady.Set();

			try
			{
				action();
			}
			catch (Exception exception)
			{
				failure = exception;
			}
		});
		thread.IsBackground = true;
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert.True(
			dispatcherReady.Wait(TimeSpan.FromSeconds(1)),
			"STA Dispatcher did not start within one second.");

		if (!thread.Join(TimeSpan.FromSeconds(10)))
		{
			dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
			bool stoppedAfterShutdown = thread.Join(TimeSpan.FromSeconds(2));
			Assert.Fail(
				$"STA action exceeded ten seconds; " +
				$"shutdownStoppedThread={stoppedAfterShutdown}.");
		}

		if (failure is not null)
		{
			ExceptionDispatchInfo.Capture(failure).Throw();
		}
	}

	private static void AssertReleaseSetter(XElement style)
	{
		Assert.Contains(
			style.Elements(Presentation + "Setter"),
			setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					ReleaseProperty,
					StringComparison.Ordinal) &&
				string.Equals(
					(string?)setter.Attribute("Value"),
					"True",
					StringComparison.Ordinal));
	}

	private static Dictionary<string, XElement> CreateResourceStyleMap(
		params XDocument[] documents)
	{
		return documents
			.SelectMany(GetResourceStyles)
			.ToDictionary(GetStyleKey, StringComparer.Ordinal);
	}

	private static IEnumerable<XElement> GetResourceStyles(XDocument document)
	{
		XElement root = document.Root!;

		if (root.Name == Presentation + "ResourceDictionary")
		{
			return root.Elements(Presentation + "Style");
		}

		return root
			.Elements(Presentation + $"{root.Name.LocalName}.Resources")
			.Elements(Presentation + "Style");
	}

	private static string GetStyleKey(XElement style)
	{
		return (string?)style.Attribute(Xaml + "Key") ??
			(string)style.Attribute("TargetType")!;
	}

	private static bool DoesEffectiveStyleReleasePointerFocus(
		XElement element,
		IReadOnlyDictionary<string, XElement> resourceStyles)
	{
		XElement? inlineStyle = element
			.Element(Presentation + $"{element.Name.LocalName}.Style")?
			.Element(Presentation + "Style");

		if (inlineStyle is not null)
		{
			return DoesStyleReleasePointerFocus(
				inlineStyle,
				resourceStyles,
				new HashSet<string>(StringComparer.Ordinal));
		}

		string styleKey = GetStaticResourceKey(
			(string?)element.Attribute("Style")) ??
			$"{{x:Type {element.Name.LocalName}}}";
		return resourceStyles.TryGetValue(styleKey, out XElement? style) &&
			DoesStyleReleasePointerFocus(
				style,
				resourceStyles,
				new HashSet<string>(StringComparer.Ordinal));
	}

	private static bool DoesStyleReleasePointerFocus(
		XElement style,
		IReadOnlyDictionary<string, XElement> resourceStyles,
		ISet<string> visitedStyleKeys)
	{
		XElement? localSetter = style
			.Elements(Presentation + "Setter")
			.LastOrDefault(setter =>
				string.Equals(
					(string?)setter.Attribute("Property"),
					ReleaseProperty,
					StringComparison.Ordinal));

		if (localSetter is not null)
		{
			return bool.TryParse(
				(string?)localSetter.Attribute("Value"),
				out bool releasesPointerFocus) &&
				releasesPointerFocus;
		}

		string? basedOnKey = GetStaticResourceKey(
			(string?)style.Attribute("BasedOn"));

		if ((basedOnKey is null) ||
			!visitedStyleKeys.Add(basedOnKey) ||
			!resourceStyles.TryGetValue(basedOnKey, out XElement? baseStyle))
		{
			return false;
		}

		return DoesStyleReleasePointerFocus(
			baseStyle,
			resourceStyles,
			visitedStyleKeys);
	}

	private static string? GetStaticResourceKey(string? markupExtension)
	{
		const string prefix = "{StaticResource ";

		if ((markupExtension is null) ||
			!markupExtension.StartsWith(prefix, StringComparison.Ordinal) ||
			!markupExtension.EndsWith('}'))
		{
			return null;
		}

		return markupExtension[prefix.Length..^1];
	}

	private static XElement GetImplicitStyle(
		XDocument document,
		string targetType)
	{
		return Assert.Single(
			document.Descendants(Presentation + "Style"),
			element =>
				(element.Attribute(Xaml + "Key") is null) &&
				string.Equals(
					(string?)element.Attribute("TargetType"),
					$"{{x:Type {targetType}}}",
					StringComparison.Ordinal));
	}

	private static XElement GetKeyedStyle(XDocument document, string key)
	{
		return Assert.Single(
			document.Descendants(Presentation + "Style"),
			element => string.Equals(
				(string?)element.Attribute(Xaml + "Key"),
				key,
				StringComparison.Ordinal));
	}

	private static XDocument LoadAppXaml(params string[] pathParts)
	{
		return XDocument.Load(GetAppPath(pathParts));
	}

	private static string LoadAppSource(params string[] pathParts)
	{
		return File.ReadAllText(GetAppPath(pathParts));
	}

	private static string GetAppPath(params string[] pathParts)
	{
		return Path.Combine(
			new[]
			{
				RepositoryTestPaths.Root,
				"src",
				"AiUsageDashboard.App"
			}.Concat(pathParts).ToArray());
	}

	private static XDocument LoadSetupXaml(params string[] pathParts)
	{
		return XDocument.Load(GetSetupPath(pathParts));
	}

	private static string LoadSetupSource(params string[] pathParts)
	{
		return File.ReadAllText(GetSetupPath(pathParts));
	}

	private static string GetSetupPath(params string[] pathParts)
	{
		return Path.Combine(
			new[]
			{
				RepositoryTestPaths.Root,
				"src",
				"AiUsageDashboard.Antigravity.Setup"
			}.Concat(pathParts).ToArray());
	}
}
