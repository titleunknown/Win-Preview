using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinPreview.Core;
using WinPreview.Core.Signatures;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinPreview.Dialogs;

/// <summary>Captures a handwritten signature drawn with a mouse, pen, or finger.</summary>
public static class SignatureDialog
{
    public static async Task<Signature?> ShowAsync(XamlRoot root)
    {
        var strokes = new List<List<PointD>>();
        List<PointD>? current = null;
        Polyline? currentLine = null;

        var canvas = new Canvas { Background = new SolidColorBrush(Colors.White), Width = 480, Height = 200 };
        var baseline = new Line { X1 = 30, X2 = 450, Y1 = 150, Y2 = 150, Stroke = new SolidColorBrush(Colors.LightGray), StrokeThickness = 1 };
        var hint = new TextBlock
        {
            Text = "Sign here with your mouse, pen, or finger",
            Foreground = new SolidColorBrush(Colors.Gray), FontSize = 12, IsHitTestVisible = false,
        };
        Canvas.SetLeft(hint, 30);
        Canvas.SetTop(hint, 160);
        canvas.Children.Add(baseline);
        canvas.Children.Add(hint);

        var description = new TextBox { PlaceholderText = "Description (optional), e.g. “Full signature” or “Initials”", Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel
        {
            Children =
            {
                new Border { Child = canvas, BorderBrush = new SolidColorBrush(Colors.Gray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) },
                description,
            },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Create Signature", Content = panel,
            PrimaryButtonText = "Done", SecondaryButtonText = "Clear", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false,
        };

        canvas.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(canvas).Position;
            current = [new PointD(p.X, p.Y)];
            currentLine = new Polyline
            {
                Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Points = { p },
            };
            canvas.Children.Add(currentLine);
            canvas.CapturePointer(e.Pointer);
            hint.Visibility = Visibility.Collapsed;
            e.Handled = true;
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (current == null || currentLine == null) return;
            foreach (var pt in e.GetIntermediatePoints(canvas).Reverse())
            {
                var p = pt.Position;
                if (p.X < 0 || p.Y < 0 || p.X > canvas.Width || p.Y > canvas.Height) continue;
                var last = current[^1];
                if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 0.7) continue;
                current.Add(new PointD(p.X, p.Y));
                currentLine.Points.Add(p);
            }
        };
        void End()
        {
            if (current != null)
            {
                if (current.Count == 1) current.Add(current[0] with { X = current[0].X + 0.5 });
                strokes.Add(current);
            }
            current = null;
            currentLine = null;
            dialog.IsPrimaryButtonEnabled = strokes.Count > 0;
        }
        canvas.PointerReleased += (_, e) => { End(); canvas.ReleasePointerCaptures(); };
        canvas.PointerCaptureLost += (_, _) => End();
        canvas.Loaded += (_, _) => SetCursor(canvas);

        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            strokes.Clear();
            foreach (var l in canvas.Children.OfType<Polyline>().ToList()) canvas.Children.Remove(l);
            hint.Visibility = Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = false;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || strokes.Count == 0) return null;
        var sig = Signature.FromStrokes(strokes);
        sig.Description = description.Text.Trim();
        return sig;
    }

    private static void SetCursor(UIElement e)
    {
        // ProtectedCursor is only settable from a subclass; reflection keeps this dialog self-contained.
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(e, InputSystemCursor.Create(InputSystemCursorShape.Cross));
    }

    /// <summary>A small vector preview of a saved signature.</summary>
    public static FrameworkElement Preview(Signature sig, double height = 36, double maxWidth = 220)
    {
        height = Math.Min(height, maxWidth / Math.Max(0.01, sig.AspectRatio));
        var geo = new PathGeometry();
        foreach (var s in sig.Strokes.Where(s => s.Count > 0))
        {
            var fig = new PathFigure { StartPoint = new(s[0].X * height, s[0].Y * height), IsFilled = false };
            var poly = new PolyLineSegment();
            foreach (var p in s.Skip(1)) poly.Points.Add(new(p.X * height, p.Y * height));
            fig.Segments.Add(poly);
            geo.Figures.Add(fig);
        }
        return new Path
        {
            Data = geo, Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = sig.AspectRatio * height + 2, Height = height + 2, Stretch = Stretch.None,
        };
    }
}
