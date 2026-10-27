// 파일 역할: 관리 서버의 DB 연결·TCP 수신·관제 화면을 조정합니다. 클라이언트 관측을 DB 권한 판정과 연결합니다.
// 기능 기준: 12.4. 서버 DB 세션 재사용과 선택형 자동 시작 및 관제 상태를 연결합니다.

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

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AccessEventRow> rows = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly MonitorServer monitor = new();
    private readonly VideoServer videoServer = new();
    private ReceivedVideo? displayedVideo;
    // 영상만 빠르게 갱신하고 대시보드·권한 상태 갱신은 기존 200ms 주기를 유지합니다.
    private readonly DispatcherTimer videoClock = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly DispatcherTimer monitorClock = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private MonitorPacket? current;
    private long receivedAt;
    private FaceDatabase? faceDatabase;
    private Task? faceRequests;
    private CancellationTokenSource? request;
    private bool closing;
    private readonly bool preview = Environment.GetCommandLineArgs().Contains("--ui-preview");
    private DatabaseEndpoint? connectedEndpoint;
    private string connectedPassword = "";
    private bool tcpBusy, tcpManuallyStopped;
    private readonly ServerSettingsStore startupSettings = new();
    private bool eventPlaybackBusy;
    private readonly MediaCoordinator media;
    private RemoteApiServer? remoteApi;
    private ServerLink? serverLink;
    private ServerPlaybackWindow? playbackWindow;
    private Process? enrollmentProcess;
    private ServerToolWindow? settingsWindow,
        historyWindow;

    private bool sidebarCollapsed;
    private readonly List<CameraTile> cameraTiles = new();

    public MainWindow()
    {
        InitializeComponent();
        for (int index = 1; index <= 9; index++)
        {
            string code = $"CAM-{index:00}";
            var tile = new CameraTile(code, index == 1 ? code + " · 출입구" : code + " · 미연결");
            cameraTiles.Add(tile);
            CameraGrid.Children.Add(tile);
        }
        InitializeCameraWorkspace();
        Closed += (_, _) => { foreach (var tile in cameraTiles) tile.CloseExpanded(); };
        Activated += (_, _) => NavLive.IsChecked = true;
        AuxiliaryViews.Children.Clear();
        InitializeHistoryFilters();
        rows.CollectionChanged += (_, _) => RefreshRecentPreview();
        RefreshRecentPreview();
        media = new MediaCoordinator(() => Volatile.Read(ref faceDatabase));
        try
        {
            if (!preview)
            {
                serverLink = PairingStore.Server();
                LinkSecurity.Server = serverLink;
            }
        }
        catch (Exception)
        {
            TcpStart.IsEnabled = false;
            TcpStatus.Text = "서버 연결 키를 읽을 수 없습니다. 기존 Windows 계정으로 실행하세요.";
        }

        ServerAddress.ItemsSource = System
            .Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Where(a => a != "127.0.0.1")
            .Distinct()
            .ToList();
        ServerAddress.SelectedIndex = 0;
        monitor.DecideAsync = (packet, token) =>
        {
            var db = Volatile.Read(ref faceDatabase);
            return db is not null
                ? Decide(db, packet, token)
                : Task.FromResult<AccessDecision?>(
                    new AccessDecision
                    {
                        AttemptId = packet.Snapshot?.AttemptId,
                        Result = packet.Snapshot?.AttemptId is null ? "WAITING" : "ERROR",
                        Reason = packet.Snapshot?.AttemptId is null
                            ? "NO_COMPARISON"
                            : "DB_NOT_CONNECTED"
                    }
                );
        };
        monitor.Received += packet => Dispatcher.BeginInvoke(new Action(() => Receive(packet)));
        monitor.StatusChanged += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (closing)
                        return;
                    TcpStatus.Text = text;
                    current = null;
                    TcpClientStatus.Text = "인증 클라이언트 미연결";
                    RefreshSnapshot();
                })
            );
        monitorClock.Tick += (_, _) =>
        {
            RefreshSnapshot();
            RefreshDashboard();
        };
        monitorClock.Start();
        videoClock.Tick += (_, _) => RefreshVideo();
        videoClock.Start();
        Loaded += async (_, _) =>
        {
            if (preview)
            {
                ShowPreview();
                return;
            }
            faceRequests = FaceApiServer.ListenAsync(HandleFaceRequestAsync, lifetime.Token);
            _ = ObserveFaceRequestsAsync();
            try
            {
                if (startupSettings.Load() is { } saved)
                {
                    HostInput.Text = saved.Endpoint.Host;
                    PortInput.Text = saved.Endpoint.Port.ToString();
                    DatabaseInput.Text = saved.Endpoint.Database;
                    UsernameInput.Text = saved.Endpoint.Username;
                    PasswordInput.Password = saved.Password;
                    TcpPort.Text = saved.TcpPort.ToString();
                    RememberConnection.IsChecked = true;
                    await ConnectDatabaseAsync();
                }
            }
            catch (Exception)
            {
                DbStatus.Text = "저장 설정 복원 실패 · 서버 설정에서 다시 연결하세요.";
            }
        };
    }

    private async Task<AccessDecision?> Decide(
        FaceDatabase db,
        MonitorPacket packet,
        CancellationToken token
    )
    {
        var decision = await db.DecideAsync(packet, token);
        return ReferenceEquals(db, Volatile.Read(ref faceDatabase))
            ? decision
            : new AccessDecision
            {
                Result = "ERROR",
                Reason = "DB_NOT_CONNECTED",
                AttemptId = packet.Snapshot?.AttemptId
            };
    }

    private async Task ObserveFaceRequestsAsync()
    {
        try
        {
            if (faceRequests is not null)
                await faceRequests;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!closing)
                FaceDbStatus.Text =
                    "직원·얼굴 요청 대기 실패 · 같은 사용자 계정의 서버 창을 하나만 실행해 주세요.";
        }
    }

    private void SelectAdmin(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Volatile.Read(ref faceDatabase) is { } db)
            db.Actor = (FaceAdmins.SelectedItem as FaceAdmin)?.Id ?? Guid.Empty;
    }

    private async void AddAdmin(object sender, RoutedEventArgs e)
    {
        var db = Volatile.Read(ref faceDatabase);
        if (db is null)
        {
            MessageBox.Show("DB 연결 및 목록 조회를 먼저 해 주세요.");
            return;
        }

        var dialog = new AdminSetupWindow(db) { Owner = this };
        dialog.ShowDialog();
        if (closing)
            return;
        try
        {
            var admins = await db.AdminsAsync(lifetime.Token);
            FaceAdmins.ItemsSource = admins;
            FaceAdmins.SelectedItem =
                admins.FirstOrDefault(a => a.Id == dialog.CreatedId) ?? admins.FirstOrDefault();
            FaceDbStatus.Text = "얼굴 등록 관리자 선택 완료 · 등록 앱에서 직원을 조회하세요.";
        }
        catch (Exception)
        {
            FaceDbStatus.Text = "관리자 목록 조회 실패 · DB 상태를 확인해 주세요.";
        }
    }

    private async void StartTcp(object sender, RoutedEventArgs e)
    {
        tcpManuallyStopped = false;
        await StartTcpAsync();
    }

    private async Task StartTcpAsync()
    {
        if (closing || monitor.Running || tcpBusy) return;
        if (!int.TryParse(TcpPort.Text, out int port))
        {
            MessageBox.Show("TCP 포트를 확인해 주세요.");
            return;
        }

        tcpBusy = true;
        TcpStart.IsEnabled = false;
        try
        {
            if (port is < 1024 or > 65533 || serverLink is null)
                throw new InvalidOperationException();
            remoteApi = new RemoteApiServer(HandleFaceRequestAsync, media.ServeAgentAsync);
            remoteApi.Start(port + 2);
            int videoPort = VideoProtocol.VideoPort(port);
            videoServer.Start(videoPort);
            monitor.Start(port);
            TcpStart.IsEnabled = false;
            TcpStop.IsEnabled = true;
            TcpPort.IsEnabled = false;
        }
        catch (Exception)
        {
            if (remoteApi is not null)
            {
                await remoteApi.DisposeAsync();
                remoteApi = null;
            }

            await videoServer.StopAsync();
            await monitor.StopAsync();
            if (!closing) TcpStatus.Text = "서버 시작 실패 · 포트 5050~5052 또는 설정한 세 포트의 점유 상태를 확인하세요.";
        }
        finally
        {
            tcpBusy = false;
            if (!closing) TcpStart.IsEnabled = !monitor.Running;
        }
    }

    private async void StopTcp(object sender, RoutedEventArgs e)
    {
        tcpManuallyStopped = true;
        if (tcpBusy) return;
        TcpStop.IsEnabled = false;
        if (remoteApi is not null)
        {
            await remoteApi.DisposeAsync();
            remoteApi = null;
        }

        await videoServer.StopAsync();
        await monitor.StopAsync();
        RefreshVideo();
        TcpStart.IsEnabled = TcpPort.IsEnabled = true;
        TcpStatus.Text = "수동 중지 · TCP 서버 시작을 눌러 다시 연결하세요.";
    }

    private static string StatusName(string status) =>
        status switch
        {
            "MATCH_CANDIDATE" => "등록 얼굴 일치 후보",
            "NO_MATCH" => "등록 얼굴 불일치",
            "PAUSED" => "비교 대기/중지",
            _ => "새 분석 결과 대기"
        };

    private void RefreshVideo()
    {
        if (preview && Environment.GetCommandLineArgs().Contains("--camera-placement-preview")) return;
        var frame = videoServer.Latest;
        if (
            frame is null
            || !VideoProtocol.IsFresh(
                frame.Packet,
                Stopwatch.GetElapsedTime(frame.At).TotalMilliseconds
            )
        )
        {
            ServerVideo.Source = null;
            displayedVideo = null;
            VideoStatus.Text = "CAM-01 · 영상 수신 대기 / 영상 없음";
            VideoHint.Visibility = Visibility.Visible;
            return;
        }

        if (ReferenceEquals(displayedVideo, frame))
            return;
        displayedVideo = frame;
        try
        {
            using var stream = new MemoryStream(frame.Packet.Jpeg!);
            // Inspect dimensions before pixel decoding; malformed/oversized JPEGs never reach the Image control.
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None
            );
            if (
                decoder.Frames.Count != 1
                || decoder.Frames[0].PixelWidth > 1280
                || decoder.Frames[0].PixelHeight > 1280
            )
                throw new InvalidDataException("영상 크기 초과");
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 640;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            ServerVideo.Source = bitmap;
            if (!string.IsNullOrWhiteSpace(frame.Packet.CameraDisplayName))
                cameraDisplayLabel = frame.Packet.CameraDisplayName;
            cameraSeen = true;
            VideoHint.Visibility = Visibility.Collapsed;
            VideoStatus.Text =
                $"CAM-01 · 실시간 영상 · #{frame.Packet.Sequence} · {frame.Packet.SentAt.ToLocalTime():HH:mm:ss} · 최대 5fps";
        }
        catch (Exception)
        {
            ServerVideo.Source = null;
            VideoHint.Visibility = Visibility.Visible;
            VideoStatus.Text = "영상 표시 실패 · 카메라 연결을 확인하세요";
        }
    }

    private void Receive(MonitorPacket packet)
    {
        if (closing)
            return;
        current = packet;
        receivedAt = Stopwatch.GetTimestamp();
        var value = packet.Snapshot!;
        TcpStatus.Text =
            $"TLS 연결 · :{monitor.BoundPort} · 영상/클립 {(media.Connected ? "연결됨" : "대기")}";
        TcpClientStatus.Text =
            $"{packet.ClientCode} / {packet.CameraCode} · 수신 #{packet.Sequence} · {DateTime.Now:HH:mm:ss}";
        if (packet.Decision is { Recorded: true, EventId: { } eventId, EventTime: { } at } decision)
            InsertHistory(
                new AccessEventRow(
                    at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    packet.CameraCode,
                    decision.EmployeeNo,
                    decision.PersonName,
                    decision.Result,
                    decision.Reason,
                    decision.RecordedDistance?.ToString("0.000") ?? "—",
                    eventId
                )
            );
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        if (current?.Snapshot is not { } value)
        {
            LiveResult.Text = "인증 앱 연결 대기";
            LiveSummary.Text = "최근 비교 결과 없음";
            DoorResult.Text = "출입 판정 대기";
            DoorResult.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        double elapsed = Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
        if (elapsed > MonitorProtocol.Timeout.TotalMilliseconds)
        {
            LiveResult.Text = "인증 앱 응답 없음";
            LiveSummary.Text = "이전 비교 결과 숨김 · 재접속 대기";
            DoorResult.Text = "출입 판정 대기 · 응답 없음";
            DoorResult.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        bool fresh = AccessPolicy.IsCurrent(value, DateTimeOffset.UtcNow);
        var decision = current.Decision;
        DoorResult.Text =
            fresh && decision is not null
                ? (
                    decision.Result switch
                    {
                        "GRANTED" => "출입 허가 (GRANTED)",
                        "DENIED" => "출입 거부 (DENIED)",
                        "ERROR" => "출입 판정 보류",
                        _ => "출입 판정 대기"
                    }
                )
                    + " · "
                    + AccessPolicy.Explain(decision.Reason)
                : "출입 판정 대기";
        DoorResult.Foreground =
            fresh && decision?.Result == "GRANTED" ? System.Windows.Media.Brushes.DarkGreen
            : fresh && decision?.Result is "DENIED" or "ERROR"
                ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DimGray;
        LiveResult.Text = fresh ? StatusName(value.Status) : "새 분석 결과 대기";
        LiveSummary.Text =
            $"임시 기준: {value.ReferenceLabel}\n기준 {value.ReferenceCount}개 · {value.Pose}\n상태: {(fresh ? value.Reason : "ANALYSIS_WAITING")}\n"
            + (
                fresh && value.Distance is { } d
                    ? $"거리 {d:0.000} / 기준 {value.Threshold:0.000}\n"
                    : ""
            )
            + $"분석 프레임 나이 {(value.AnalysisAgeMs + Math.Max(0, (DateTimeOffset.UtcNow - value.SentAt).TotalMilliseconds)) / 1000:0.00}초 · 수신 #{current.Sequence}";
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing)
            return;
        e.Cancel = true;
        closing = true;
        monitorClock.Stop();
        videoClock.Stop();
        lifetime.Cancel();
        settingsWindow?.Close();
        historyWindow?.Close();
        playbackWindow?.Close();
        employeesWindow?.Close();
        if (remoteApi is not null)
        {
            await remoteApi.DisposeAsync();
            remoteApi = null;
        }

        await media.DisposeAsync();
        await videoServer.DisposeAsync();
        ServerVideo.Source = null;
        await monitor.DisposeAsync();
        if (faceRequests is not null)
        {
            try
            {
                await faceRequests;
            }
            catch (Exception) { }
        }

        serverLink?.Certificate.Dispose();
        enrollmentProcess?.Dispose();
        connectedPassword = "";
        connectedEndpoint = null;
        Volatile.Write(ref faceDatabase, null);
        lifetime.Dispose();
        // DisposeAsync가 즉시 끝나도 Closing 이벤트가 반환된 뒤 창을 닫아 재진입을 피합니다.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void ShowEmployees(object sender, RoutedEventArgs e)
    {
        var session = Volatile.Read(ref faceDatabase);
        if (session is null || connectedEndpoint is null)
        {
            MessageBox.Show("서버 설정에서 DB를 연결해 주세요.");
            ShowServerSettings(sender, e);
            return;
        }

        if (employeesWindow is not null)
        {
            if (employeesWindow.WindowState == WindowState.Minimized)
                employeesWindow.WindowState = WindowState.Normal;
            employeesWindow.Activate();
            return;
        }

        employeesWindow = new EmployeesWindow(
            connectedEndpoint,
            connectedPassword,
            () => !closing && ReferenceEquals(session, Volatile.Read(ref faceDatabase))
        )
        {
            Owner = this
        };
        employeesWindow.Closed += (_, _) => employeesWindow = null;
        employeesWindow.Show();
    }

    private void ShowRecordings(object sender, RoutedEventArgs e) =>
        new RecordingsWindow(() => Volatile.Read(ref faceDatabase)) { Owner = this }.Show();

    private void ClearDatabase(object sender, RoutedEventArgs e)
    {
        employeesWindow?.Close();
        connectedEndpoint = null;
        connectedPassword = "";
        rows.Clear();
        HistoryStatus.Text = "DB 조회 전";
        current = null;
        RefreshSnapshot();
        Volatile.Write(ref faceDatabase, null);
        FaceAdmins.ItemsSource = null;
        FaceDbStatus.Text = "직원·얼굴 서비스 미연결";
        request?.Cancel();
        PasswordInput.Clear();
        Clients.ItemsSource = null;
        Cameras.ItemsSource = null;
        DeviceCount.Text = "조회 전";
        DbStatus.Text = "PostgreSQL · 표시 초기화";
    }

    private void ForgetStartupSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            startupSettings.Forget();
            DbStatus.Text = "저장된 자동 DB 연결 설정 삭제 · 현재 연결은 유지됩니다.";
        }
        catch (Exception)
        {
            DbStatus.Text = "자동 연결 설정 삭제 실패 · 파일 접근 권한을 확인하세요.";
        }
    }


}
