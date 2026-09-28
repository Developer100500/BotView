using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Chart.TechnicalAnalysis;

namespace BotView.Chart.ChartViews;

/// <summary>A drawn popup menu for the outline of a chart tool.</summary>
internal sealed class ChartContextMenu
{
	private enum Section { None, Color, Thickness, Style }
	private static readonly Color[] Palette =
	{
		Colors.White, Color.FromRgb(17, 24, 39), Color.FromRgb(239, 68, 68),
		Color.FromRgb(249, 115, 22), Color.FromRgb(250, 204, 21),
		Color.FromRgb(34, 197, 94), Color.FromRgb(6, 182, 212),
		Color.FromRgb(59, 130, 246), Color.FromRgb(139, 92, 246),
		Color.FromRgb(236, 72, 153)
	};

	private readonly ChartView owner;
	private readonly TechnicalAnalysisManager manager;
	private readonly Popup popup;
	private readonly Canvas canvas;
	private readonly TextBox hexEditor;
	private TechnicalAnalysisTool? target;
	private IStrokeStyleTool? outline;
	private MenuDrawingSurface? surface;
	private MenuButtonWidget? hexButton;
	private Point anchor;
	private Section section;
	private bool editingHex;
	private bool invalidHex;

	public bool IsOpen => popup.IsOpen;

	public ChartContextMenu(ChartView owner, TechnicalAnalysisManager manager)
	{
		this.owner = owner;
		this.manager = manager;
		canvas = new Canvas();
		popup = new Popup
		{
			PlacementTarget = owner,
			Placement = PlacementMode.Relative,
			AllowsTransparency = true,
			StaysOpen = false,
			Child = canvas
		};
		popup.Closed += (_, _) =>
		{
			target = null;
			outline = null;
			editingHex = false;
			invalidHex = false;
		};
		hexEditor = new TextBox
		{
			Background = new SolidColorBrush(Color.FromRgb(23, 30, 42)),
			Foreground = Brushes.White,
			BorderBrush = Brushes.SteelBlue,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(5, 2, 5, 2),
			FontSize = 12
		};
		hexEditor.PreviewKeyDown += OnHexKeyDown;
		owner.Unloaded += (_, _) => CloseMenu();
	}

	public void OpenMenu(TechnicalAnalysisTool tool, Point position)
	{
		if (tool is not IStrokeStyleTool styled) return;
		CloseMenu();
		target = tool;
		outline = styled;
		anchor = position;
		section = Section.None;
		Rebuild();
		popup.IsOpen = true;
		surface?.Focus();
	}

	public void CloseMenu()
	{
		popup.IsOpen = false;
		target = null;
		outline = null;
		editingHex = false;
		invalidHex = false;
	}

	private void Rebuild()
	{
		if (outline == null) return;
		hexButton = null;
		var root = BuildWidgets();
		surface = new MenuDrawingSurface(root);
		surface.PreviewKeyDown += (_, e) =>
		{
			if (e.Key != Key.Escape) return;
			CloseMenu();
			e.Handled = true;
		};
		canvas.Children.Clear();
		canvas.Width = surface.Width;
		canvas.Height = surface.Height;
		canvas.Children.Add(surface);
		if (editingHex && hexButton != null)
		{
			hexEditor.BorderBrush = invalidHex ? Brushes.IndianRed : Brushes.SteelBlue;
			hexEditor.Width = hexButton.Bounds.Width - 12;
			hexEditor.Height = hexButton.Bounds.Height - 6;
			Canvas.SetLeft(hexEditor, hexButton.Bounds.X + 6);
			Canvas.SetTop(hexEditor, hexButton.Bounds.Y + 3);
			canvas.Children.Add(hexEditor);
		}
		Position();
		if (popup.IsOpen && !editingHex)
			surface.Focus();
	}

	private StackMenuWidget BuildWidgets()
	{
		var root = new StackMenuWidget(Orientation.Vertical, 6, new Thickness(10));
		root.Add(new MenuLabelWidget("Контур", new Size(214, 18)));
		var toolbar = new StackMenuWidget(Orientation.Horizontal, 8);
		toolbar.Add(new MenuButtonWidget(new Size(66, 36), () => Toggle(Section.Color), "Цвет",
			preview: (dc, rect) => DrawSwatch(dc, rect, CurrentColor))
		{ IsSelected = section == Section.Color });
		toolbar.Add(new MenuButtonWidget(new Size(66, 36), () => Toggle(Section.Thickness),
			$"{outline!.Thickness:G} px", preview: (dc, rect) => DrawLine(dc, rect, LineStyle.Solid, 2))
		{ IsSelected = section == Section.Thickness });
		toolbar.Add(new MenuButtonWidget(new Size(66, 36), () => Toggle(Section.Style), "Тип",
			preview: (dc, rect) => DrawLine(dc, rect, outline!.Style, 2))
		{ IsSelected = section == Section.Style });
		root.Add(toolbar);

		switch (section)
		{
			case Section.Color: BuildColorSection(root); break;
			case Section.Thickness: BuildThicknessSection(root); break;
			case Section.Style: BuildStyleSection(root); break;
		}
		return root;
	}

