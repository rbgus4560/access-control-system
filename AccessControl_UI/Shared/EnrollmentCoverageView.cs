// 파일 역할: 수집한 등록 방향과 현재 자세를 간단한 그림으로 표시하는 WPF 요소입니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AccessControl.Ai;

public sealed class EnrollmentCoverageView : FrameworkElement
{
    private readonly HashSet<string> collected = new(StringComparer.Ordinal);
    private string current = "UNKNOWN";

    public EnrollmentCoverageView()
    {
        Width = 152;
        Height = 136;
        Margin = new Thickness(4);
    }

    public void Update(IEnumerable<string> poses, string pose)
    {
        collected.Clear();
        collected.UnionWith(poses);
        current = pose;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Point center = new(76, 66);
        double radius = 45;
        dc.DrawEllipse(null, new Pen(Brushes.LightSlateGray, 1), center, radius, radius);
        string[] names =
        [
            "UP",
            "UP_RIGHT",
            "RIGHT",
            "DOWN_RIGHT",
            "DOWN",
            "DOWN_LEFT",
            "LEFT",
            "UP_LEFT"
        ];
        string[] labels = ["위", "우상", "오른쪽", "우하", "아래", "좌하", "왼쪽", "좌상"];
        for (int i = 0; i < 8; i++)
        {
            double angle = (-90 + i * 45) * Math.PI / 180;
            Draw(
                names[i],
                labels[i],
                new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                17
            );
        }

        Draw("FRONT", "정면", center, 22);
        void Draw(string name, string text, Point point, double size)
        {
            var fill = collected.Contains(name) ? Brushes.ForestGreen : Brushes.WhiteSmoke;
            var stroke = current == name ? Brushes.DarkOrange : Brushes.SlateGray;
            dc.DrawEllipse(fill, new Pen(stroke, current == name ? 3 : 1), point, size, size);
            var label = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"),
                10.5,
                collected.Contains(name) ? Brushes.White : Brushes.DarkSlateGray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(label, new(point.X - label.Width / 2, point.Y - label.Height / 2));
        }
    }
}
