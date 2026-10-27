// 서버 사이드바 펼침·접힘과 상태 도움말.

using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AccessControl.Network;
using AccessControl.Faces;
using System.Windows;
using Npgsql;

namespace AccessControlServer;

public partial class MainWindow
{
    private void AnimateMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Expander { Content: FrameworkElement content } expander) return;
        if (!expander.IsExpanded) return;
        content.Measure(new Size(Math.Max(40, SidebarColumn.Width.Value - 24), double.PositiveInfinity));
        double height = content.DesiredSize.Height;
        content.ClipToBounds = true;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0, height, TimeSpan.FromMilliseconds(180));
        animation.Completed += (_, _) => { content.BeginAnimation(HeightProperty, null); content.Height = double.NaN; };
        content.BeginAnimation(HeightProperty, animation);
        e.Handled = true;
    }

    private void ShowStatusHint(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: string text })
        {
            StatusHintText.Text = text;
            StatusHint.Visibility = Visibility.Visible;
        }
    }

    private void HideStatusHint(object sender, System.Windows.Input.MouseEventArgs e) => StatusHint.Visibility = Visibility.Collapsed;

    private void ToggleSidebar(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!sidebarCollapsed);

    private void SetSidebarCollapsed(bool collapsed)
    {
        sidebarCollapsed = collapsed;
        Sidebar.Tag = collapsed ? "Collapsed" : "Expanded";
        SidebarColumn.Width = new GridLength(collapsed ? 64 : 220);
        SidebarLayout.Margin = collapsed ? new Thickness(6, 18, 6, 18) : new Thickness(12, 18, 12, 18);
        SidebarToggle.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SidebarToggle.ToolTip = collapsed ? "메뉴 펼치기" : "메뉴 접기";
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle, (string)SidebarToggle.ToolTip);
        CamerasMenu.IsExpanded = false;
        EmployeesMenu.IsExpanded = false;
        RecordsMenu.IsExpanded = false;
        UpdateSidebarElements(Sidebar, collapsed);
        cameraListKey = "";
        RenderCameraList(monitor.Running && current is not null && Stopwatch.GetElapsedTime(receivedAt) < MonitorProtocol.Timeout);
    }

    // 글자와 아이콘의 배치만 바꾸며 관제 연결·영상과 메뉴 동작은 유지합니다.
    private static void UpdateSidebarElements(DependencyObject parent, bool collapsed)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is FrameworkElement element)
            {
                if (element.Tag as string == "SidebarLabel")
                    element.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                if (element.Tag as string == "SidebarIcon")
                    element.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 12, 0);
                if (element is System.Windows.Controls.RadioButton radio)
                    radio.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            }
            if (child is DependencyObject dependency) UpdateSidebarElements(dependency, collapsed);
        }
    }

}
