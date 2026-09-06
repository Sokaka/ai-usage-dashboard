using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace AiUsageDashboard.Presentation;

public static class PointerFocusRelease
{
	public static readonly DependencyProperty ReleaseAfterPointerActionProperty =
		DependencyProperty.RegisterAttached(
			"ReleaseAfterPointerAction",
			typeof(bool),
			typeof(PointerFocusRelease),
			new PropertyMetadata(
				false,
				OnReleaseAfterPointerActionChanged));

	private static readonly DependencyProperty IsPointerPressActiveProperty =
		DependencyProperty.RegisterAttached(
			"IsPointerPressActive",
			typeof(bool),
			typeof(PointerFocusRelease),
			new PropertyMetadata(false));

	private static readonly DependencyProperty IsContextMenuObservedProperty =
		DependencyProperty.RegisterAttached(
			"IsContextMenuObserved",
			typeof(bool),
			typeof(PointerFocusRelease),
			new PropertyMetadata(false));

	private static readonly DependencyProperty ReleasePlacementTargetOnClosedProperty =
		DependencyProperty.RegisterAttached(
			"ReleasePlacementTargetOnClosed",
			typeof(bool),
			typeof(PointerFocusRelease),
			new PropertyMetadata(false));

	private static readonly System.Windows.Input.KeyEventHandler
		PreviewKeyDownHandler =
		OperationElement_PreviewKeyDown;

	private static readonly MouseButtonEventHandler PreviewMouseButtonDownHandler =
		OperationElement_PreviewMouseButtonDown;

	private static readonly MouseButtonEventHandler MouseButtonUpHandler =
		OperationElement_MouseButtonUp;

	static PointerFocusRelease()
	{
		EventManager.RegisterClassHandler(
			typeof(WpfComboBoxItem),
			UIElement.PreviewMouseLeftButtonDownEvent,
			new MouseButtonEventHandler(
				ComboBoxItem_PreviewMouseLeftButtonDown),
			handledEventsToo: true);
	}

	public static bool GetReleaseAfterPointerAction(DependencyObject element)
	{
		ArgumentNullException.ThrowIfNull(element);
		return (bool)element.GetValue(ReleaseAfterPointerActionProperty);
	}

	public static void SetReleaseAfterPointerAction(
		DependencyObject element,
		bool value)
	{
		ArgumentNullException.ThrowIfNull(element);
		element.SetValue(ReleaseAfterPointerActionProperty, value);
	}

	internal static bool WasInvokedByPointer(object? sender)
	{
		return (sender is DependencyObject element) &&
			GetIsPointerPressActive(element);
	}

	internal static DependencyObject? GetPointerFocusOwner(object? sender)
	{
		if (sender is not DependencyObject operationElement)
		{
			return null;
		}

		if (operationElement is MenuItem menuItem)
		{
			return FindOwningContextMenu(menuItem)?.PlacementTarget ??
				operationElement;
		}

		return operationElement;
	}

	private static bool GetIsPointerPressActive(DependencyObject element)
	{
		return (bool)element.GetValue(IsPointerPressActiveProperty);
	}

	private static void SetIsPointerPressActive(
		DependencyObject element,
		bool value)
	{
		element.SetValue(IsPointerPressActiveProperty, value);
	}

	private static void OnReleaseAfterPointerActionChanged(
		DependencyObject element,
		DependencyPropertyChangedEventArgs e)
	{
		if ((element is not WpfButtonBase) &&
			(element is not MenuItem) &&
			(element is not WpfComboBox))
		{
			return;
		}

		if ((bool)e.NewValue)
		{
			Attach(element);
		}
		else
		{
			Detach(element);
		}
	}

	private static void Attach(DependencyObject element)
	{
		UIElement operationElement = (UIElement)element;
		operationElement.AddHandler(
			UIElement.PreviewKeyDownEvent,
			PreviewKeyDownHandler,
			handledEventsToo: true);
		operationElement.AddHandler(
			UIElement.PreviewMouseLeftButtonDownEvent,
			PreviewMouseButtonDownHandler,
			handledEventsToo: true);

		if (element is MenuItem)
		{
			operationElement.AddHandler(
				UIElement.PreviewMouseRightButtonDownEvent,
				PreviewMouseButtonDownHandler,
				handledEventsToo: true);
		}

		if (element is WpfComboBox comboBox)
		{
			comboBox.DropDownClosed += ComboBox_DropDownClosed;
			return;
		}

		operationElement.AddHandler(
			UIElement.MouseLeftButtonUpEvent,
			MouseButtonUpHandler,
			handledEventsToo: true);

		if (element is MenuItem)
		{
			operationElement.AddHandler(
				UIElement.MouseRightButtonUpEvent,
				MouseButtonUpHandler,
				handledEventsToo: true);
		}
	}

