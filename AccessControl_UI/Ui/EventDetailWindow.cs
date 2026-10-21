// 파일 역할: 서버에 기록된 인증 시각·직원·판정·거리·이벤트 ID를 별도 상세 창에 표시합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;
using System.Windows.Controls;
using AccessControl.Network;

namespace AccessControl.Ui;

public sealed class EventDetailWindow : Window
{
    public EventDetailWindow(
        string time,
        string name,
        string employee,
        string result,
        string reason,
        string distance,
        Guid id,
        string source
    )
    {
        Title = "출입 기록 상세";
        Width = 680;
        Height = 600;
        MinWidth = 540;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "StudioBackground");
        var root = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(
            new TextBlock
            {
                Text = "출입 기록 상세",
                FontSize = 25,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 18)
            }
        );
        string display = result switch
        {
            "GRANTED" => "출입 허가",
            "DENIED" => "출입 거부",
            _ => "판정 보류"
        };
        var verdict = new TextBlock
        {
            Text = display,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 20)
        };
        verdict.Foreground =
            result == "GRANTED" ? System.Windows.Media.Brushes.DarkGreen
            : result == "DENIED" ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DimGray;
        root.Children.Add(verdict);
        foreach (
            var pair in new[]
            {
                ("시각", time),
                ("이름", name),
                ("사원번호", employee),
                ("판정", result),
                ("사유", AccessPolicy.Explain(reason)),
                ("코사인 거리", distance),
                ("이벤트 ID", id.ToString())
            }
        )
        {
            var row = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = pair.Item1 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "StudioMuted");
            row.Children.Add(label);
            var value = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(pair.Item2) ? "—" : pair.Item2,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            root.Children.Add(row);
        }

        root.Children.Add(new Separator());
        root.Children.Add(new TextBlock { Text = source, TextWrapping = TextWrapping.Wrap });
        var close = new Wpf.Ui.Controls.Button
        {
            Content = "닫기",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
    }
}
