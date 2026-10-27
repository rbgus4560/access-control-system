// 파일 역할: 등록·인증에서 재사용하는 카메라와 AI 표시 패널입니다. 화면 갱신과 기준 적용 중 이전 비교 결과를 숨깁니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

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

public sealed partial class FaceAiPanel : UserControl, IAsyncDisposable
{
    private readonly string role;
    private readonly AiClient client = new();
    private readonly ReferenceInbox inbox = new();
    private FaceGallerySnapshot? gallery;
    private bool galleryPending,
        galleryLoading;
    private long galleryAttemptAt;
    private CancellationTokenSource? galleryStop;
    private readonly TextBox python = new(),
        directory = new(),
        modelHome = new();
    private readonly ComboBox camera = new() { Width = 60 },
        backend = new() { Width = 90 };
    private readonly CheckBox yaw = new() { Content = "좌우 반전 보정" },
        pitch = new() { Content = "상하 반전 보정" };
    private readonly Image analysisView = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock analysisLabel = new()
    {
        Foreground = Brushes.White, Background = Brushes.Transparent,
        Margin = new Thickness(12), Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed, Text = "분석 화면 · 새 분석 결과 대기"
    };

    // 라이브 위에 과거 좌표를 그리지 않고 동일 시점의 주석 포함 분석 프레임을 표시합니다.
    public void SetAnalysisVisible(bool visible)
    {
        analysisVisible = visible;
        analysisLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        analysisView.Visibility = Visibility.Collapsed;
        analysisLabel.Text = "분석 화면 · 새 분석 결과 대기";
        if (visible) Refresh();
    }

    private readonly Image live = new() { Stretch = Stretch.Uniform },
        analyzed = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = Text("카메라·AI 연결을 눌러 주세요."),
        direction = Text("정면 보정 대기"),
        progress = Text("기준 얼굴 없음"),
        match = Text("얼굴 비교 대기"),
        timing = Text(""),
        analyzedCaption = Text("마지막 분석 프레임"),
        message = Text("");
    private readonly Button start,
        stop,
        calibrate,
        enroll,
        capture,
        compare,
        clear;
    private readonly Expander settings;
    private readonly ProgressBar enrollmentProgress = new()
    {
        Minimum = 0,
        Maximum = 100,
        Height = 12,
        Margin = new Thickness(3, 8, 3, 8)
    };
    private readonly TextBlock coverageHint = Text("정면을 바라본 상태에서 등록 시작을 누르세요.");
    private readonly DispatcherTimer timer;
    private ReceivedState? rendered;
    private int requiredCommand;
    private bool disposed,
        invalidImage,
        awaitingCommand,
        suspended,
        importFailed;
    public event Action<AiState, bool>? StateUpdated;
    public event Action<JsonElement>? ReferenceReceived;
    public event Action<ReceivedLive?>? LiveFrameUpdated;
    public event Action<string>? RecordingStatusChanged;
    public double AnalysisAgeMs { get; private set; } = double.PositiveInfinity;
    public long ReferenceRevision { get; private set; }
    public AiState? LastState => client.State?.State;
    public bool IsRunning => client.Running;
    public StackPanel OperationsView { get; } = new() { Margin = new Thickness(4) };
    public StackPanel EnrollmentControls { get; } = new() { Margin = new Thickness(0, 4, 0, 4) };

    private readonly Dictionary<string, TextBlock> stages = new();
    private readonly TextBlock livePose = Text("현재 감지 방향: 대기");
    private readonly EnrollmentMotionGuide motionGuide = new();
    private readonly CheckBox animateGuide = new() { Content = "움직이는 방향 안내", IsChecked = true, Margin = new Thickness(5, 8, 5, 8) };

