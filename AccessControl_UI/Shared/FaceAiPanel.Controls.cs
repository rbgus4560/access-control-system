// 카메라 패널의 공통 컨트롤 생성.

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
    private static TextBlock Text(string value) =>
        new()
        {
            Text = value,
            Margin = new Thickness(3),
            TextWrapping = TextWrapping.Wrap
        };

    private static Button Button(string caption, Func<Task> action)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Content = caption,
            Margin = new Thickness(3),
            Padding = new Thickness(10, 7, 10, 7)
        };
        if (caption is "카메라·AI 연결" or "등록 시작")
            button.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "AI 연결");
            }
        };
        return button;
    }

    private static DockPanel PathRow(string caption, TextBox box, bool browse)
    {
        var row = new DockPanel { Margin = new Thickness(3) };
        var label = Text(caption);
        label.Width = 80;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        if (browse)
        {
            var pick = Button(
                "찾기",
                () =>
                {
                    var dialog = new OpenFileDialog
                    {
                        Filter = "Python 실행 파일|python.exe|실행 파일|*.exe"
                    };
                    if (dialog.ShowDialog() == true)
                        box.Text = dialog.FileName;
                    return Task.CompletedTask;
                }
            );
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
        }

        box.MinWidth = 120;
        box.Padding = new Thickness(4);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        row.Children.Add(box);
        return row;
    }

    private static Border ImagePanel(string? caption, Image image)
    {
        var dock = new DockPanel();
        if (caption is not null)
        {
            var label = Text(caption);
            label.Foreground = Brushes.White;
            DockPanel.SetDock(label, Dock.Top);
            dock.Children.Add(label);
        }

        dock.Children.Add(image);
        return new Border
        {
            Child = dock,
            Background = new SolidColorBrush(Color.FromRgb(20, 32, 51)),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Margin = new Thickness(4),
            Padding = new Thickness(8)
        };
    }

}
