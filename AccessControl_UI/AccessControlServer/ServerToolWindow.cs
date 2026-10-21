// 파일 역할: 관리 서버의 목록·설정 패널을 별도 창으로 옮겼다가 닫을 때 돌려주는 공통 창입니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;
using System.Windows.Controls;

namespace AccessControlServer;

// Owns a view while open, then releases it so the same live controls can be reopened safely.
public sealed class ServerToolWindow : Window
{
    public ServerToolWindow(string title, string description, UIElement view)
    {
        Title = title;
        Width = 1100;
        Height = 780;
        MinWidth = 850;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "StudioBackground");
        var root = new DockPanel { Margin = new Thickness(22) };
        Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 25,
                FontWeight = FontWeights.Bold
            }
        );
        var caption = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "StudioMuted");
        header.Children.Add(caption);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var host = new ContentControl { Content = view };
        root.Children.Add(host);
        Closed += (_, _) => host.Content = null;
    }
}
