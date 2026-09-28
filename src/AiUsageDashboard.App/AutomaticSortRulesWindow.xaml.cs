using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using FormsScreen = System.Windows.Forms.Screen;

namespace AiUsageDashboard.App;

public partial class AutomaticSortRulesWindow : Window
{
	private const double PreferredMinimumWidth = 320;

	public AutomaticSortRulesWindow()
		: this(resources: null)
	{
	}

	internal AutomaticSortRulesWindow(ResourceDictionary? resources)
	{
		if (resources is not null)
		{
			Resources.MergedDictionaries.Add(resources);
		}

		InitializeComponent();
		Loaded += (_, _) => CloseButton.Focus();
		DpiChanged += (_, _) => UpdateWorkAreaConstraints();
		LocationChanged += (_, _) => UpdateWorkAreaConstraints();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		UpdateWorkAreaConstraints(preferOwner: true);
	}

	internal void ApplyWorkAreaConstraints(
		System.Drawing.Rectangle workArea,
		DpiScale dpi)
	{
		double maximumWidth = WindowWorkAreaLayout.CalculateMaxWidth(
			PreferredMinimumWidth, workArea.Width, dpi.DpiScaleX);
		double targetMinimumWidth = Math.Min(
			PreferredMinimumWidth, maximumWidth);
		if (MaxWidth < targetMinimumWidth)
		{
			MaxWidth = maximumWidth;
		}

		MinWidth = targetMinimumWidth;
		MaxWidth = maximumWidth;
		MaxHeight = WindowWorkAreaLayout.CalculateMaxHeight(
			0, workArea.Height, dpi.DpiScaleY);
	}

	private void UpdateWorkAreaConstraints(bool preferOwner = false)
	{
		Window referenceWindow = this;
		IntPtr referenceHandle = new WindowInteropHelper(this).Handle;

		if (preferOwner && (Owner is Window owner))
		{
			IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
			if (ownerHandle != IntPtr.Zero)
			{
				referenceWindow = owner;
				referenceHandle = ownerHandle;
			}
		}

		if (referenceHandle == IntPtr.Zero)
		{
			return;
		}

		ApplyWorkAreaConstraints(
			FormsScreen.FromHandle(referenceHandle).WorkingArea,
			VisualTreeHelper.GetDpi(referenceWindow));
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
