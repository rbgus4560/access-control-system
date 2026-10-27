// 장치·DB에 접근하지 않는 합성 화면 검증 진입점.

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
    private void ShowPreview()
    {
        Window target = this;
        if (Environment.GetCommandLineArgs().Contains("--employees-preview"))
        {
            target = new EmployeesWindow(new("서버 설정에서 연결", 5432, "미연결", ""), "", () => false) { Owner = this };
            target.Show();
        }
        else if (Environment.GetCommandLineArgs().Contains("--settings-preview"))
        {
            ShowServerSettings(this, new RoutedEventArgs());
            target = settingsWindow!;
        }
        if (Environment.GetCommandLineArgs().Contains("--calendar-preview"))
        {
            var calendar = new System.Windows.Controls.Calendar { Style = (Style)FindResource("StudioCalendar"), SelectedDate = DateTime.Today, DisplayDate = DateTime.Today };
            target = new Window { Width = 380, Height = 420, Content = calendar, Owner = this };
            target.Show();
        }
        if (Environment.GetCommandLineArgs().Contains("--playback-tools-preview"))
        {
            target = new ServerPlaybackWindow(() => null, media) { Owner = this };
            target.Show();
        }
        if (Environment.GetCommandLineArgs().Contains("--history-filters-preview"))
        {
            rows.Add(new AccessEventRow("2026-10-27 12:34:56", "CAM-01", "TEST", "미리보기", "GRANTED", "ACCESS_ALLOWED", "0.234", Guid.NewGuid()));
            HistorySearch.Text = "granted";
            ApplyHistoryFilters();
            if (historyView!.Cast<object>().Count() != 1) throw new InvalidOperationException("검색 검사 실패");
            HistorySearch.Text = "없는이름";
            if (historyView!.Cast<object>().Count() != 1) throw new InvalidOperationException("입력 중 검색이 적용됐습니다");
            ApplyHistoryFilters();
            if (historyView!.Cast<object>().Any()) throw new InvalidOperationException("검색 제외 검사 실패");
            HistorySearch.Text = "미리보기";
            HistoryFrom.SelectedDate = new DateTime(2026, 10, 28);
            ApplyHistoryFilters();
            if (historyView!.Cast<object>().Any()) throw new InvalidOperationException("기간 필터 검사 실패");
            HistoryFrom.SelectedDate = new DateTime(2026, 10, 27);
            HistoryUntil.SelectedDate = new DateTime(2026, 10, 27);
            ApplyHistoryFilters();
            ShowHistory(this, new RoutedEventArgs());
            target = historyWindow!;
        }
        if (Environment.GetCommandLineArgs().Contains("--sidebar-collapsed-preview"))
            SetSidebarCollapsed(true);
        if (Environment.GetCommandLineArgs().Contains("--menu-groups-preview"))
        {
            EmployeesMenu.IsExpanded = true;
            RecordsMenu.IsExpanded = true;
        }
        if (Environment.GetCommandLineArgs().Contains("--status-hint-preview"))
        {
            StatusHintText.Text = "TCP/IP · 수신 대기";
            StatusHint.Visibility = Visibility.Visible;
        }
        if (Environment.GetCommandLineArgs().Contains("--camera-placement-preview"))
        {
            var drawing = new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.SlateGray, null,
                new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 640, 480)));
            ServerVideo.Source = new System.Windows.Media.DrawingImage(drawing);
            cameraSeen = true;
            cameraDisplayLabel = "CAM-01 · 배치 미리보기";
            pendingCamera = "CAM-01";
            PlaceSelectedCamera(4);
            if (cameraWorkspace.Slots[4] != "CAM-01" || cameraWorkspace.Slots.Count(x => x == "CAM-01") != 1)
                throw new InvalidOperationException("카메라 배치 검사 실패");
            cameraTiles[4].SetDisplayName(cameraDisplayLabel);
            CamerasMenu.IsExpanded = true;
            RenderCameraList(true);
        }
        if (Environment.GetCommandLineArgs().Contains("--camera-grid-preview"))
        {
            // 장치·DB에 접근하지 않는 합성 영상으로 오버레이와 확대 창을 검사합니다.
            var drawing = new System.Windows.Media.GeometryDrawing(
                new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Colors.SlateGray, System.Windows.Media.Colors.DarkSlateGray, 90),
                null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 640, 480)));
            cameraSeen = true;
            CamerasMenu.IsExpanded = true;
            RenderCameraList(true);
            cameraTiles[0].Refresh(new System.Windows.Media.DrawingImage(drawing), true, true,
                new AccessDecision { Recorded = true, EventId = Guid.NewGuid(), Result = "GRANTED", PersonName = "미리보기 직원" });
            if (Environment.GetCommandLineArgs().Contains("--camera-expanded-preview"))
                target = cameraTiles[0].OpenExpanded();
        }
        AccessControl.Preview.UiPreviewCapture.Queue(target);
    }
}
