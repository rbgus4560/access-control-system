// 카메라명·시계·출입 판정 오버레이.

using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using AccessControl.Faces;

namespace AccessControl.Ai;

public sealed partial class FaceAiPanel
{
    private string displayCameraName = "CAM-01 · 출입구";
    private readonly TextBox cameraDisplayName = new() { MaxLength = 60 };
    private Grid? recognitionViewport;
    private readonly Grid videoOverlay = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock videoClock = new()
    {
        FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
        Background = Brushes.Transparent,
        Margin = new Thickness(14), Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock cameraCaption = new()
    {
        Text = "CAM-01 · 출입구", FontSize = 18, FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White, Background = Brushes.Transparent,
        Margin = new Thickness(14), Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
    };
    private readonly TextBlock decisionText = new()
    {
        FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right
    };
    private readonly Border decisionRibbon = new()
    {
        VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(18, 13, 18, 13),
        Visibility = Visibility.Collapsed
    };
    private readonly HashSet<Guid> shownDecisions = new();
    private readonly Queue<Guid> shownDecisionOrder = new();
    private long ribbonStarted;

    public void ShowAccessDecision(Guid eventId, bool allowed, string name)
    {
        if (!shownDecisions.Add(eventId)) return;
        shownDecisionOrder.Enqueue(eventId);
        if (shownDecisionOrder.Count > 256) shownDecisions.Remove(shownDecisionOrder.Dequeue());
        decisionText.Text = (allowed ? "출입 허가" : "출입 거부") + " · " + name;
        decisionRibbon.Background = Brushes.Transparent;
        decisionText.Foreground = new SolidColorBrush(allowed
            ? Color.FromRgb(83, 240, 139) : Color.FromRgb(255, 112, 112));
        ribbonStarted = Stopwatch.GetTimestamp();
        decisionRibbon.Visibility = Visibility.Visible;
        decisionRibbon.Opacity = 1;
        UpdateVideoOverlay();
    }

    // 장치 접근 없는 개발 미리보기입니다. 실제 영상·출입 결과를 생성하지 않습니다.
    public void PreviewVideoOverlay(bool allowed)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-preview")) return;
        var drawing = new GeometryDrawing(
            new LinearGradientBrush(Color.FromRgb(80, 100, 120), Color.FromRgb(35, 48, 65), 90),
            null, new RectangleGeometry(new Rect(0, 0, 640, 480)));
        live.Source = new DrawingImage(drawing);
        ShowAccessDecision(Guid.NewGuid(), allowed, "미리보기 직원");
    }

    public void ClearAccessDecision()
    {
        ribbonStarted = 0;
        decisionRibbon.Visibility = Visibility.Collapsed;
        cameraCaption.Margin = new Thickness(14);
    }

    // Uniform 표시의 검은 여백을 제외한 실제 영상 사각형에만 시각 정보를 배치합니다.
    private void UpdateVideoOverlay()
    {
        if (recognitionViewport is null) return;
        var source = analysisView.Visibility == Visibility.Visible ? analysisView.Source : live.Source;
        if (source is null || source.Width <= 0 || source.Height <= 0)
        {
            videoOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        double scale = Math.Min(recognitionViewport.ActualWidth / source.Width,
            recognitionViewport.ActualHeight / source.Height);
        videoOverlay.Width = Math.Max(0, source.Width * scale);
        videoOverlay.Height = Math.Max(0, source.Height * scale);
        videoOverlay.Visibility = Visibility.Visible;
        videoClock.Text = DateTime.Now.ToString("yyyy-MM-dd'- 'HH:mm:ss");
        if (ribbonStarted != 0)
        {
            double age = Stopwatch.GetElapsedTime(ribbonStarted).TotalSeconds;
            if (age >= 2.8) ClearAccessDecision();
            else decisionRibbon.Opacity = age <= 2.2 ? 1 : Math.Max(0, (2.8 - age) / .6);
        }
        cameraCaption.Margin = new Thickness(14, 14, 14,
            decisionRibbon.Visibility == Visibility.Visible ? decisionRibbon.ActualHeight + 22 : 14);
    }

    private bool analysisVisible;
}