	private void BuildColorSection(StackMenuWidget root)
	{
		for (int row = 0; row < 2; row++)
		{
			var colors = new StackMenuWidget(Orientation.Horizontal, 5);
			for (int column = 0; column < 5; column++)
			{
				Color color = Palette[row * 5 + column];
				colors.Add(new MenuButtonWidget(new Size(38, 34), () => SetColor(color),
					preview: (dc, rect) => DrawSwatch(dc, rect, color))
				{ IsSelected = CurrentColor == color });
			}
			root.Add(colors);
		}
		hexButton = new MenuButtonWidget(new Size(214, 31), StartHexEditing,
			$"HEX: {FormatHex(CurrentColor)}");
		root.Add(hexButton);
		if (invalidHex)
			root.Add(new MenuLabelWidget("HEX: #RRGGBB / #AARRGGBB",
				new Size(214, 20), Brushes.IndianRed));
	}

	private void BuildThicknessSection(StackMenuWidget root)
	{
		var widths = new StackMenuWidget(Orientation.Horizontal, 7);
		for (int width = 1; width <= 5; width++)
		{
			double selectedWidth = width;
			widths.Add(new MenuButtonWidget(new Size(36, 38),
				() => Apply(styled => styled.Thickness = selectedWidth),
				width.ToString(CultureInfo.InvariantCulture))
			{ IsSelected = Math.Abs(outline!.Thickness - selectedWidth) < 0.001 });
		}
		root.Add(widths);
	}

	private void BuildStyleSection(StackMenuWidget root)
	{
		foreach (var (style, label) in new[]
		{
			(LineStyle.Solid, "Сплошная"),
			(LineStyle.Dashed, "Штриховая"),
			(LineStyle.Dotted, "Пунктирная")
		})
		{
			root.Add(new MenuButtonWidget(new Size(214, 32),
				() => Apply(styled => styled.Style = style), label,
				preview: (dc, rect) => DrawLine(dc, rect, style, 2))
			{ IsSelected = outline!.Style == style });
		}
	}

	private void Toggle(Section next)
	{
		section = section == next ? Section.None : next;
		editingHex = false;
		invalidHex = false;
		Rebuild();
	}

	private void StartHexEditing()
	{
		editingHex = true;
		invalidHex = false;
		hexEditor.Text = FormatHex(CurrentColor);
		Rebuild();
		hexEditor.Focus();
		hexEditor.SelectAll();
	}

	private void OnHexKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			CloseMenu();
			e.Handled = true;
		}
		else if (e.Key == Key.Enter)
		{
			if (TryParseHex(hexEditor.Text, out var color))
			{
				editingHex = false;
				invalidHex = false;
				SetColor(color);
			}
			else
			{
				invalidHex = true;
				Rebuild();
				hexEditor.Focus();
			}
			e.Handled = true;
		}
	}

	private Color CurrentColor => outline?.Color is SolidColorBrush brush ? brush.Color : Colors.White;

	private void SetColor(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		Apply(styled => styled.Color = brush);
	}

	private void Apply(Action<IStrokeStyleTool> change)
	{
		if (target == null || outline == null || !manager.GetTools().Contains(target))
		{
			CloseMenu();
			return;
		}
		change(outline);
		target.NeedsRedrawing = true;
		owner.InvalidateVisual();
		manager.SaveTools();
		Rebuild();
	}

	private void Position()
	{
		var window = Window.GetWindow(owner);
		if (window == null) return;
		Point origin = owner.TransformToAncestor(window).Transform(new Point());
		Point position = ClampToWindow(new Point(origin.X + anchor.X + 8, origin.Y + anchor.Y + 8),
			new Size(canvas.Width, canvas.Height), new Size(window.ActualWidth, window.ActualHeight));
		popup.HorizontalOffset = position.X - origin.X;
		popup.VerticalOffset = position.Y - origin.Y;
	}

	internal static Point ClampToWindow(Point desired, Size menu, Size window) => new(
		Math.Clamp(desired.X, 8, Math.Max(8, window.Width - menu.Width - 8)),
		Math.Clamp(desired.Y, 8, Math.Max(8, window.Height - menu.Height - 8)));

	private static void DrawSwatch(DrawingContext context, Rect rect, Color color)
	{
		var inset = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
		context.DrawRoundedRectangle(new SolidColorBrush(color), new Pen(Brushes.LightGray, 1), inset, 3, 3);
	}

	private static void DrawLine(DrawingContext context, Rect rect, LineStyle style, double width)
	{
		var pen = new Pen(Brushes.White, width)
		{
			DashStyle = style switch
			{
				LineStyle.Dashed => DashStyles.Dash,
				LineStyle.Dotted => DashStyles.Dot,
				_ => DashStyles.Solid
			}
		};
		if (style == LineStyle.Dotted)
			pen.DashCap = PenLineCap.Round;
		double y = rect.Y + rect.Height / 2;
		context.DrawLine(pen, new Point(rect.X, y), new Point(rect.Right, y));
	}

	internal static string FormatHex(Color color) => color.A == 255
		? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
		: $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

	internal static bool TryParseHex(string? value, out Color color)
	{
		color = default;
		string text = value?.Trim() ?? string.Empty;
		if (!text.StartsWith('#') || (text.Length != 7 && text.Length != 9)) return false;
		if (!uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber,
			CultureInfo.InvariantCulture, out uint argb)) return false;
		if (text.Length == 7) argb |= 0xFF000000;
		color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
			(byte)(argb >> 8), (byte)argb);
		return true;
	}
}
