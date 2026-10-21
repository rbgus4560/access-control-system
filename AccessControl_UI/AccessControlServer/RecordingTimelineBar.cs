// 파일 역할: 녹화가 있는 시간대를 타임라인에 그립니다. 선택 시각 변경을 재생 패널에 전달합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AccessControl.Faces;

namespace AccessControlServer;

public sealed class RecordingTimelineBar : FrameworkElement
{
    public RecordingDay? Day { get; set; }
    public double PositionSeconds { get; set; }

    private double viewStart,
        duration = 86400;
    public event Action<double>? Seek;

    public RecordingTimelineBar()
    {
        Height = 54;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    public void Zoom(double seconds)
    {
        duration = Math.Clamp(seconds, 60, 86400);
        viewStart = Math.Clamp(PositionSeconds - duration / 2, 0, 86400 - duration);
        InvalidateVisual();
    }

    public void Follow(double position)
    {
        PositionSeconds = position;
        if (duration < 86400 && (position < viewStart || position > viewStart + duration))
            viewStart = Math.Clamp(position - duration / 2, 0, 86400 - duration);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth;
        if (width < 1)
            return;
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(204, 211, 220)),
            null,
            new Rect(0, 4, width, 22)
        );
        if (Day is { } day)
            foreach (var item in day.Segments)
            {
                double start = (item.StartTime - day.Start).TotalSeconds,
                    end = (item.EndTime!.Value - day.Start).TotalSeconds;
                double left = Math.Max(0, (start - viewStart) / duration * width),
                    right = Math.Min(width, (end - viewStart) / duration * width);
                if (right > left)
                    dc.DrawRectangle(
                        Brushes.SteelBlue,
                        null,
                        new Rect(left, 4, Math.Max(1, right - left), 22)
                    );
            }

        double x = (PositionSeconds - viewStart) / duration * width;
        if (x >= 0 && x <= width)
            dc.DrawLine(new Pen(Brushes.DarkOrange, 2), new Point(x, 0), new Point(x, 29));
        for (int i = 0; i <= 4; i++)
        {
            string text = TimeSpan
                .FromSeconds(viewStart + duration * i / 4)
                .ToString(
                    duration < 3600 ? @"hh\:mm\:ss" : @"hh\:mm",
                    CultureInfo.InvariantCulture
                );
            if (viewStart + duration * i / 4 >= 86400)
                text = "24:00";
            var formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"),
                11,
                Brushes.DimGray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(
                formatted,
                new Point(
                    Math.Clamp(
                        width * i / 4 - formatted.Width / 2,
                        0,
                        Math.Max(0, width - formatted.Width)
                    ),
                    31
                )
            );
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0)
            return;
        double seconds = Math.Clamp(
            viewStart + e.GetPosition(this).X / ActualWidth * duration,
            0,
            86399.999
        );
        Seek?.Invoke(seconds);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Day is not { } day || ActualWidth <= 0)
            return;
        double seconds = Math.Clamp(
            viewStart + e.GetPosition(this).X / ActualWidth * duration,
            0,
            86399.999
        );
        var at = day.Start.AddSeconds(seconds);
        ToolTip =
            at.ToLocalTime().ToString("HH:mm:ss")
            + (
                RecordingTimeline.FindAt(day.Segments, at) is null
                    ? " · 녹화 없음"
                    : " · 녹화 있음 · 클릭해서 재생"
            );
    }
}
