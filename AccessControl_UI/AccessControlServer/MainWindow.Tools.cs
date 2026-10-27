// 기록 상세·재생·등록 창과 대시보드 표시.

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
    private void ShowDetail(object s, RoutedEventArgs e)
    {
        if (Events.SelectedItem is AccessEventRow row)
            OpenDetail(row);
        else
            MessageBox.Show("조회할 이벤트를 선택해 주세요.");
    }

    private void OpenDetail(AccessEventRow row) =>
        new AccessControl.Ui.EventDetailWindow(
            row.시각,
            row.이름 ?? "미확인",
            row.사원번호 ?? "—",
            row.결과,
            row.사유,
            row.거리,
            row.이벤트ID,
            "서버 DB에 저장된 출입 판정 기록입니다."
        )
        {
            Owner = this
        }.Show();

    private void OpenRecentDetail(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e
    )
    {
        if (
            closing
            || e.AddedItems.Count == 0
            || RecentPreview.SelectedItem is not AccessEventRow row
        )
            return;
        OpenDetail(row);
        RecentPreview.SelectedIndex = -1;
    }

    private void RefreshRecentPreview()
    {
        if (closing)
            return;
        RecentPreview.ItemsSource = rows.Take(5).ToList();
        RecentStatus.Text =
            rows.Count == 0
                ? "표시할 기록 없음 · 서버 설정에서 DB 연결 후 조회하세요."
                : $"최신 {Math.Min(5, rows.Count)}건 · 선택하면 상세 창이 열립니다.";
    }

    private void RefreshDashboard()
    {
        if (closing)
            return;
        ServerClock.Text = DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
        DashboardStatus.Text =
            (Volatile.Read(ref faceDatabase) is null ? "DB 미설정" : "DB 연결 설정 완료")
            + "  ·  "
            + (monitor.Running ? "TCP 수신 중" : "TCP 중지")
            + "  ·  "
            + (media.Connected ? "영상·클립 요청 채널 연결됨" : "영상·클립 요청 채널 대기");
        DbIndicator.Text = faceDatabase is null ? "DB · 연결 필요" : "DB · 사용 준비";
        DbIndicator.Foreground = faceDatabase is null ? System.Windows.Media.Brushes.SlateGray : System.Windows.Media.Brushes.SeaGreen;
        TcpIndicator.Text = monitor.Running ? $"TCP · 수신 중 :{monitor.BoundPort}" : "TCP · 중지";
        TcpIndicator.Foreground = monitor.Running ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.SlateGray;
        bool videoFresh = displayedVideo is not null && Stopwatch.GetElapsedTime(displayedVideo.At) < TimeSpan.FromSeconds(3);
        StreamIndicator.Text = videoFresh ? "영상 · 수신 중" : "영상 · 대기";
        DbLight.Fill = faceDatabase is not null ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Firebrick;
        TcpLight.Fill = monitor.Running ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Firebrick;
        StreamLight.Fill = videoFresh ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Firebrick;
        DbStatusIcon.ToolTip = DbIndicator.Text;
        TcpStatusIcon.ToolTip = "TCP/IP · " + TcpIndicator.Text;
        StreamStatusIcon.ToolTip = StreamIndicator.Text;
        RefreshCameraTiles();
    }

    private void ShowMonitoring(object sender, RoutedEventArgs e) => RefreshDashboard();

    private static void BringForward(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ShowServerSettings(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is not null)
        {
            BringForward(settingsWindow);
            return;
        }

        settingsWindow = new ServerToolWindow(
            "서버 설정",
            "DB·관리자와 클라이언트 수신 설정을 관리합니다. 창을 닫아도 연결은 유지됩니다.",
            SettingsControls
        )
        {
            Owner = this
        };
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    private void ShowHistory(object sender, RoutedEventArgs e)
    {
        if (historyWindow is not null)
        {
            BringForward(historyWindow);
            return;
        }

        historyWindow = new ServerToolWindow(
            "출입 기록",
            "",
            HistoryControls
        )
        {
            Owner = this
        };
        historyWindow.Closed += (_, _) => historyWindow = null;
        historyWindow.Show();
    }

    private async void PlaySelectedEvent(object sender, RoutedEventArgs e)
    {
        if (eventPlaybackBusy)
            return;
        if (Events.SelectedItem is not AccessEventRow row)
        {
            HistoryStatus.Text = "재생할 출입 기록을 선택해 주세요.";
            return;
        }

        var db = Volatile.Read(ref faceDatabase);
        if (db is null)
        {
            HistoryStatus.Text = "서버 DB 연결을 먼저 해 주세요.";
            return;
        }

        eventPlaybackBusy = true;
        EventPlaybackButton.IsEnabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(15000);
        try
        {
            HistoryStatus.Text = "출입 시각 조회·녹화 열기 요청 중";
            var request = await db.PlaybackEventAsync(row.이벤트ID, timeout.Token);
            if (closing || !ReferenceEquals(db, Volatile.Read(ref faceDatabase)))
                return;
            var reply = await PlaybackWindow().OpenAsync(request, timeout.Token);
            if (closing)
                return;
            HistoryStatus.Text =
                reply.Success && reply.HasRecording ? "서버 녹화 재생 창 확인" : reply.Message;
            if (!reply.HasRecording || !reply.Success)
                MessageBox.Show(reply.Message, "출입 기록 영상");
        }
        catch (Exception)
        {
            if (!closing)
            {
                HistoryStatus.Text = "재생 연결 실패";
                MessageBox.Show(
                    "클라이언트 연결·서버 DB·15번 SQL 적용을 확인하세요.",
                    "출입 기록 영상"
                );
            }
        }
        finally
        {
            eventPlaybackBusy = false;
            if (!closing)
                EventPlaybackButton.IsEnabled = true;
        }
    }

    private void DoubleClickEvent(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (
            System.Windows.Controls.ItemsControl.ContainerFromElement(
                Events,
                e.OriginalSource as DependencyObject
            ) is System.Windows.Controls.DataGridRow
        )
            PlaySelectedEvent(sender, e);
    }

    private ServerPlaybackWindow PlaybackWindow()
    {
        if (playbackWindow is null)
        {
            playbackWindow = new ServerPlaybackWindow(() => Volatile.Read(ref faceDatabase), media)
            {
                Owner = this
            };
            playbackWindow.Closed += (_, _) => playbackWindow = null;
            playbackWindow.Show();
        }

        if (playbackWindow.WindowState == WindowState.Minimized)
            playbackWindow.WindowState = WindowState.Normal;
        playbackWindow.Activate();
        return playbackWindow;
    }

    private void ShowTimeline(object sender, RoutedEventArgs e) => PlaybackWindow();

    private void CopyPairing(object sender, RoutedEventArgs e)
    {
        try
        {
            if (serverLink is null || !int.TryParse(TcpPort.Text, out int port))
                throw new InvalidOperationException();
            Clipboard.SetText(PairingStore.Code(serverLink, ServerAddress.Text.Trim(), port));
            TcpStatus.Text =
                "연결 코드 복사 완료 · 클라이언트 설정에 붙여넣으세요. 코드는 비밀번호처럼 보관하세요.";
        }
        catch (Exception)
        {
            MessageBox.Show("클라이언트에서 접속 가능한 서버 IPv4 주소와 포트를 선택하세요.");
        }
    }

    private void ShowEnrollment(object sender, RoutedEventArgs e)
    {
        if (enrollmentProcess is { HasExited: false })
        {
            MessageBox.Show("열린 얼굴 등록 창을 확인하세요.");
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        var config = new DirectoryInfo(baseDir).Parent?.Name == "Release" ? "Release" : "Debug";
        var paths = new[]
        {
            Path.GetFullPath(
                Path.Combine(
                    baseDir,
                    $"../../../../FaceEnrollment/bin/{config}/net10.0-windows/FaceEnrollment.exe"
                )
            ),
            Path.Combine(baseDir, "FaceEnrollment.exe")
        };
        var exe = paths.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            MessageBox.Show("Start_Server.cmd로 서버와 얼굴 등록 앱을 먼저 빌드하세요.");
            return;
        }

        try
        {
            enrollmentProcess?.Dispose();
            enrollmentProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show("얼굴 등록 앱을 열 수 없습니다.");
        }
    }}
