// 파일 역할: 등록용 카메라 설정·AI 진단 패널을 별도 창에 표시합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;
using System.Windows.Controls;

namespace FaceEnrollment;

public sealed class EnrollmentSettingsWindow : Window
{
    public EnrollmentSettingsWindow(UIElement view)
    {
        Title = "등록 카메라 설정·진단";
        Width = 900;
        Height = 760;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        Content = root;
        var heading = new TextBlock
        {
            Text = "카메라 설정·분석 상세",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 18)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var host = new ContentControl { Content = view };
        root.Children.Add(
            new ScrollViewer
            {
                Content = host,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        );
        Closed += (_, _) => host.Content = null;
    }
}
