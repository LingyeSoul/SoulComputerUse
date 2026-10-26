using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SoulComputerUse.Windows.Shell;

/// <summary>Everything the caption needs in order to paint itself once.</summary>
public sealed record CaptionPaintRequest(
    TitleBarPalette Palette,
    SessionStatusVisual Status,
    string Title,
    CaptionButtonKind? HoveredButton,
    CaptionButtonKind? PressedButton,
    bool IsMaximized);

/// <summary>
/// Draws the caption strip and the letterbox with GDI+. It only paints: it never reads window state,
/// so the same request always produces the same picture.
/// </summary>
public static class CaptionPainter
{
    /// <summary>Weight of the palette background mixed into the gate tier label.</summary>
    public const int GateTierBackgroundWeightPercent = 40;

    private const int GlyphExtent = 10;
    private const int IconStatusDotInset = 2;

    /// <summary>Paints the whole client area: black letterbox underneath, caption strip on top.</summary>
    public static void PaintClientArea(Graphics graphics, CaptionMetrics metrics, CaptionPaintRequest request)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(request);

        graphics.Clear(CaptionColor.Black.ToDrawingColor());
        PaintCaption(graphics, metrics, request);
    }

    /// <summary>Paints only the caption strip, with its bottom border.</summary>
    public static void PaintCaption(Graphics graphics, CaptionMetrics metrics, CaptionPaintRequest request)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(request);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (SolidBrush background = new(request.Palette.Background.ToDrawingColor()))
        {
            graphics.FillRectangle(background, 0, 0, metrics.ClientWidth, metrics.Height);
        }

        PaintButtons(graphics, metrics, request);
        PaintIcon(graphics, metrics, request);
        PaintTitle(graphics, metrics, request);
        PaintStatus(graphics, metrics, request);

        using SolidBrush borderBrush = new(request.Palette.Border.ToDrawingColor());
        int borderThickness = CaptionMetrics.ScaleVisible(1, metrics.Dpi);
        graphics.FillRectangle(borderBrush, 0, metrics.Height - borderThickness, metrics.ClientWidth, borderThickness);
    }

    private static void PaintButtons(Graphics graphics, CaptionMetrics metrics, CaptionPaintRequest request)
    {
        PaintButton(graphics, metrics, request, CaptionButtonKind.Minimize, metrics.MinimizeBounds);
        PaintButton(graphics, metrics, request, CaptionButtonKind.Maximize, metrics.MaximizeBounds);
        PaintButton(graphics, metrics, request, CaptionButtonKind.Close, metrics.CloseBounds);
    }

    private static void PaintButton(
        Graphics graphics,
        CaptionMetrics metrics,
        CaptionPaintRequest request,
        CaptionButtonKind kind,
        Rectangle bounds)
    {
        CaptionButtonState state = StateOf(request, kind);
        CaptionColor fill = request.Palette.ButtonBackground(kind, state);
        if (state != CaptionButtonState.Normal)
        {
            using SolidBrush brush = new(fill.ToDrawingColor());
            graphics.FillRectangle(brush, bounds);
        }

        CaptionColor glyphColor = request.Palette.ButtonIcon(kind, state);
        int glyphExtent = Math.Max(4, CaptionMetrics.Scale(GlyphExtent, metrics.Dpi));
        int glyphThickness = CaptionMetrics.ScaleVisible(1, metrics.Dpi);
        int centreX = bounds.Left + (bounds.Width / 2);
        int centreY = bounds.Top + (bounds.Height / 2);
        int half = glyphExtent / 2;

        using Pen pen = new(glyphColor.ToDrawingColor(), glyphThickness)
        {
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat,
        };

        switch (kind)
        {
            case CaptionButtonKind.Minimize:
                graphics.DrawLine(pen, centreX - half, centreY, centreX + half, centreY);
                break;
            case CaptionButtonKind.Maximize when request.IsMaximized:
                PaintRestoreGlyph(graphics, pen, centreX, centreY, half);
                break;
            case CaptionButtonKind.Maximize:
                graphics.DrawRectangle(pen, centreX - half, centreY - half, glyphExtent, glyphExtent);
                break;
            case CaptionButtonKind.Close:
                graphics.DrawLine(pen, centreX - half, centreY - half, centreX + half, centreY + half);
                graphics.DrawLine(pen, centreX - half, centreY + half, centreX + half, centreY - half);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown caption button.");
        }
    }

    private static void PaintRestoreGlyph(Graphics graphics, Pen pen, int centreX, int centreY, int half)
    {
        int offset = Math.Max(2, half / 2);
        graphics.DrawRectangle(pen, centreX - half, centreY - half + offset, half * 2 - offset, half * 2 - offset);
        graphics.DrawLine(pen, centreX - half + offset, centreY - half, centreX + half, centreY - half);
        graphics.DrawLine(pen, centreX + half, centreY - half, centreX + half, centreY + half - offset);
    }

    private static void PaintIcon(Graphics graphics, CaptionMetrics metrics, CaptionPaintRequest request)
    {
        Rectangle icon = metrics.IconBounds;
        using SolidBrush iconBrush = new(request.Palette.CaptionIcon.ToDrawingColor());
        graphics.FillRectangle(iconBrush, icon);

        int dotEdge = Math.Max(4, metrics.StatusDotPixels);
        Rectangle dot = new(
            icon.Right - dotEdge + IconStatusDotInset,
            icon.Bottom - dotEdge + IconStatusDotInset,
            dotEdge,
            dotEdge);

        using SolidBrush outline = new(request.Palette.Background.ToDrawingColor());
        graphics.FillEllipse(outline, dot.Left - 1, dot.Top - 1, dot.Width + 2, dot.Height + 2);
        using SolidBrush dotBrush = new(request.Status.DotColor.ToDrawingColor());
        graphics.FillEllipse(dotBrush, dot);
    }

    private static void PaintTitle(Graphics graphics, CaptionMetrics metrics, CaptionPaintRequest request)
    {
        if (metrics.TitleBounds.Width <= 0 || request.Title.Length == 0)
        {
            return;
        }

        using Font font = new("Segoe UI", metrics.TitleFontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
        using SolidBrush brush = new(request.Palette.TitleText.ToDrawingColor());
        using StringFormat format = new(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        RectangleF bounds = new(
            metrics.TitleBounds.Left,
            0,
            metrics.TitleBounds.Width,
            metrics.Height);
        graphics.DrawString(request.Title, font, brush, bounds, format);
    }

    private static void PaintStatus(Graphics graphics, CaptionMetrics metrics, CaptionPaintRequest request)
    {
        StatusRun[] runs = BuildStatusRuns(request);
        if (runs.Length == 0)
        {
            return;
        }

        using Font font = new("Segoe UI", metrics.StatusFontPixels, FontStyle.Regular, GraphicsUnit.Pixel);

        float textWidth = 0.0f;
        float textHeight = 0.0f;
        foreach (StatusRun run in runs)
        {
            SizeF measured = graphics.MeasureString(run.Text, font);
            textWidth += measured.Width;
            textHeight = Math.Max(textHeight, measured.Height);
        }

        int gap = CaptionMetrics.Scale(CaptionMetrics.ContentGap, metrics.Dpi);
        int groupWidth = (int)Math.Ceiling(textWidth) + gap + metrics.StatusDotPixels;
        int groupLeft = Math.Max(metrics.TitleBounds.Right, metrics.StatusRightLimit - groupWidth);
        int textTop = (int)Math.Round((metrics.Height - textHeight) / 2.0, MidpointRounding.AwayFromZero);

        Rectangle dot = new(
            groupLeft,
            (metrics.Height - metrics.StatusDotPixels) / 2,
            metrics.StatusDotPixels,
            metrics.StatusDotPixels);
        using SolidBrush dotBrush = new(request.Status.DotColor.ToDrawingColor());
        graphics.FillEllipse(dotBrush, dot);

        int cursor = dot.Right + gap;
        foreach (StatusRun run in runs)
        {
            using SolidBrush brush = new(run.Color.ToDrawingColor());
            graphics.DrawString(run.Text, font, brush, cursor, textTop);
            cursor += (int)Math.Ceiling(graphics.MeasureString(run.Text, font).Width);
        }
    }

    /// <summary>
    /// Builds the status runs in drawing order. The gate tier label follows the main text as one
    /// space plus a dimmed run.
    /// </summary>
    private static StatusRun[] BuildStatusRuns(CaptionPaintRequest request)
    {
        string mainText = request.Status.CaptionText;
        string tierLabel = request.Status.GateTierLabel;
        if (mainText.Length == 0 && tierLabel.Length == 0)
        {
            return [];
        }

        CaptionColor dimmed = request.Palette.StatusText.Mix(
            request.Palette.Background,
            GateTierBackgroundWeightPercent);
        List<StatusRun> runs = [];
        AppendRun(runs, mainText, request.Palette.StatusText);
        AppendRun(runs, tierLabel, dimmed);
        return [.. runs];
    }

    private static void AppendRun(List<StatusRun> runs, string text, CaptionColor color)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (runs.Count > 0)
        {
            runs.Add(new StatusRun(" ", color));
        }

        runs.Add(new StatusRun(text, color));
    }

    /// <summary>One coloured piece of the status text, drawn left to right.</summary>
    private readonly record struct StatusRun(string Text, CaptionColor Color);

    private static CaptionButtonState StateOf(CaptionPaintRequest request, CaptionButtonKind kind)
    {
        if (request.PressedButton == kind)
        {
            return CaptionButtonState.Pressed;
        }

        return request.HoveredButton == kind ? CaptionButtonState.Hover : CaptionButtonState.Normal;
    }
}
