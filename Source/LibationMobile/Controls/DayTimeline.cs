using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace LibationMobile.Controls;

/// <summary>A session on a day's timeline: where it starts and ends, as hours from midnight, and its speed.</summary>
public record TimelineBlock(double FromHour, double ToHour, double Speed);

/// <summary>
/// A day from midnight to midnight as a thin track, each session a block coloured by its speed, paper at 1x warming
/// through lamp at 3x to ember at 10x, the speed rail's colours. Shows at a glance when and how fast the day's listening was.
/// </summary>
public class DayTimeline : Control
{
	public static readonly StyledProperty<IReadOnlyList<TimelineBlock>> BlocksProperty =
		AvaloniaProperty.Register<DayTimeline, IReadOnlyList<TimelineBlock>>(nameof(Blocks), []);

	public IReadOnlyList<TimelineBlock> Blocks { get => GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }

	private static readonly Color Dust = Color.Parse("#9C95B0");
	private static readonly Color Paper = Color.Parse("#EFE7D6");
	private static readonly Color Lamp = Color.Parse("#F0B25A");
	private static readonly Color Ember = Color.Parse("#E8745A");

	private const double TRACK = 12;
	private const double LABEL_SIZE = 9;
	/// <summary>A block is never drawn thinner than this, so a two-minute session still shows.</summary>
	private const double MIN_BLOCK = 2;

	static DayTimeline() => AffectsRender<DayTimeline>(BlocksProperty);

	protected override Size MeasureOverride(Size availableSize)
		=> new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width, TRACK + 3 + LABEL_SIZE * 1.4);

	/// <summary>The speed rail's warming: paper at 1x, lamp at 3x, ember at 10x.</summary>
	public static Color SpeedColor(double speed)
	{
		if (speed <= 1)
			return Paper;
		if (speed <= 3)
			return Blend(Paper, Lamp, (speed - 1) / 2);
		return Blend(Lamp, Ember, Math.Log(speed / 3) / Math.Log(10.0 / 3));
	}

	private static Color Blend(Color a, Color b, double t)
	{
		t = Math.Clamp(t, 0, 1);
		return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
	}

	public override void Render(DrawingContext context)
	{
		var width = Bounds.Width;
		if (width < 40)
			return;
		context.FillRectangle(new SolidColorBrush(Dust, 0.15), new Rect(0, 0, width, TRACK), 3);
		foreach (var block in Blocks)
		{
			var x = Math.Clamp(block.FromHour / 24, 0, 1) * width;
			var w = Math.Max(MIN_BLOCK, (Math.Clamp(block.ToHour / 24, 0, 1) - Math.Clamp(block.FromHour / 24, 0, 1)) * width);
			context.FillRectangle(new SolidColorBrush(SpeedColor(block.Speed)), new Rect(Math.Min(x, width - w), 0, w, TRACK), 2);
		}
		var typeface = new Typeface(FontFamily.Default);
		var brush = new SolidColorBrush(Dust);
		foreach (var hour in new[] { 0, 6, 12, 18, 24 })
		{
			var text = new FormattedText(hour switch { 0 or 24 => "12a", 12 => "12p", < 12 => $"{hour}a", _ => $"{hour - 12}p" },
				CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LABEL_SIZE, brush);
			var x = hour / 24.0 * width - text.Width / 2;
			context.DrawText(text, new Point(Math.Clamp(x, 0, width - text.Width), TRACK + 3));
		}
	}
}
