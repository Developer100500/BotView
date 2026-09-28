using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BotView.Chart.ChartViews;

/// <summary>Small retained layout tree for DrawingContext-based menu content.</summary>
internal abstract class MenuWidget
{
	public Rect Bounds { get; private set; }
	public abstract Size Measure();
	public virtual void Arrange(Rect bounds) => Bounds = bounds;
	public abstract void Draw(DrawingContext context, double pixelsPerDip);
	public virtual MenuButtonWidget? HitTest(Point point) => null;
}

internal sealed class StackMenuWidget : MenuWidget
{
	private readonly List<MenuWidget> children = new();
	public Orientation Orientation { get; }
	public double Spacing { get; }
	public Thickness Padding { get; }

	public StackMenuWidget(Orientation orientation, double spacing = 0, Thickness padding = default)
	{
		Orientation = orientation;
		Spacing = spacing;
		Padding = padding;
	}

	public StackMenuWidget Add(MenuWidget widget)
	{
		children.Add(widget);
		return this;
	}

	public override Size Measure()
	{
		double width = 0;
		double height = 0;
		foreach (var child in children)
		{
			Size size = child.Measure();
			if (Orientation == Orientation.Horizontal)
			{
				width += size.Width;
				height = Math.Max(height, size.Height);
			}
			else
			{
				width = Math.Max(width, size.Width);
				height += size.Height;
			}
		}

		if (children.Count > 1)
		{
			if (Orientation == Orientation.Horizontal) width += (children.Count - 1) * Spacing;
			else height += (children.Count - 1) * Spacing;
		}
		return new Size(width + Padding.Left + Padding.Right, height + Padding.Top + Padding.Bottom);
	}

	public override void Arrange(Rect bounds)
	{
		base.Arrange(bounds);
		double x = bounds.X + Padding.Left;
		double y = bounds.Y + Padding.Top;
		foreach (var child in children)
		{
			Size size = child.Measure();
			child.Arrange(new Rect(x, y, size.Width, size.Height));
			if (Orientation == Orientation.Horizontal) x += size.Width + Spacing;
			else y += size.Height + Spacing;
		}
	}

	public override void Draw(DrawingContext context, double pixelsPerDip)
	{
		foreach (var child in children)
			child.Draw(context, pixelsPerDip);
	}

	public override MenuButtonWidget? HitTest(Point point)
	{
		if (!Bounds.Contains(point)) return null;
		foreach (var child in children)
		{
			var hit = child.HitTest(point);
			if (hit != null) return hit;
		}
		return null;
	}
}

internal sealed class MenuButtonWidget : MenuWidget
{
	private static readonly Typeface Typeface = new("Segoe UI");
	private static readonly Brush TextBrush = Brushes.White;
	private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(60, 73, 93));
	private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(44, 83, 124));
	private readonly Size size;
	private readonly Action onClick;

	public string? Text { get; }
	public ImageSource? Image { get; }
	public Action<DrawingContext, Rect>? Preview { get; }
	public bool IsSelected { get; set; }
	public bool IsHovered { get; set; }

	public MenuButtonWidget(Size size, Action onClick, string? text = null,
		ImageSource? image = null, Action<DrawingContext, Rect>? preview = null)
	{
		this.size = size;
		this.onClick = onClick;
		Text = text;
		Image = image;
		Preview = preview;
	}

	public override Size Measure() => size;

	public override void Draw(DrawingContext context, double pixelsPerDip)
	{
		if (IsSelected || IsHovered)
			context.DrawRoundedRectangle(IsSelected ? SelectedBrush : HoverBrush, null, Bounds, 5, 5);

		double contentX = Bounds.X + 7;
		if (Image != null || Preview != null)
		{
			if (Text == null) contentX = Bounds.X + (Bounds.Width - 18) / 2;
			var imageBounds = new Rect(contentX, Bounds.Y + (Bounds.Height - 18) / 2, 18, 18);
			if (Image != null) context.DrawImage(Image, imageBounds);
			else Preview?.Invoke(context, imageBounds);
			contentX += 23;
		}
		if (Text == null) return;

		var formatted = new FormattedText(Text, CultureInfo.CurrentUICulture,
			FlowDirection.LeftToRight, Typeface, 12, TextBrush, pixelsPerDip);
		double textX = Image == null && Preview == null
			? Bounds.X + (Bounds.Width - formatted.Width) / 2
			: contentX;
		context.DrawText(formatted, new Point(textX, Bounds.Y + (Bounds.Height - formatted.Height) / 2));
	}

	public override MenuButtonWidget? HitTest(Point point) => Bounds.Contains(point) ? this : null;
	public void Invoke() => onClick();
}

internal sealed class MenuLabelWidget : MenuWidget
{
	private static readonly Typeface Typeface = new("Segoe UI");
	private readonly string text;
	private readonly Size size;
	private readonly Brush brush;

	public MenuLabelWidget(string text, Size size, Brush? brush = null)
	{
		this.text = text;
		this.size = size;
		this.brush = brush ?? Brushes.LightGray;
	}

	public override Size Measure() => size;

	public override void Draw(DrawingContext context, double pixelsPerDip)
	{
		var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
			FlowDirection.LeftToRight, Typeface, 11, brush, pixelsPerDip);
		context.DrawText(formatted, new Point(Bounds.X + 4, Bounds.Y + (Bounds.Height - formatted.Height) / 2));
	}
}

internal sealed class MenuDrawingSurface : FrameworkElement
{
	private readonly MenuWidget root;
	private MenuButtonWidget? hovered;
	private MenuButtonWidget? pressed;

	public MenuDrawingSurface(MenuWidget root)
	{
		this.root = root;
		Size size = root.Measure();
		Width = size.Width;
		Height = size.Height;
		root.Arrange(new Rect(size));
		Focusable = true;
	}

	protected override void OnRender(DrawingContext context)
	{
		base.OnRender(context);
		var bounds = new Rect(RenderSize);
		context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(31, 39, 53)),
			new Pen(new SolidColorBrush(Color.FromRgb(83, 96, 115)), 0.5), bounds, 8, 8);
		root.Draw(context, VisualTreeHelper.GetDpi(this).PixelsPerDip);
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		var hit = root.HitTest(e.GetPosition(this));
		if (ReferenceEquals(hit, hovered)) return;
		if (hovered != null) hovered.IsHovered = false;
		hovered = hit;
		if (hovered != null) hovered.IsHovered = true;
		InvalidateVisual();
	}

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		base.OnMouseLeave(e);
		if (hovered != null) hovered.IsHovered = false;
		hovered = null;
		InvalidateVisual();
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		pressed = root.HitTest(e.GetPosition(this));
		e.Handled = true;
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);
		var hit = root.HitTest(e.GetPosition(this));
		if (ReferenceEquals(hit, pressed)) hit?.Invoke();
		pressed = null;
		e.Handled = true;
	}

	protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseRightButtonDown(e);
		e.Handled = true;
	}

	protected override void OnMouseWheel(MouseWheelEventArgs e)
	{
		base.OnMouseWheel(e);
		e.Handled = true;
	}
}
