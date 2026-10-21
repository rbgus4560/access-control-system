// 파일 역할: 관리 서버의 DB 연결·TCP 수신·관제 화면을 조정합니다. 클라이언트 관측을 DB 권한 판정과 연결합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

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
    private bool eventPlaybackBusy;
    private readonly MediaCoordinator media;
    private RemoteApiServer? remoteApi;
    private ServerLink? serverLink;
    private ServerPlaybackWindow? playbackWindow;
    private Process? enrollmentProcess;
    private ServerToolWindow? settingsWindow,
        historyWindow;

    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => NavLive.IsChecked = true;
        AuxiliaryViews.Children.Clear();
        Events.ItemsSource = rows;
        rows.CollectionChanged += (_, _) => RefreshRecentPreview();
        RefreshRecentPreview();
        media = new MediaCoordinator(() => Volatile.Read(ref faceDatabase));
        try
        {
            serverLink = PairingStore.Server();
            LinkSecurity.Server = serverLink;
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
            RefreshVideo();
            RefreshDashboard();
        };
        monitorClock.Start();
        Loaded += (_, _) =>
        {
            faceRequests = FaceApiServer.ListenAsync(HandleFaceRequestAsync, lifetime.Token);
            _ = ObserveFaceRequestsAsync();
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

    private void InsertHistory(AccessEventRow row)
    {
        if (rows.Any(r => r.이벤트ID == row.이벤트ID))
            return;
        int index = 0;
        while (index < rows.Count && string.CompareOrdinal(rows[index].시각, row.시각) > 0)
            index++;
        rows.Insert(index, row);
        while (rows.Count > 500)
            rows.RemoveAt(rows.Count - 1);
        HistoryStatus.Text = $"DB 출입 기록 {rows.Count}건 표시";
    }

    private async void RefreshHistory(object sender, RoutedEventArgs e)
    {
        var db = Volatile.Read(ref faceDatabase);
        if (db is null)
        {
            HistoryStatus.Text = "서버 DB 연결을 먼저 해 주세요.";
            return;
        }

        if (!HistoryRefresh.IsEnabled)
            return;
        HistoryRefresh.IsEnabled = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var history = await db.EventsAsync(cts.Token);
            if (closing || !ReferenceEquals(db, Volatile.Read(ref faceDatabase)))
                return;
            rows.Clear();
            foreach (var row in history)
                rows.Add(row);
            HistoryStatus.Text = $"DB 최근 {rows.Count}건 조회";
        }
        catch (Exception)
        {
            if (!closing)
                HistoryStatus.Text = "출입 기록 조회 실패 · DB 연결을 확인하세요.";
        }
        finally
        {
            if (!closing)
                HistoryRefresh.IsEnabled = true;
        }
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
        if (!int.TryParse(TcpPort.Text, out int port))
        {
            MessageBox.Show("TCP 포트를 확인해 주세요.");
            return;
        }

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
            MessageBox.Show(
                "서버 시작 실패 · 1024~65533 기본 포트와 다음 두 포트가 비어 있어야 합니다. 기본: 5050 / 5051 / 5052"
            );
        }
    }

    private async void StopTcp(object sender, RoutedEventArgs e)
    {
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
        Volatile.Write(ref faceDatabase, null);
        lifetime.Dispose();
        Close();
    }

    private async void LoadDatabase(object sender, RoutedEventArgs e)
    {
        if (request != null)
            return;
        if (
            !int.TryParse(PortInput.Text, out int port)
            || port < 1
            || port > 65535
            || string.IsNullOrWhiteSpace(HostInput.Text)
            || string.IsNullOrWhiteSpace(DatabaseInput.Text)
            || string.IsNullOrWhiteSpace(UsernameInput.Text)
        )
        {
            MessageBox.Show("호스트·포트·DB 이름·계정을 확인해 주세요.");
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        request = cts;
        LoadButton.IsEnabled = false;
        ConnectionFields.IsEnabled = false;
        Clients.ItemsSource = null;
        Cameras.ItemsSource = null;
        DeviceCount.Text = "조회 중";
        DbStatus.Text = "PostgreSQL · 연결 및 조회 중";
        rows.Clear();
        HistoryStatus.Text = "DB 조회 중";
        current = null;
        RefreshSnapshot();
        Volatile.Write(ref faceDatabase, null);
        FaceAdmins.ItemsSource = null;
        FaceDbStatus.Text = "직원·얼굴 서비스 준비 중";
        try
        {
            var snapshot = await DatabaseService.LoadAsync(
                HostInput.Text.Trim(),
                port,
                DatabaseInput.Text.Trim(),
                UsernameInput.Text.Trim(),
                PasswordInput.Password,
                cts.Token
            );
            var db = new FaceDatabase(
                new DatabaseEndpoint(
                    HostInput.Text.Trim(),
                    port,
                    DatabaseInput.Text.Trim(),
                    UsernameInput.Text.Trim()
                ),
                PasswordInput.Password
            );
            var admins = await db.AdminsAsync(cts.Token);
            var history = await db.EventsAsync(cts.Token);
            if (closing || cts.IsCancellationRequested)
                return;
            Volatile.Write(ref faceDatabase, db);
            rows.Clear();
            foreach (var row in history)
                rows.Add(row);
            HistoryStatus.Text = $"DB 최근 {rows.Count}건 조회";
            FaceAdmins.ItemsSource = admins;
            FaceAdmins.SelectedIndex = admins.Count > 0 ? 0 : -1;
            FaceDbStatus.Text =
                admins.Count == 0
                    ? "얼굴 저장 전 관리자 추가를 한 번 진행해 주세요."
                    : "직원·얼굴 요청 대기 중 · DB 연결 정보는 이 서버의 메모리에만 보관";
            Clients.ItemsSource = snapshot.Clients;
            Cameras.ItemsSource = snapshot.Cameras;
            DeviceCount.Text =
                $"클라이언트 {snapshot.Clients.Count}개 / 카메라 {snapshot.Cameras.Count}개";
            DbStatus.Text = $"조회 성공 · {snapshot.Database} · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            if (!closing)
            {
                DbStatus.Text = "조회 취소 또는 시간 초과 · 다시 시도하세요";
                DeviceCount.Text = "조회 결과 없음";
            }
        }
        catch (PostgresException ex)
        {
            if (!closing)
            {
                string hint = ex.SqlState switch
                {
                    "28P01" => "DB 비밀번호 또는 계정을 확인하세요.",
                    "3D000" => "DB 이름을 확인하세요.",
                    "42P01" or "3F000" =>
                        "이 DB에 access_control 스키마와 테이블이 있는지 확인하세요.",
                    "42501" => "계정의 스키마·테이블 조회 권한을 확인하세요.",
                    _ => "DB 상태와 테이블 설정을 확인하세요."
                };
                DbStatus.Text = $"DB 조회 실패 [{ex.SqlState}] · {hint}";
                DeviceCount.Text = "조회 결과 없음";
            }
        }
        catch (Exception)
        {
            if (!closing)
            {
                DbStatus.Text =
                    "DB 연결·조회 실패 · PostgreSQL 실행 상태와 접속 정보를 확인하세요.";
                DeviceCount.Text = "조회 결과 없음";
            }
        }
        finally
        {
            request = null;
            if (!closing)
            {
                LoadButton.IsEnabled = true;
                ConnectionFields.IsEnabled = true;
                PasswordInput.Clear();
            }
        }
    }

    private EmployeesWindow? employeesWindow;

    private async Task<FaceReply> HandleFaceRequestAsync(
        FaceRequest request,
        CancellationToken token
    )
    {
        var db =
            Volatile.Read(ref faceDatabase)
            ?? throw new InvalidOperationException(
                "서버에서 DB 연결 및 목록 조회를 먼저 해 주세요."
            );
        if (request.Operation != "open-employees")
            return await db.HandleAsync(request, token);
        token.ThrowIfCancellationRequested();
        await Dispatcher.InvokeAsync(() =>
        {
            if (closing)
                throw new InvalidOperationException("서버 종료 중입니다.");
            ShowEmployees(this, new RoutedEventArgs());
        });
        return new FaceReply
        {
            RequestId = request.RequestId,
            Success = true,
            Message =
                "서버의 직원·권한 관리 창을 열었습니다. 해당 창에서 DB 비밀번호 입력 후 조회·수정하세요."
        };
    }

    private void ShowEmployees(object sender, RoutedEventArgs e)
    {
        if (
            !int.TryParse(PortInput.Text, out int port)
            || port < 1
            || port > 65535
            || string.IsNullOrWhiteSpace(HostInput.Text)
            || string.IsNullOrWhiteSpace(DatabaseInput.Text)
            || string.IsNullOrWhiteSpace(UsernameInput.Text)
        )
        {
            MessageBox.Show("DB 접속 정보를 확인해 주세요.");
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
            new DatabaseEndpoint(
                HostInput.Text.Trim(),
                port,
                DatabaseInput.Text.Trim(),
                UsernameInput.Text.Trim()
            )
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
            "DB 최근 500건 · 기록을 선택해 상세 정보 또는 해당 시각의 영상을 여세요.",
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
    }
}
