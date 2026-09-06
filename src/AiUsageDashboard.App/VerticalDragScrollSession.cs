namespace AiUsageDashboard.App;

internal readonly record struct VerticalDragScrollUpdate(
	bool ShouldCaptureMouse,
	bool ShouldScroll,
	double VerticalOffset);

internal sealed class VerticalDragScrollSession
{
	private double _startPointerY;
	private double _startVerticalOffset;

	internal bool IsActive => IsPending || IsDragging;

	internal bool IsDragging { get; private set; }

	internal bool IsPending { get; private set; }

	internal void Begin(double pointerY, double verticalOffset)
	{
		ValidateFinite(pointerY, nameof(pointerY));
		ValidateNonNegativeFinite(verticalOffset, nameof(verticalOffset));

		_startPointerY = pointerY;
		_startVerticalOffset = verticalOffset;
		IsDragging = false;
		IsPending = true;
	}

	internal void Cancel()
	{
		IsDragging = false;
		IsPending = false;
	}

	internal bool End()
	{
		bool wasDragging = IsDragging;
		Cancel();
		return wasDragging;
	}

	internal VerticalDragScrollUpdate Move(
		double pointerY,
		bool isLeftButtonPressed,
		double dragThreshold,
		double scrollableHeight)
	{
		if (!IsActive)
		{
			return default;
		}

		if (!isLeftButtonPressed)
		{
			Cancel();
			return default;
		}

		ValidateFinite(pointerY, nameof(pointerY));
		ValidateNonNegativeFinite(dragThreshold, nameof(dragThreshold));
		ValidateNonNegativeFinite(scrollableHeight, nameof(scrollableHeight));

		double delta = pointerY - _startPointerY;
		bool shouldCaptureMouse = false;

		if (IsPending)
		{
			if (Math.Abs(delta) < dragThreshold)
			{
				return default;
			}

			IsPending = false;
			IsDragging = true;
			shouldCaptureMouse = true;
		}

		double verticalOffset = Math.Clamp(
			_startVerticalOffset - delta,
			0,
			scrollableHeight);
		return new VerticalDragScrollUpdate(
			shouldCaptureMouse,
			ShouldScroll: true,
			verticalOffset);
	}

	private static void ValidateFinite(double value, string parameterName)
	{
		if (!double.IsFinite(value))
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}

	private static void ValidateNonNegativeFinite(
		double value,
		string parameterName)
	{
		if (!double.IsFinite(value) || (value < 0))
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}
}