    public FaceAiPanel(string role)
    {
        this.role = role;
        foreach (var overlayText in new[] { videoClock, cameraCaption, decisionText, analysisLabel })
            overlayText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1.5, Opacity = 1
            };
        var saved = AiSettings.Load(role);
        cameraDisplayName.Text = string.IsNullOrWhiteSpace(saved.CameraDisplayName)
            ? "CAM-01 · 출입구" : saved.CameraDisplayName;
        displayCameraName = cameraDisplayName.Text;
        cameraCaption.Text = displayCameraName;
        cameraDisplayName.TextChanged += (_, _) =>
        {
            // 영상 송신 스레드는 WPF TextBox를 읽지 않고 문자열 스냅샷만 읽습니다.
            System.Threading.Volatile.Write(ref displayCameraName, DisplayCameraName());
            cameraCaption.Text = displayCameraName;
        };
        python.Text = saved.Python;
        directory.Text = saved.AiDirectory;
        modelHome.Text = saved.ModelHome;
        foreach (int number in Enumerable.Range(0, 4))
            camera.Items.Add(number);
        camera.SelectedItem = saved.Camera;
        backend.ItemsSource = new[] { "dshow", "msmf", "any" };
        backend.SelectedItem = saved.Backend;
        yaw.IsChecked = saved.ReverseYaw;
        pitch.IsChecked = saved.ReversePitch;
        var root = new DockPanel();
        Content = root;
        var setup = new StackPanel();
        if (role == "recognition")
        {
            var nameRow = PathRow("표시 이름", cameraDisplayName, false);
            setup.Children.Add(nameRow);
            setup.Children.Add(Button("표시 이름 저장", () =>
            {
                // 화면용 이름만 저장해 실행 중인 장치 번호와 통신 식별자는 변경하지 않습니다.
                var current = AiSettings.Load(role);
                current.CameraDisplayName = DisplayCameraName();
                current.Save(role);
                cameraDisplayName.Text = current.CameraDisplayName;
                return Task.CompletedTask;
            }));
        }
        setup.Children.Add(PathRow("Python", python, true));
        setup.Children.Add(PathRow("AI 폴더", directory, false));
        setup.Children.Add(PathRow("모델 폴더", modelHome, false));
        var options = new WrapPanel { Margin = new Thickness(4) };
        options.Children.Add(Text("카메라 번호 "));
        options.Children.Add(camera);
        options.Children.Add(Text("  드라이버 "));
        options.Children.Add(backend);
        yaw.Margin = pitch.Margin = new Thickness(12, 4, 4, 4);
        options.Children.Add(yaw);
        options.Children.Add(pitch);
        setup.Children.Add(options);
        settings = new Expander
        {
            Header = "카메라 및 AI 실행 설정",
            IsExpanded = true,
            Content = setup,
            Margin = new Thickness(4)
        };
        DockPanel.SetDock(settings, Dock.Top);
        root.Children.Add(settings);
        var buttons = new WrapPanel { Margin = new Thickness(4) };
        start = Button("카메라·AI 연결", StartAsync);
        stop = Button("연결 종료", StopAsync);
        calibrate = Button(
            role == "recognition" ? "정면 재보정" : "정면 보정",
            () => SendAsync("0")
        );
        enroll = Button("등록 시작", () => SendAsync("e"));
        capture = Button("등록 취소", () => SendAsync("x"));
        compare = Button("얼굴 비교 켜기/끄기", () => SendAsync("t"));
        clear = Button("기준 초기화", ClearAsync);
        if (role == "recognition")
            clear.Visibility = Visibility.Collapsed;
        foreach (var button in new[] { start, stop, calibrate })
            buttons.Children.Add(button);
        if (role == "enrollment")
        {
            buttons.Children.Add(enroll);
            buttons.Children.Add(capture);
        }
        else
            buttons.Children.Add(compare);
        buttons.Children.Add(clear);
        buttons.Children.Add(
            Button(
                "진단 보기",
                () =>
                {
                    MessageBox.Show(
                        client.DiagnosticText.Length == 0
                            ? "진단 메시지가 없습니다."
                            : client.DiagnosticText,
                        "AI 진단"
                    );
                    return Task.CompletedTask;
                }
            )
        );
        DockPanel.SetDock(buttons, Dock.Top);
        root.Children.Add(buttons);
        if (role == "enrollment")
        {
            var guide = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(enrollmentProgress, Dock.Bottom);
            guide.Children.Add(enrollmentProgress);
            coverageHint.VerticalAlignment = VerticalAlignment.Center;
            coverageHint.FontSize = 15;
            guide.Children.Add(coverageHint);
            DockPanel.SetDock(guide, Dock.Top);
            root.Children.Add(guide);
        }

