// 파일 역할: 인증 클라이언트의 화면과 통신 상태를 연결합니다. 식별된 직원 정보는 서버에 전달하고 최종 허가·거부 응답을 표시합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using AccessControl.Ai;
using AccessControl.Network;
using AccessControl.Faces;

namespace FaceRecognition;

public partial class MainWindow : Window
{
    private readonly FaceAiPanel panel = new("recognition");
    private readonly MonitorClient monitor = new();
    private readonly VideoClient videoClient = new();
    private readonly VideoSource videoSource = new();
    private readonly RecordingSync recordingSync = new();
    private readonly MediaAgent mediaAgent = new();
    private string mediaState = "영상 전송 대기";
    private string recordingState = "녹화 대기 · 카메라 연결 후 자동 시작",
        recordingDbState = "";
    private readonly ObservationSource observations = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closing;
    private readonly GallerySynchronizer gallerySync;
    private FaceGallerySnapshot? gallery;
    private bool galleryReady;
    private long decisionReceivedAt;
    private AccessDecision? lastDecision;
    private SettingsWindow? settingsWindow;
    private long latestCameraAt,
        framesInPeriod,
        fpsSince = Stopwatch.GetTimestamp();
    private readonly ObservableCollection<RecentEntry> recent = [];
    private readonly HashSet<Guid> recentIds = [];

    private sealed record RecentEntry(
        Guid Id,
        DateTimeOffset Time,
        string Name,
        string Result,
        string Reason,
        string EmployeeNo,
        string Distance
    )
    {
        public string Display =>
            $"{Time.ToLocalTime():HH:mm:ss} · {Name}\n{(Result == "GRANTED" ? "출입 허가" : Result == "DENIED" ? "출입 거부" : "판정 보류")} · {AccessPolicy.Explain(Reason)}";
    }

