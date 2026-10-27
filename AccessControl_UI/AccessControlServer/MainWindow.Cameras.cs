// 카메라 목록과 9분할 배치의 UI 동작. 저장 형식은 CameraWorkspace에서 관리합니다.
using System.IO;
using System.Diagnostics;
using AccessControl.Network;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessControlServer;

public partial class MainWindow
{
    private void RefreshCameraTiles()
    {
        if (cameraTiles.Count == 0) return;
        if (preview && Environment.GetCommandLineArgs().Contains("--camera-grid-preview")) return;
        bool connected = monitor.Running && current is not null
            && Stopwatch.GetElapsedTime(receivedAt) < MonitorProtocol.Timeout;
        var access = connected && current?.Snapshot is { } snapshot
            && AccessPolicy.IsCurrent(snapshot, DateTimeOffset.UtcNow)
            && current.Decision?.AttemptId == snapshot.AttemptId ? current.Decision : null;
        cameraSeen |= connected || ServerVideo.Source is not null;
        for (int index = 0; index < cameraTiles.Count; index++)
        {
            bool assigned = cameraWorkspace.Slots[index] == "CAM-01";
            cameraTiles[index].AssignCamera(assigned ? "CAM-01" : "", assigned ? cameraDisplayLabel : "빈 화면");
            cameraTiles[index].Refresh(assigned ? ServerVideo.Source : null,
                assigned && ServerVideo.Source is not null, assigned && connected, assigned ? access : null);
        }
        RenderCameraList(connected);
    }


    private readonly CameraWorkspace cameraWorkspace = new();
    private string? pendingCamera;
    private string cameraDisplayLabel = "CAM-01 · 출입구";
    private bool cameraSeen;
    private string cameraListKey = "";

    private void InitializeCameraWorkspace()
    {
        // 미리보기는 개인 배치를 읽거나 저장하지 않습니다.
        if (!preview)
        {
            var saved = CameraWorkspace.Load();
            cameraWorkspace.Slots = saved.Slots;
        }
        for (int index = 0; index < cameraTiles.Count; index++)
        {
            int slot = index;
            cameraTiles[index].SlotClicked += () => PlaceSelectedCamera(slot);
        }
        RenderCameraList(false);
    }

    private void RenderCameraList(bool connected)
    {
        string key = $"{connected}|{cameraSeen}|{cameraDisplayLabel}|{pendingCamera}|{sidebarCollapsed}|";
        if (key == cameraListKey) return;
        cameraListKey = key;
        CameraList.Children.Clear();
        Button CameraButton()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "\uE714", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, Margin = new Thickness(0, 0, sidebarCollapsed ? 0 : 8, 0) });
            if (!sidebarCollapsed) row.Children.Add(new TextBlock { Text = cameraDisplayLabel + (connected ? "" : " · 미연결"), MaxWidth = 145, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Style = (Style)FindResource("CameraListButton"), Content = row, Padding = new Thickness(8), Margin = new Thickness(0, 2, 0, 2), HorizontalContentAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                Background = pendingCamera == "CAM-01" ? new SolidColorBrush(Color.FromRgb(224, 237, 255)) : Brushes.Transparent, BorderThickness = new Thickness(0) };
            System.Windows.Automation.AutomationProperties.SetName(button, cameraDisplayLabel);
            button.Click += (_, _) =>
            {
                pendingCamera = "CAM-01";
                PlacementHint.Text = cameraDisplayLabel + " 선택됨 · 배치할 화면을 클릭하세요";
                RenderCameraList(connected);
            };
            return button;
        }
        if (!cameraSeen)
        {
            if (!sidebarCollapsed) CameraList.Children.Add(new TextBlock { Text = "연결된 카메라 없음", Foreground = Brushes.SlateGray, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
        }
        else CameraList.Children.Add(CameraButton());
    }

    private void PlaceSelectedCamera(int slot)
    {
        if (pendingCamera is null) return;
        var old = (string?[])cameraWorkspace.Slots.Clone();
        for (int index = 0; index < 9; index++)
            if (cameraWorkspace.Slots[index] == pendingCamera) cameraWorkspace.Slots[index] = null;
        cameraWorkspace.Slots[slot] = pendingCamera;
        try { if (!preview) cameraWorkspace.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cameraWorkspace.Slots = old;
            PlacementHint.Text = "배치를 저장하지 못했습니다. 저장 폴더 권한을 확인하세요.";
            return;
        }
        pendingCamera = null;
        PlacementHint.Text = $"{cameraDisplayLabel} · {slot + 1}번 화면에 배치됨";
        RefreshCameraTiles();
    }

}
