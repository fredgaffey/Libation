using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace LibationMobile.Controls;

/// <summary>
/// A grid of squares coloured by how much listening each holds, as GitHub's contribution calendar: weeks across and
/// days down, or hours across and days down. Empty squares are faint; the fullest is ember. Tap a square to select it.
/// </summary>
public class HeatGrid : Control
{
	public static readonly StyledProperty<IReadOnlyList<double>> ValuesProperty =
		AvaloniaProperty.Register<HeatGrid, IReadOnlyList<double>>(nameof(Values), []);

	public static readonly StyledProperty<int> RowsProperty = AvaloniaProperty.Register<HeatGrid, int>(nameof(Rows), 7);

	/// <summary>Down the left, one a row; blank ones are skipped.</summary>
	public static readonly StyledProperty<IReadOnlyList<string>> RowLabelsProperty =
		AvaloniaProperty.Register<HeatGrid, IReadOnlyList<string>>(nameof(RowLabels), []);

	/// <summary>Along the bottom, one a column; blank ones are skipped.</summary>
	public static readonly StyledProperty<IReadOnlyList<string>> ColumnLabelsProperty =
		AvaloniaProperty.Register<HeatGrid, IReadOnlyList<string>>(nameof(ColumnLabels), []);

	/// <summary>The square tapped, as an index into <see cref="Values"/>; -1 for none.</summary>
	public static readonly StyledProperty<int> SelectedIndexProperty =
		AvaloniaProperty.Register<HeatGrid, int>(nameof(SelectedIndex), -1, defaultBindingMode: BindingMode.TwoWay);

	/// <summary>Values filled column by column, top to bottom; NaN for a square not drawn, such as a day still to come.</summary>
	public IReadOnlyList<double> Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
	public int Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
	public IReadOnlyList<string> RowLabels { get => GetValue(RowLabelsProperty); set => SetValue(RowLabelsProperty, value); }
	public IReadOnlyList<string> ColumnLabels { get => GetValue(ColumnLabelsProperty); set => SetValue(ColumnLabelsProperty, value); }
	public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

	private static readonly Color Slate = Color.Parse("#2A2640");
	private static readonly Color Dust = Color.Parse("#9C95B0");
	private static readonly Color Paper = Color.Parse("#EFE7D6");
	private static readonly Color Lamp = Color.Parse("#F0B25A");
	private static readonly Color Ember = Color.Parse("#E8745A");

	private const double GAP = 3;
	private const double LABEL_SIZE = 10;
	private const double ROW_LABEL_WIDTH = 22;
	private const double COLUMN_LABEL_HEIGHT = 16;
	private const double MAX_CELL = 22;

	static HeatGrid()
	{
		AffectsRender<HeatGrid>(ValuesProperty, RowsProperty, RowLabelsProperty, ColumnLabelsProperty, SelectedIndexProperty);
		AffectsMeasure<HeatGrid>(ValuesProperty, RowsProperty);
	}

	private int Columns => Rows > 0 ? (Values.Count + Rows - 1) / Rows : 0;

	private double Cell(double width)
		=> Columns == 0 ? MAX_CELL : Math.Min(MAX_CELL, (width - ROW_LABEL_WIDTH - GAP * (Columns - 1)) / Columns);

	protected override Size MeasureOverride(Size availableSize)
	{
		var width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
		var cell = Cell(width);
		return new Size(width, Rows * cell + (Rows - 1) * GAP + COLUMN_LABEL_HEIGHT);
	}

	/// <summary>Faint for nothing, then paper to lamp to ember as the share of the fullest square rises.</summary>
	public static Color Shade(double share)
	{
		if (share <= 0)
			return Color.FromArgb(255, (byte)(Slate.R + 18), (byte)(Slate.G + 18), (byte)(Slate.B + 22));
		// A square root, so light days still show against heavy ones.
		var t = Math.Sqrt(Math.Clamp(share, 0, 1));
		return t < 0.5 ? Blend(Blend(Slate, Paper, 0.35), Lamp, t / 0.5) : Blend(Lamp, Ember, (t - 0.5) / 0.5);
	}

	private static Color Blend(Color a, Color b, double t)
	{
		t = Math.Clamp(t, 0, 1);
		return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
	}

	public override void Render(DrawingContext context)
	{
		if (Bounds.Width < 40 || Rows <= 0 || Values.Count == 0)
			return;
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		var cell = Cell(Bounds.Width);
		var most = 0.0;
		foreach (var v in Values)
			if (!double.IsNaN(v))
				most = Math.Max(most, v);
		var typeface = new Typeface(FontFamily.Default);
		var labelBrush = new SolidColorBrush(Dust);

		for (var i = 0; i < Values.Count; i++)
		{
			var value = Values[i];
			if (double.IsNaN(value))
				continue;
			var rect = CellRect(i, cell);
			context.FillRectangle(new SolidColorBrush(Shade(most > 0 ? value / most : 0)), rect, (float)Math.Min(4, cell / 4));
			if (i == SelectedIndex)
				context.DrawRectangle(new Pen(new SolidColorBrush(Paper), 2), rect.Inflate(1), (float)Math.Min(5, cell / 3));
		}

		for (var r = 0; r < Math.Min(Rows, RowLabels.Count); r++)
		{
			if (string.IsNullOrEmpty(RowLabels[r]))
				continue;
			var text = new FormattedText(RowLabels[r], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LABEL_SIZE, labelBrush);
			context.DrawText(text, new Point(0, r * (cell + GAP) + (cell - text.Height) / 2));
		}
		var bottom = Rows * cell + (Rows - 1) * GAP + 2;
		for (var c = 0; c < Math.Min(Columns, ColumnLabels.Count); c++)
		{
			if (string.IsNullOrEmpty(ColumnLabels[c]))
				continue;
			var text = new FormattedText(ColumnLabels[c], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LABEL_SIZE, labelBrush);
			var x = ROW_LABEL_WIDTH + c * (cell + GAP);
			context.DrawText(text, new Point(Math.Min(x, Bounds.Width - text.Width), bottom));
		}
	}

	private Rect CellRect(int index, double cell)
		=> new(ROW_LABEL_WIDTH + index / Rows * (cell + GAP), index % Rows * (cell + GAP), cell, cell);

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		var point = e.GetPosition(this);
		var cell = Cell(Bounds.Width);
		var column = (int)Math.Floor((point.X - ROW_LABEL_WIDTH) / (cell + GAP));
		var row = (int)Math.Floor(point.Y / (cell + GAP));
		var index = column * Rows + row;
		if (column < 0 || row < 0 || row >= Rows || index >= Values.Count || double.IsNaN(Values[index]))
			return;
		SelectedIndex = SelectedIndex == index ? -1 : index;
		e.Handled = true;
	}
}
