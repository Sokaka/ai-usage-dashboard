using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class VerticalDragScrollSessionTests
{
	[Fact]
	public void Move_BelowThreshold_KeepsClickPending()
	{
		VerticalDragScrollSession session = new();
		session.Begin(pointerY: 100, verticalOffset: 40);

		VerticalDragScrollUpdate update = session.Move(
			pointerY: 97,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight: 100);

		Assert.False(update.ShouldCaptureMouse);
		Assert.False(update.ShouldScroll);
		Assert.True(session.IsPending);
		Assert.False(session.End());
	}

	[Fact]
	public void Move_AtThreshold_StartsDragAndUsesInitialOffset()
	{
		VerticalDragScrollSession session = new();
		session.Begin(pointerY: 100, verticalOffset: 40);

		VerticalDragScrollUpdate first = session.Move(
			pointerY: 96,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight: 100);
		VerticalDragScrollUpdate second = session.Move(
			pointerY: 90,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight: 100);

		Assert.True(first.ShouldCaptureMouse);
		Assert.True(first.ShouldScroll);
		Assert.Equal(44, first.VerticalOffset);
		Assert.False(second.ShouldCaptureMouse);
		Assert.True(second.ShouldScroll);
		Assert.Equal(50, second.VerticalOffset);
		Assert.True(session.IsDragging);
		Assert.True(session.End());
	}

	[Theory]
	[InlineData(100, 190, 80, 0)]
	[InlineData(100, 0, 80, 80)]
	public void Move_ClampsOffsetToScrollableRange(
		double startPointerY,
		double currentPointerY,
		double scrollableHeight,
		double expectedOffset)
	{
		VerticalDragScrollSession session = new();
		session.Begin(startPointerY, verticalOffset: 10);

		VerticalDragScrollUpdate update = session.Move(
			currentPointerY,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight);

		Assert.True(update.ShouldScroll);
		Assert.Equal(expectedOffset, update.VerticalOffset);
	}

	[Fact]
	public void Move_WhenLeftButtonIsReleasedAfterDragging_CancelsSession()
	{
		VerticalDragScrollSession session = new();
		session.Begin(pointerY: 100, verticalOffset: 40);
		_ = session.Move(
			pointerY: 90,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight: 100);

		VerticalDragScrollUpdate update = session.Move(
			pointerY: 85,
			isLeftButtonPressed: false,
			dragThreshold: 4,
			scrollableHeight: 100);

		Assert.Equal(default, update);
		Assert.False(session.IsActive);
		Assert.False(session.IsDragging);
		Assert.False(session.IsPending);
		Assert.Equal(
			default,
			session.Move(
				pointerY: 80,
				isLeftButtonPressed: true,
				dragThreshold: 4,
				scrollableHeight: 100));
	}

	[Fact]
	public void Cancel_AfterDrag_ClearsSessionWithoutCompletingClick()
	{
		VerticalDragScrollSession session = new();
		session.Begin(pointerY: 100, verticalOffset: 40);
		_ = session.Move(
			pointerY: 90,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight: 100);

		session.Cancel();

		Assert.False(session.IsActive);
		Assert.False(session.End());
	}

	[Fact]
	public void End_AfterDrag_ReportsCompletedDragOnlyOnce()
	{
		VerticalDragScrollSession session = new();
		session.Begin(pointerY: 100, verticalOffset: 40);
		_ = session.Move(
			pointerY: 90,
			isLeftButtonPressed: true,
			dragThreshold: 4,
			scrollableHeight: 100);

		Assert.True(session.End());
		Assert.False(session.End());
	}
}