        var footer = new StackPanel { Margin = new Thickness(8) };
        match.FontSize = 17;
        match.FontWeight = FontWeights.Bold;
        foreach (var text in new[] { status, direction, progress, match })
            footer.Children.Add(text);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var diagnosticGrid = new Grid();
        diagnosticGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        diagnosticGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var preview = ImagePanel(null, analyzed);
        preview.Height = 155;
        diagnosticGrid.Children.Add(preview);
        var diagnosticText = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(diagnosticText, 1);
        foreach (var text in new[] { analyzedCaption, timing, message })
            diagnosticText.Children.Add(text);
        diagnosticGrid.Children.Add(diagnosticText);
        var diagnostic = new Expander
        {
            Header = "분석 상세 · 마지막 분석 프레임",
            Content = diagnosticGrid,
            IsExpanded = false,
            Margin = new Thickness(8, 2, 8, 4)
        };
        DockPanel.SetDock(diagnostic, Dock.Bottom);
        root.Children.Add(diagnostic);
        var main = ImagePanel(null, live);
        foreach (var image in new[] { live, analyzed, analysisView })
        {
            // 모든 카메라 표시를 거울처럼 통일합니다. 분석·전송·녹화 프레임은 원본입니다.
            image.RenderTransformOrigin = new Point(.5, .5);
            image.RenderTransform = new ScaleTransform(-1, 1);
        }
        main.MinHeight = 160;
        root.Children.Add(main);
        if (role == "recognition")
        {
            var cameraViewport = new Grid();
            root.Children.Remove(main);
            // 인증 화면에서는 영상과 오버레이가 같은 뷰포트를 사용합니다.
            ((Panel)live.Parent).Children.Remove(live);
            cameraViewport.Children.Add(live);
            cameraViewport.Children.Add(analysisView);
            recognitionViewport = cameraViewport;
            videoOverlay.HorizontalAlignment = HorizontalAlignment.Center;
            videoOverlay.VerticalAlignment = VerticalAlignment.Center;
            decisionRibbon.Child = decisionText;
            analysisLabel.Margin = new Thickness(14, 58, 14, 14);
            videoOverlay.Children.Add(videoClock);
            videoOverlay.Children.Add(cameraCaption);
            videoOverlay.Children.Add(analysisLabel);
            videoOverlay.Children.Add(decisionRibbon);
            cameraViewport.Children.Add(videoOverlay);
            cameraViewport.SizeChanged += (_, _) => UpdateVideoOverlay();
            root.Children.Add(cameraViewport);
            foreach (
                UIElement element in root
                    .Children.Cast<UIElement>()
                    .Where(e => !ReferenceEquals(e, cameraViewport))
                    .ToArray()
            )
            {
                root.Children.Remove(element);
                // 분석 상세는 메인 화면의 분석 토글로 이동합니다. 등록 앱의 상세 패널은 유지합니다.
                if (!ReferenceEquals(element, diagnostic)) OperationsView.Children.Add(element);
            }
        }
        else
        {
            root.Children.Clear();
            buttons.Children.Clear();
            footer.Children.Clear();
            // Removing the outer guide does not release its children. Each reused control must have one parent.
            (coverageHint.Parent as Panel)?.Children.Remove(coverageHint);
            (enrollmentProgress.Parent as Panel)?.Children.Remove(enrollmentProgress);
            (message.Parent as Panel)?.Children.Remove(message);
            OperationsView.Children.Add(settings);
            var advanced = new WrapPanel();
            advanced.Children.Add(calibrate);
            advanced.Children.Add(clear);
            advanced.Children.Add(
                Button(
                    "진단 보기",
                    () =>
                    {
                        MessageBox.Show(client.DiagnosticText, "AI 진단");
                        return Task.CompletedTask;
                    }
                )
            );
            OperationsView.Children.Add(advanced);
            OperationsView.Children.Add(diagnostic);
            OperationsView.Children.Add(direction);
            OperationsView.Children.Add(match);
            // A Grid gives the image a finite, growing viewport. Controls no longer consume its height.
            var viewport = new Grid();
            viewport.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            viewport.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            );
            Grid.SetRow(main, 1);
            viewport.Children.Add(main);
            coverageHint.Foreground = Brushes.White;
            coverageHint.FontSize = 20;
            coverageHint.FontWeight = FontWeights.SemiBold;
            var guideText = new StackPanel();
            var caption = Text("LIVE CAMERA · 거울 미리보기 · 화면 기준 안내");
            caption.Foreground = Brushes.LightSteelBlue;
            caption.FontSize = 11;
            caption.Margin = new Thickness(3, 0, 3, 7);
            guideText.Children.Add(caption);
            guideText.Children.Add(coverageHint);
            var guideHeader = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 32, 51)),
                Padding = new Thickness(18, 14, 18, 10),
                Child = new DockPanel()
            };
            var animatedHeader = (DockPanel)guideHeader.Child;
            DockPanel.SetDock(motionGuide, Dock.Left);
            animatedHeader.Children.Add(motionGuide);
            animatedHeader.Children.Add(guideText);
            animateGuide.Unchecked += (_, _) => motionGuide.ShowStage(null);
            EnrollmentControls.Children.Add(animateGuide);
            viewport.Children.Add(guideHeader);
            root.Children.Add(viewport);
            foreach (var button in new[] { start, stop, enroll, capture })
            {
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.Height = 38;
                button.Margin = new Thickness(0, 4, 0, 4);
                ((Wpf.Ui.Controls.Button)button).Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            }
            start.Content = "카메라 연결";
            stop.Visibility = capture.Visibility = Visibility.Collapsed;
            var cameraButtons = new Grid();
            cameraButtons.Children.Add(start);
            cameraButtons.Children.Add(stop);
            EnrollmentControls.Children.Add(cameraButtons);
            var registrationButtons = new Grid();
            registrationButtons.Children.Add(enroll);
            registrationButtons.Children.Add(capture);
            EnrollmentControls.Children.Add(registrationButtons);
            EnrollmentControls.Children.Add(livePose);
            foreach (string key in new[] { "FRONT", "LEFT", "RIGHT", "UP", "DOWN" })
            {
                var row = Text("○  " + PoseName(MirrorPose(key)));
                row.Margin = new Thickness(5, 9, 5, 9);
                row.FontSize = 15;
                stages.Add(key, row);
                EnrollmentControls.Children.Add(row);
            }

            EnrollmentControls.Children.Add(enrollmentProgress);
            EnrollmentControls.Children.Add(progress);
            EnrollmentControls.Children.Add(status);
            EnrollmentControls.Children.Add(message);
        }

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        SetConnected(false);
        // 운영 입력을 만들지 않고 등록 안내의 배치만 확인하는 개발용 미리보기입니다.
        if (role == "enrollment" && Environment.GetCommandLineArgs().Contains("--ui-preview")
            && Environment.GetCommandLineArgs().Contains("--guide-preview"))
        {
            motionGuide.ShowStage("RIGHT");
            coverageHint.Text = "화면 오른쪽으로 천천히 돌려 주세요.";
        }
    }

    public string CameraDisplayName => System.Threading.Volatile.Read(ref displayCameraName);

    private string DisplayCameraName() => string.IsNullOrWhiteSpace(cameraDisplayName.Text)
        ? "CAM-01 · 출입구" : cameraDisplayName.Text.Trim();

    private AiSettings ReadSettings() =>
        new()
        {
            Python = Path.GetFullPath(python.Text.Trim()),
            AiDirectory = Path.GetFullPath(directory.Text.Trim()),
            ModelHome = Path.GetFullPath(modelHome.Text.Trim()),
            CameraDisplayName = DisplayCameraName(),
            Camera = camera.SelectedItem is int value ? value : 0,
            Backend = backend.SelectedItem as string ?? "dshow",
            ReverseYaw = yaw.IsChecked == true,
            ReversePitch = pitch.IsChecked == true
        };

    private Task StartAsync()
    {
        var value = ReadSettings();
        value.Save(role);
        client.Start(value, role);
        inbox.Requeue();
        galleryPending = gallery is not null;
        importFailed = false;
        rendered = null;
        requiredCommand = 0;
        invalidImage = false;
        settings.IsExpanded = false;
        SetConnected(true);
        suspended = false;
        awaitingCommand = false;
        status.Text = "카메라 연결 · AI 모델 준비 중…";
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        motionGuide.ShowStage(null);
        try
        {
            galleryStop?.Cancel();
        }
        catch (ObjectDisposedException) { }

        LiveFrameUpdated?.Invoke(null);
        if (role == "enrollment")
        {
            livePose.Text = "현재 감지 방향: 대기";
            coverageHint.Text = "카메라를 연결한 뒤 등록을 시작하세요.";
            foreach (var (key, row) in stages)
            {
                row.Text = "○  " + PoseName(MirrorPose(key));
                row.Foreground = Brushes.SlateGray;
            }

            enrollmentProgress.Value = 0;
        }

        AnalysisAgeMs = double.PositiveInfinity;
        suspended = true;
        awaitingCommand = false;
        SetConnected(false);
        start.IsEnabled = false;
        StateUpdated?.Invoke(new AiState(), false);
        match.Text = "비교 중지 · 연결 종료 중";
        ClearAccessDecision();
        live.Source = analyzed.Source = analysisView.Source = null;
        UpdateVideoOverlay();
        analysisView.Visibility = Visibility.Collapsed;
        analysisLabel.Text = "분석 화면 · 새 분석 결과 대기";
        await client.StopAsync();
        start.IsEnabled = true;
        rendered = null;
        status.Text = "연결 종료";
        direction.Text = "정면 보정 대기";
        inbox.Requeue();
        importFailed = false;
        progress.Text =
            gallery is not null ? $"직원 기준 {gallery.People.Count}명 · 카메라 재연결 후 자동 적용"
            : inbox.HasReference
                ? $"임시 기준 수신 {inbox.Count}/{inbox.Count} · 카메라 재연결 후 적용"
            : "기준 얼굴 없음";
        match.Text = "얼굴 비교 대기";
        timing.Text = message.Text = "";
        clear.IsEnabled = inbox.HasReference;
    }

    private void SetConnected(bool connected)
    {
        start.IsEnabled = !connected;
        stop.IsEnabled = connected;
        calibrate.IsEnabled = false;
        clear.IsEnabled = connected || inbox.HasReference;
        enroll.IsEnabled = capture.IsEnabled = compare.IsEnabled = false;
        settings.IsEnabled = !connected;
        if (role == "enrollment")
        {
            start.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            stop.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            enroll.Visibility = Visibility.Visible;
            capture.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SendAsync(string key)
    {
        // Hide prior identity results immediately, before the bridge acknowledgement.
        match.Text = "명령 처리 중 · 이전 비교 결과 숨김";
        awaitingCommand = true;
        try
        {
            await client.SendAsync(key);
        }
        catch
        {
            awaitingCommand = false;
            throw;
        }
    }

    private void SetImage(Image image, string? jpeg)
    {
        if (jpeg is null)
            return;
        try
        {
            var bytes = Convert.FromBase64String(jpeg);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
        }
        catch
        {
            if (!invalidImage)
            {
                invalidImage = true;
                message.Text = "영상 수신 오류 · 진단 확인 필요";
            }
        }
    }

    // 등록 화면의 방향 표시만 뒤집고 내부 키와 수집 순서는 유지합니다.
    private static string? MirrorPose(string? pose) => pose switch
    {
        "LEFT" => "RIGHT", "RIGHT" => "LEFT",
        "UP_LEFT" => "UP_RIGHT", "UP_RIGHT" => "UP_LEFT",
        "DOWN_LEFT" => "DOWN_RIGHT", "DOWN_RIGHT" => "DOWN_LEFT", _ => pose
    };

    public static string PoseName(string? pose) =>
        pose switch
        {
            "FRONT" => "정면",
            "UP" => "위",
            "DOWN" => "아래",
            "LEFT" => "왼쪽",
            "RIGHT" => "오른쪽",
            "UP_LEFT" => "왼쪽 위",
            "UP_RIGHT" => "오른쪽 위",
            "DOWN_LEFT" => "왼쪽 아래",
            "DOWN_RIGHT" => "오른쪽 아래",
            _ => "방향 확인 중"
        };

    // 최신 분석의 나이·명령 적용 순서·기준 버전을 함께 확인한 뒤 화면과 인증 관측을 갱신합니다.
    private async Task RecoverAsync(string error)
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            status.Text = error;
        }
    }

    private static string Explain(string value)
    {
        if (value.StartsWith("Gallery syncing"))
            return "등록 직원 전체 기준을 적용하고 있습니다. 적용 완료 전 인증을 보류합니다.";
        if (value.StartsWith("Gallery ready"))
            return "등록 직원 전체 기준 적용 완료 · 정면 자동 보정 후 비교합니다.";
        if (value.StartsWith("Auto comparison ON"))
            return "자동 얼굴 비교 중 · 출입 허가·거부는 서버에서 판정합니다.";
        if (value.StartsWith("Auto FRONT calibrated"))
            return "정면 자동 보정 완료 · 얼굴 기준이 준비되면 자동 비교를 시작합니다.";
        if (value.StartsWith("Auto start: load"))
            return "서버의 등록 직원 기준을 자동 동기화한 뒤 비교를 시작합니다.";
        if (value.StartsWith("Auto start:"))
            return "정면을 바라보고 잠시 멈춰 주세요. 정면 자동 보정 후 비교를 시작합니다.";
        if (value.StartsWith("FRONT calibrated"))
            return "정면 보정 완료 · 인증은 자동 비교를 준비합니다. 수동 중지 상태라면 비교 시작을 눌러 주세요.";
        if (value.StartsWith("Look FRONT") || value.StartsWith("First look FRONT"))
            return "정면을 보고 정면 보정 버튼을 눌러 주세요.";
        if (value.StartsWith("Guided samples ready"))
            return "대표 샘플 자동 선택 완료 · 직원을 확인하고 DB에 저장하세요.";
        if (value.StartsWith("Guided collecting"))
            return "움직이는 동안 선명도와 각도 차이를 확인해 샘플을 자동 선택하고 있습니다.";
        if (value.StartsWith("Nine directions ready"))
            return "9방향 자동 수집 완료 · 직원을 확인하고 DB에 저장하세요.";
        if (value.StartsWith("Enrollment cancelled"))
            return "등록 취소 · 이전 완성 기준은 유지합니다.";
        if (value.StartsWith("Auto saved"))
            return "자동 촬영 완료 · 초록색으로 채워지지 않은 방향으로 천천히 움직여 주세요.";
        if (value.StartsWith("Auto capture rejected"))
            return "정면 기준과 차이가 큽니다 · 같은 사람이 자세·조명을 조정해 주세요.";
        if (value.StartsWith("Auto capture waiting"))
            return "분석이 지연됐습니다 · 같은 방향에서 잠시 더 멈춰 주세요.";
        if (value.StartsWith("Auto enrollment:"))
            return "정면을 바라본 뒤 화면 안내에 따라 천천히 고개를 움직여 주세요.";
        if (value.StartsWith("Enrollment:"))
            return "정면부터 안내된 방향을 유지한 뒤 현재 방향 촬영을 눌러 주세요.";
        if (value.StartsWith("Saved "))
            return "촬영 완료 · 다음 방향을 확인해 주세요.";
        if (value.StartsWith("Five directions ready"))
            return "5방향 등록 완료 · 인증 앱으로 임시 기준을 전달할 수 있습니다.";
        if (value.StartsWith("Reference ready"))
            return "특징 준비 완료 · DB 저장 결과는 등록 앱 하단에서 확인하세요.";
        if (value.StartsWith("Reference imported"))
            return "얼굴 기준 수신 · 정면을 잠깐 바라보면 자동 보정 후 비교를 시작합니다.";
        if (value.StartsWith("Capture rejected"))
            return "촬영 조건 미충족 · 방향과 안정된 분석 횟수, 조명을 확인해 주세요.";
        if (value.StartsWith("All references cleared"))
            return "기준 얼굴 초기화 완료 · 정면부터 다시 보정해 주세요.";
        if (value.StartsWith("Comparison ON"))
            return "얼굴 특징 비교 중 · 출입 판정은 인증 앱 상단에서 확인하세요.";
        if (value == "Comparison OFF")
            return "얼굴 비교 중지";
        if (value.StartsWith("AI ERROR"))
            return "AI 처리 오류 · 진단 보기에서 내용을 확인해 주세요.";
        if (value.StartsWith("Command expired"))
            return "명령 대기 시간 초과 · 자세를 유지하고 다시 눌러 주세요.";
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        motionGuide.ShowStage(null);
        disposed = true;
        try
        {
            galleryStop?.Cancel();
        }
        catch (ObjectDisposedException) { }

        timer.Stop();
        inbox.Clear();
        await client.DisposeAsync();
    }
}