	private static void Detach(DependencyObject element)
	{
		UIElement operationElement = (UIElement)element;
		operationElement.RemoveHandler(
			UIElement.PreviewKeyDownEvent,
			PreviewKeyDownHandler);
		operationElement.RemoveHandler(
			UIElement.PreviewMouseLeftButtonDownEvent,
			PreviewMouseButtonDownHandler);

		if (element is MenuItem)
		{
			operationElement.RemoveHandler(
				UIElement.PreviewMouseRightButtonDownEvent,
				PreviewMouseButtonDownHandler);
		}

		if (element is WpfComboBox comboBox)
		{
			comboBox.DropDownClosed -= ComboBox_DropDownClosed;
		}
		else
		{
			operationElement.RemoveHandler(
				UIElement.MouseLeftButtonUpEvent,
				MouseButtonUpHandler);

			if (element is MenuItem)
			{
				operationElement.RemoveHandler(
					UIElement.MouseRightButtonUpEvent,
					MouseButtonUpHandler);
			}
		}

		SetIsPointerPressActive(element, false);
	}

	private static void OperationElement_PreviewKeyDown(
		object sender,
		System.Windows.Input.KeyEventArgs e)
	{
		SetIsPointerPressActive((DependencyObject)sender, false);
	}

	private static void OperationElement_PreviewMouseButtonDown(
		object sender,
		MouseButtonEventArgs e)
	{
		DependencyObject operationElement = (DependencyObject)sender;

		if (!IsSupportedPointerDownButton(operationElement, e.ChangedButton))
		{
			return;
		}

		SetIsPointerPressActive(operationElement, true);

		if ((operationElement is MenuItem menuItem) && !menuItem.HasItems)
		{
			ContextMenu? contextMenu = FindOwningContextMenu(menuItem);

			if (contextMenu is not null)
			{
				PrepareContextMenuPointerRelease(contextMenu);
			}
		}
	}

	private static void OperationElement_MouseButtonUp(
		object sender,
		MouseButtonEventArgs e)
	{
		FrameworkElement operationElement = (FrameworkElement)sender;

		if (!IsSupportedPointerUpButton(operationElement, e.ChangedButton))
		{
			return;
		}

		try
		{
			if (!GetIsPointerPressActive(operationElement))
			{
				return;
			}

			if ((operationElement is MenuItem menuItem) && menuItem.HasItems)
			{
				return;
			}

			if ((operationElement is WpfButtonBase button) &&
				(button.ContextMenu is { IsOpen: true } contextMenu))
			{
				PrepareContextMenuPointerRelease(contextMenu);
				return;
			}

			DependencyObject? fallbackFocusOwner = operationElement is MenuItem leaf
				? FindOwningContextMenu(leaf)?.PlacementTarget
				: null;
			QueueFocusRelease(operationElement, fallbackFocusOwner);
		}
		finally
		{
			if (operationElement is MenuItem)
			{
				QueueMenuItemPointerReset(operationElement);
			}
			else
			{
				SetIsPointerPressActive(operationElement, false);
			}
		}
	}

	private static bool IsSupportedPointerDownButton(
		DependencyObject operationElement,
		MouseButton button)
	{
		return (button == MouseButton.Left) ||
			((button == MouseButton.Right) &&
			(operationElement is MenuItem menuItem) &&
			(FindOwningContextMenu(menuItem) is not null));
	}

	private static bool IsSupportedPointerUpButton(
		DependencyObject operationElement,
		MouseButton button)
	{
		return (button == MouseButton.Left) ||
			((button == MouseButton.Right) &&
			(operationElement is MenuItem));
	}

	private static void QueueMenuItemPointerReset(
		DependencyObject operationElement)
	{
		// MenuItem 會在 mouse-up route 後，以 Render priority 觸發 Click。
		_ = operationElement.Dispatcher.BeginInvoke(
			DispatcherPriority.Input,
			new Action(
				() => SetIsPointerPressActive(operationElement, false)));
	}

	private static void ComboBox_DropDownClosed(object? sender, EventArgs e)
	{
		WpfComboBox comboBox = (WpfComboBox)sender!;

		if (!GetIsPointerPressActive(comboBox))
		{
			return;
		}

		SetIsPointerPressActive(comboBox, false);
		QueueFocusRelease(comboBox);
	}

	private static void ComboBoxItem_PreviewMouseLeftButtonDown(
		object sender,
		MouseButtonEventArgs e)
	{
		if ((e.ChangedButton != MouseButton.Left) ||
			(ItemsControl.ItemsControlFromItemContainer(
				(WpfComboBoxItem)sender) is not WpfComboBox comboBox) ||
			!GetReleaseAfterPointerAction(comboBox))
		{
			return;
		}

		SetIsPointerPressActive(comboBox, true);
	}

	private static ContextMenu? FindOwningContextMenu(MenuItem menuItem)
	{
		ItemsControl? owner = ItemsControl.ItemsControlFromItemContainer(menuItem);

		while (owner is MenuItem parentMenuItem)
		{
			owner = ItemsControl.ItemsControlFromItemContainer(parentMenuItem);
		}

		return owner as ContextMenu;
	}