    public MainWindow()
    {
        gallerySync = new GallerySynchronizer(() => monitor.Running);
        InitializeComponent();
        Activated += (_, _) => NavLive.IsChecked = true;
        AuxiliaryViews.Children.Clear();
        AiHost.Content = panel;
        RecentEvents.ItemsSource = recent;
        // The zone must never fall back to a same-PC database pipe.
        FaceApi.RemoteCaller = RemoteFaceClient.CallAsync;
        try
        {
            LinkSecurity.Client = PairingStore.LoadClient();
        }
        catch (Exception)
        {
            TcpStatus.Text = "저장된 연결 코드를 다시 적용하세요.";
        }

        if (LinkSecurity.Client is { } stored)
        {
            TcpHost.Text = stored.Host;
            TcpPort.Text = stored.Port.ToString();
        }

        mediaAgent.StatusChanged += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!closing)
                    {
                        mediaState = text;
                        RefreshRecordingStatus();
                    }
                })
            );
        mediaAgent.Start();
        panel.LiveFrameUpdated += frame =>
        {
            if (frame is null)
            {
                latestCameraAt = 0;
                videoSource.Clear();
                recordingState = "녹화 중지 · 구간 파일 및 DB 확인";
                RefreshRecordingStatus();
            }
            else
            {
                latestCameraAt = frame.ReceivedAt;
                framesInPeriod++;
                videoSource.Publish(frame.Jpeg, frame.ReceivedAt);
            }
        };
        panel.RecordingStatusChanged += text =>
        {
            recordingState = text;
            RefreshRecordingStatus();
        };
        recordingSync.StatusChanged += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (closing)
                        return;
                    recordingDbState = text;
                    RefreshRecordingStatus();
                })
            );
        recordingSync.RetentionStatusChanged += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!closing)
                        RetentionStatus.Text = text;
                })
            );
        recordingSync.Start();
        gallerySync.Invalidated += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (closing)
                        return;
                    galleryReady = false;
                    lastDecision = null;
                    observations.Update(new AiState(), false, 0);
                    GalleryStatus.Text = text;
                    ReferenceSummary.Text = text;
                    RefreshAccessDisplay();
                })
            );
        gallerySync.StatusChanged += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!closing)
                        GalleryStatus.Text = text;
                })
            );
        gallerySync.Synchronized += value =>
            Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    if (closing)
                        return;
                    gallery = value;
                    galleryReady = true;
                    lastDecision = null;
                    observations.Update(new AiState(), false, 0);
                    await panel.ImportGalleryAsync(value);
                })
            );
        gallerySync.Start();
        panel.StateUpdated += (state, fresh) =>
        {
            bool ready =
                galleryReady
                && gallery is not null
                && state.GalleryMode
                && state.GalleryId == gallery.Id
                && state.GalleryCount == gallery.People.Count;
            var person =
                ready && Guid.TryParse(state.Identification?.PersonId, out var id)
                    ? gallery!.People.FirstOrDefault(p => p.PersonId == id)
                    : null;
            ReferenceIdentity? identity = person is null
                ? null
                : new(person.PersonId, person.EnrollmentId, person.Lease);
            if (ready && fresh && state.GalleryCount == 0)
                ReferenceSummary.Text =
                    "등록된 얼굴 기준이 없습니다. 서버에서 직원 얼굴을 등록해 주세요.";
            else if (ready && fresh)
                ReferenceSummary.Text =
                    $"등록 직원 {state.GalleryCount}명 자동 비교 · "
                    + (
                        state.Identification?.Result switch
                        {
                            "MATCH" when person is not null =>
                                $"{person.EmployeeNo} · {person.Name}",
                            "NO_MATCH" => "미등록 얼굴",
                            "AMBIGUOUS" => "식별 보류 · 직원 간 점수 유사",
                            _ => state.Matching
                                ? "얼굴 검출 대기"
                                : "자동 보정 대기 또는 수동 중지"
                        }
                    );
            else if (!galleryReady)
                ReferenceSummary.Text = "서버 연결 후 등록 직원 전체를 자동 동기화합니다.";
            else
                ReferenceSummary.Text =
                    $"등록 직원 {gallery?.People.Count ?? 0}명 · AI 적용/분석 대기";
            observations.Update(
                state,
                fresh && ready && (state.Identification is null || person is not null),
                panel.AnalysisAgeMs,
                identity
            );
            RefreshAccessDisplay();
        };
        monitor.StatusChanged += text =>
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (closing)
                        return;
                    TcpStatus.Text = text;
                    if (!text.Contains("수신 확인"))
                    {
                        lastDecision = null;
                        RefreshAccessDisplay();
                    }
                })
            );
        monitor.Acknowledged += ack =>
        {
            long at = Stopwatch.GetTimestamp();
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (closing)
                        return;
                    if (galleryReady && ack.Decision?.Reason == "DB_REFERENCE_REQUIRED")
                    {
                        galleryReady = false;
                        observations.Update(new AiState(), false, 0);
                        gallerySync.Reset();
                    }

                    lastDecision = ack.Decision;
                    decisionReceivedAt = at;
                    RememberDecision(ack.Decision);
                    RefreshAccessDisplay();
                })
            );
        };
        clock.Tick += (_, _) =>
        {
            Clock.Text = DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
            RefreshChrome();
            RefreshAccessDisplay();
        };
        clock.Start();
        Clock.Text = DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
    }

    private void RememberDecision(AccessDecision? decision)
    {
        if (
            decision is not { Recorded: true, EventId: { } id, EventTime: { } time }
            || id == Guid.Empty
            || !recentIds.Add(id)
        )
            return;
        recent.Insert(
            0,
            new RecentEntry(
                id,
                time.ToUniversalTime(),
                string.IsNullOrWhiteSpace(decision.PersonName) ? "미확인" : decision.PersonName,
                decision.Result,
                decision.Reason,
                decision.EmployeeNo ?? "—",
                decision.RecordedDistance?.ToString("0.000") ?? "—"
            )
        );
        if (recent.Count > 10)
        {
            recentIds.Remove(recent[^1].Id);
            recent.RemoveAt(recent.Count - 1);
        }
    }

    private void RefreshChrome()
    {
        bool cameraFresh =
            latestCameraAt != 0 && Stopwatch.GetElapsedTime(latestCameraAt).TotalSeconds < 3;
        CameraConnection.Text =
            cameraFresh ? "카메라 · 연결됨"
            : panel.IsRunning ? "카메라 · 준비/수신 대기"
            : "카메라 · 미연결";
        CameraConnection.Foreground = cameraFresh ? Brushes.DarkGreen : Brushes.SlateGray;
        bool serverFresh =
            monitor.Running
            && decisionReceivedAt != 0
            && Stopwatch.GetElapsedTime(decisionReceivedAt).TotalSeconds < 3;
        ServerConnection.Text =
            serverFresh ? "서버 · 연결됨"
            : monitor.Running ? "서버 · 연결 대기"
            : "서버 · 미연결";
        ServerConnection.Foreground = serverFresh ? Brushes.DarkGreen : Brushes.SlateGray;
        double elapsed = Stopwatch.GetElapsedTime(fpsSince).TotalSeconds;
        LiveIndicator.Text = cameraFresh
            ? $"LIVE · 수신 {framesInPeriod / Math.Max(.001, elapsed):0.0} FPS"
            : "카메라 대기";
        framesInPeriod = 0;
        fpsSince = Stopwatch.GetTimestamp();
    }

    private static void BringForward(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ShowSettings(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is not null)
        {
            BringForward(settingsWindow);
            return;
        }

        settingsWindow = new SettingsWindow(
            ConnectionControls,
            panel.OperationsView,
            RecordingControls
        )
        {
            Owner = this
        };
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    private void SelectRecentEvent(object sender, SelectionChangedEventArgs e)
    {
        if (closing || e.AddedItems.Count == 0 || RecentEvents.SelectedItem is not RecentEntry item)
            return;
        new AccessControl.Ui.EventDetailWindow(
            item.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            item.Name,
            item.EmployeeNo,
            item.Result,
            item.Reason,
            item.Distance,
            item.Id,
            "서버가 DB 기록 완료로 응답한 최근 인증입니다. 해당 시각의 영상·클립은 관리 서버에서 조회하세요."
        )
        {
            Owner = this
        }.Show();
        RecentEvents.SelectedIndex = -1;
    }

    // 현재 관측의 인증 시도 ID와 서버 응답이 일치하고 응답이 최신일 때만 판정을 표시합니다.
    private void RefreshAccessDisplay()
    {
        var snapshot = observations.Get();
        var decision = lastDecision;
        if (snapshot.Reason == "AMBIGUOUS_MATCH" && snapshot.Fresh && galleryReady)
        {
            AccessStatus.Text =
                "출입 판정 보류 · 두 직원의 얼굴 점수가 비슷합니다. 정면을 바라봐 주세요.";
            AccessStatus.Foreground = Brushes.DarkOrange;
            return;
        }

        if (
            decision is null
            || snapshot.AttemptId is null
            || snapshot.AttemptId != decision.AttemptId
            || !snapshot.Fresh
            || snapshot.AnalysisAgeMs > MonitorProtocol.MaxAnalysisAgeMs
            || !monitor.Running
            || Stopwatch.GetElapsedTime(decisionReceivedAt).TotalSeconds > 3
            || !galleryReady
        )
        {
            AccessStatus.Text =
                "출입 판정 대기 · 등록 직원 자동 동기화·카메라·서버 연결을 확인하세요";
            AccessStatus.Foreground = Brushes.DimGray;
            return;
        }

        string result = decision.Result switch
        {
            "GRANTED" => "출입 허가 (GRANTED)",
            "DENIED" => "출입 거부 (DENIED)",
            "ERROR" => "출입 판정 보류",
            _ => "출입 판정 대기"
        };
        AccessStatus.Text =
            $"{result} · {AccessPolicy.Explain(decision.Reason)}"
            + (decision.Recorded ? " · DB 기록 완료" : "");
        AccessStatus.Foreground =
            decision.Result == "GRANTED" ? Brushes.DarkGreen
            : decision.Result is "DENIED" or "ERROR" ? Brushes.Firebrick
            : Brushes.DimGray;
    }

    private void RefreshRecordingStatus()
    {
        RecordingStatus.Text = recordingState + "\n" + recordingDbState + "\n" + mediaState;
        RecordingStatus.Foreground = recordingState.Contains("오류")
            ? Brushes.Firebrick
            : Brushes.DarkSlateGray;
    }

    private void OpenRecordingFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RecordingFiles.Root);
            Process.Start(new ProcessStartInfo(RecordingFiles.Root) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show("녹화 폴더를 열 수 없습니다.");
        }
    }

    private void ApplyPairing(object sender, RoutedEventArgs e)
    {
        if (monitor.Running)
        {
            MessageBox.Show("먼저 서버 연결을 종료하세요.");
            return;
        }

        try
        {
            var profile = PairingStore.Parse(PairingCode.Password);
            PairingStore.SaveClient(profile);
            LinkSecurity.Client = profile;
            mediaAgent.Reconnect();
            TcpHost.Text = profile.Host;
            TcpPort.Text = profile.Port.ToString();
            PairingCode.Clear();
            gallerySync.Reset();
            galleryReady = false;
            lastDecision = null;
            observations.Update(new AiState(), false, 0);
            RefreshAccessDisplay();
            TcpStatus.Text = "연결 코드 저장 완료 · 서버 연결 후 직원 기준 자동 동기화";
        }
        catch (Exception)
        {
            TcpStatus.Text = "연결 코드 적용 실패 · 서버에서 복사한 전체 코드를 확인하세요.";
        }
    }

    private void ConnectServer(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile =
                LinkSecurity.Client
                ?? throw new InvalidOperationException(
                    "서버에서 복사한 연결 코드를 먼저 적용하세요."
                );
            profile.Validate();
            monitor.Start(profile.Host, profile.Port, observations.Get);
            videoClient.Start(profile.Host, profile.Port + 1, videoSource);
            TcpConnect.IsEnabled = false;
            TcpDisconnect.IsEnabled = true;
            ApplyPair.IsEnabled = PairingCode.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "서버 연결");
        }
    }

    private async void DisconnectServer(object sender, RoutedEventArgs e)
    {
        gallerySync.Reset();
        galleryReady = false;
        observations.Update(new AiState(), false, 0);
        TcpDisconnect.IsEnabled = false;
        await videoClient.StopAsync();
        await monitor.StopAsync();
        lastDecision = null;
        TcpConnect.IsEnabled = ApplyPair.IsEnabled = PairingCode.IsEnabled = true;
        RefreshAccessDisplay();
    }

    private void ShowLive(object sender, RoutedEventArgs e) => RefreshAccessDisplay();

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing)
            return;
        e.Cancel = true;
        closing = true;
        clock.Stop();
        lifetime.Cancel();
        settingsWindow?.Close();
        await gallerySync.DisposeAsync();
        await mediaAgent.DisposeAsync();
        videoSource.Clear();
        await videoClient.DisposeAsync();
        await monitor.DisposeAsync();
        await panel.DisposeAsync();
        await recordingSync.DisposeAsync();
        lifetime.Dispose();
        Close();
    }
}
