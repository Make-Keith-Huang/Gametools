using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Overlay;

/// <summary>Draws font outlines from sanitized text; no XAML/HTML/URI parser ever receives provider output.</summary>
public sealed class SubtitleRenderer : FrameworkElement
{
    private string _text = string.Empty;
    private OverlayOptions _options = new();
    public string DisplayText => _text;

    public void Update(string text, OverlayOptions options)
    {
        var sanitized = OverlayLayout.Sanitize(text, options);
        if (_text == sanitized && _options == options)
        { return; }
        _text = sanitized;
        _options = options;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_text.Length == 0 || ActualWidth <= 24 || ActualHeight <= 24)
        { return; }
        var foreground = Brush(_options.Foreground);
        var typeface = new Typeface(new FontFamily(_options.FontFamily), FontStyles.Normal,
            _options.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(_text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, _options.FontSize, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, ActualWidth - 24),
            MaxTextHeight = Math.Max(1, Math.Min(ActualHeight - 24, _options.MaximumLines * _options.FontSize * 1.35)),
            LineHeight = _options.FontSize * 1.35,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = _options.Alignment switch
            {
                OverlayTextAlignment.Left => TextAlignment.Left,
                OverlayTextAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Center
            }
        };
        var geometry = text.BuildGeometry(new Point(12, 12));
        var background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(_options.BackgroundOpacity * 255), 0, 0, 0));
        background.Freeze();
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        drawingContext.DrawRoundedRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        drawingContext.DrawGeometry(foreground, new Pen(Brush(_options.OutlineColor), _options.OutlineThickness)
        {
            LineJoin = PenLineJoin.Round
        }, geometry);
        drawingContext.Pop();
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(Color.FromRgb(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        brush.Freeze();
        return brush;
    }
}