	private static void PrepareContextMenuPointerRelease(
		ContextMenu contextMenu)
	{
		if (!(bool)contextMenu.GetValue(IsContextMenuObservedProperty))
		{
			contextMenu.SetValue(IsContextMenuObservedProperty, true);
			contextMenu.AddHandler(
				UIElement.PreviewKeyDownEvent,
				new System.Windows.Input.KeyEventHandler(
					ContextMenu_PreviewKeyDown),
				handledEventsToo: true);
			contextMenu.Closed += ContextMenu_Closed;
		}

		contextMenu.SetValue(ReleasePlacementTargetOnClosedProperty, true);
	}

	private static void ContextMenu_PreviewKeyDown(
		object sender,
		System.Windows.Input.KeyEventArgs e)
	{
		((ContextMenu)sender).SetValue(
			ReleasePlacementTargetOnClosedProperty,
			false);
	}

	private static void ContextMenu_Closed(object sender, RoutedEventArgs e)
	{
		ContextMenu contextMenu = (ContextMenu)sender;
		bool shouldRelease = (bool)contextMenu.GetValue(
			ReleasePlacementTargetOnClosedProperty);
		contextMenu.SetValue(ReleasePlacementTargetOnClosedProperty, false);

		if (shouldRelease)
		{
			QueueFocusRelease(contextMenu, contextMenu.PlacementTarget);
		}
	}

	private static void QueueFocusRelease(
		FrameworkElement operationElement,
		DependencyObject? fallbackFocusOwner = null)
	{
		_ = operationElement.Dispatcher.BeginInvoke(
			DispatcherPriority.Input,
			new Action(
				() => ReleaseFocusWhenOwnerIsReady(
					operationElement,
					fallbackFocusOwner)));
	}

	private static void ReleaseFocusWhenOwnerIsReady(
		DependencyObject operationElement,
		DependencyObject? fallbackFocusOwner)
	{
		Window? ownerWindow = GetOwningWindow(fallbackFocusOwner) ??
			GetOwningWindow(operationElement);

		if ((ownerWindow is not null) &&
			ownerWindow.IsVisible &&
			!ownerWindow.IsActive)
		{
			QueueFocusReleaseAfterActivation(
				ownerWindow,
				operationElement,
				fallbackFocusOwner);
			return;
		}

		ReleaseFocusIfStillOwned(operationElement, fallbackFocusOwner);
	}

	private static Window? GetOwningWindow(DependencyObject? element)
	{
		return element is null
			? null
			: element as Window ?? Window.GetWindow(element);
	}

	private static void QueueFocusReleaseAfterActivation(
		Window ownerWindow,
		DependencyObject operationElement,
		DependencyObject? fallbackFocusOwner)
	{
		EventHandler? activatedHandler = null;
		EventHandler? closedHandler = null;

		void DetachHandlers()
		{
			ownerWindow.Activated -= activatedHandler;
			ownerWindow.Closed -= closedHandler;
		}

		activatedHandler = (sender, e) =>
		{
			DetachHandlers();
			// Modal 會在 Activated 返回後還原 focus，且可能連續開啟。
			_ = ownerWindow.Dispatcher.BeginInvoke(
				DispatcherPriority.Input,
				new Action(
					() => ReleaseFocusWhenOwnerIsReady(
						operationElement,
						fallbackFocusOwner)));
		};
		closedHandler = (sender, e) => DetachHandlers();
		ownerWindow.Activated += activatedHandler;
		ownerWindow.Closed += closedHandler;
	}

	internal static void ReleaseFocusIfStillOwned(
		DependencyObject operationElement,
		DependencyObject? fallbackFocusOwner)
	{
		bool operationHasKeyboardFocus =
			IsKeyboardFocusWithin(operationElement);
		bool fallbackHasKeyboardFocus =
			IsKeyboardFocusWithin(fallbackFocusOwner);
		bool operationHasLogicalFocus = HasLogicalFocus(operationElement);
		bool fallbackHasLogicalFocus = HasLogicalFocus(fallbackFocusOwner);

		if (!operationHasKeyboardFocus &&
			!fallbackHasKeyboardFocus &&
			!operationHasLogicalFocus &&
			!fallbackHasLogicalFocus)
		{
			return;
		}

		ClearLogicalFocus(operationElement);

		if (fallbackFocusOwner is not null)
		{
			ClearLogicalFocus(fallbackFocusOwner);
		}

		if (operationHasKeyboardFocus || fallbackHasKeyboardFocus)
		{
			Keyboard.ClearFocus();
		}
	}

	private static bool IsKeyboardFocusWithin(DependencyObject? element)
	{
		return element switch
		{
			UIElement uiElement => uiElement.IsKeyboardFocusWithin,
			ContentElement contentElement =>
				contentElement.IsKeyboardFocusWithin,
			_ => false
		};
	}

	private static bool HasLogicalFocus(DependencyObject? element)
	{
		if (element is null)
		{
			return false;
		}

		DependencyObject focusScope = FocusManager.GetFocusScope(element);
		return ReferenceEquals(
			FocusManager.GetFocusedElement(focusScope),
			element);
	}

	private static void ClearLogicalFocus(DependencyObject element)
	{
		DependencyObject focusScope = FocusManager.GetFocusScope(element);

		if (FocusManager.GetFocusedElement(focusScope) is not null)
		{
			FocusManager.SetFocusedElement(focusScope, null);
		}
	}
}
