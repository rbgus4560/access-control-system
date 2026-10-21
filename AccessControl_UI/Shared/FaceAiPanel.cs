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

public sealed class FaceAiPanel : UserControl, IAsyncDisposable
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

    public FaceAiPanel(string role)
    {
        this.role = role;
        var saved = AiSettings.Load(role);
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
        var main = ImagePanel(role == "enrollment" ? null : "LIVE CAMERA", live);
        main.MinHeight = 160;
        root.Children.Add(main);
        if (role == "recognition")
        {
            foreach (
                UIElement element in root
                    .Children.Cast<UIElement>()
                    .Where(e => !ReferenceEquals(e, main))
                    .ToArray()
            )
            {
                root.Children.Remove(element);
                OperationsView.Children.Add(element);
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
            var caption = Text("LIVE CAMERA");
            caption.Foreground = Brushes.LightSteelBlue;
            caption.FontSize = 11;
            caption.Margin = new Thickness(3, 0, 3, 7);
            guideText.Children.Add(caption);
            guideText.Children.Add(coverageHint);
            var guideHeader = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 32, 51)),
                Padding = new Thickness(18, 14, 18, 10),
                Child = guideText
            };
            viewport.Children.Add(guideHeader);
            root.Children.Add(viewport);
            var cameraButtons = new WrapPanel();
            cameraButtons.Children.Add(start);
            cameraButtons.Children.Add(stop);
            EnrollmentControls.Children.Add(cameraButtons);
            var registrationButtons = new WrapPanel();
            registrationButtons.Children.Add(enroll);
            registrationButtons.Children.Add(capture);
            EnrollmentControls.Children.Add(registrationButtons);
            EnrollmentControls.Children.Add(livePose);
            foreach (string key in new[] { "FRONT", "LEFT", "RIGHT", "UP", "DOWN" })
            {
                var row = Text("○  " + PoseName(key));
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

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        SetConnected(false);
    }

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

    private AiSettings ReadSettings() =>
        new()
        {
            Python = Path.GetFullPath(python.Text.Trim()),
            AiDirectory = Path.GetFullPath(directory.Text.Trim()),
            ModelHome = Path.GetFullPath(modelHome.Text.Trim()),
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
                row.Text = "○  " + PoseName(key);
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
        live.Source = analyzed.Source = null;
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

    public Task ExportAsync() => SendAsync("export");

    public async Task<JsonElement> ExportReferenceAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void Receive(JsonElement value) => completion.TrySetResult(value.Clone());
        ReferenceReceived += Receive;
        try
        {
            await SendAsync("export");
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            ReferenceReceived -= Receive;
        }
    }

    public Task ClearReferenceAsync() => ClearAsync();

    private Task ClearAsync()
    {
        ReferenceRevision++;
        inbox.Clear();
        importFailed = false;
        if (client.Running)
            return SendAsync("c");
        progress.Text = "기준 얼굴 없음";
        message.Text = "임시 기준 초기화 완료";
        match.Text = "얼굴 비교 대기";
        clear.IsEnabled = false;
        return Task.CompletedTask;
    }

    public async Task ImportReferenceAsync(JsonElement reference, string label)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(FaceAiPanel));
            if (role != "recognition")
                throw new InvalidOperationException("인증 앱에서만 수신 가능합니다.");
            inbox.Receive(reference, label);
            importFailed = false;
            ReferenceRevision++;
            compare.IsEnabled = false;
            clear.IsEnabled = true;
            match.Text = "얼굴 기준 수신 · 자동 보정·비교 대기";
            progress.Text = $"임시 기준 수신 {inbox.Count}/{inbox.Count} · " + label;
            message.Text = client.Running
                ? "AI 준비 후 등록 기준 적용 대기"
                : "카메라·AI를 연결한 뒤 정면을 잠깐 바라봐 주세요. 자동 보정 후 비교를 시작합니다.";
        });
    }

    private async Task LoadPendingReferenceAsync(ReferenceSnapshot value)
    {
        awaitingCommand = true;
        match.Text = "등록 기준 적용 중 · 이전 비교 결과 숨김";
        try
        {
            await client.SendAsync("load", value.Reference, value.Label);
        }
        catch (Exception ex)
        {
            importFailed = true;
            awaitingCommand = false;
            message.Text = "기준 적용 실패 · 카메라·AI를 다시 연결해 주세요. " + ex.Message;
        }
    }

    public Task ImportGalleryAsync(FaceGallerySnapshot value) =>
        Dispatcher
            .InvokeAsync(() =>
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(FaceAiPanel));
                if (role != "recognition")
                    throw new InvalidOperationException("인증 앱에서만 직원 기준을 적용합니다.");
                if (gallery?.Id == value.Id)
                    return;
                try
                {
                    galleryStop?.Cancel();
                }
                catch (ObjectDisposedException) { }

                gallery = value;
                galleryPending = true;
                importFailed = false;
                ReferenceRevision++;
                match.Text = $"직원 기준 {value.People.Count}명 수신 · AI 적용 대기";
            })
            .Task;

    // 시작·직원별 추가·완료 순서로 전체 기준을 전달합니다. 적용 중에는 이전 얼굴 비교 결과를 인증에 사용하지 않습니다.
    private async Task LoadGalleryAsync(FaceGallerySnapshot value)
    {
        galleryLoading = true;
        galleryPending = false;
        importFailed = false;
        galleryAttemptAt = Stopwatch.GetTimestamp();
        using var cancel = new CancellationTokenSource();
        galleryStop = cancel;
        try
        {
            await client.SendAppliedAsync(
                "gallery_begin",
                new { gallery_id = value.Id, count = value.People.Count },
                cancel.Token
            );
            foreach (var person in value.People)
                await client.SendAppliedAsync(
                    "gallery_add",
                    new { person_id = person.PersonId.ToString("D"), reference = person.Reference },
                    cancel.Token
                );
            await client.SendAppliedAsync(
                "gallery_commit",
                new { gallery_id = value.Id },
                cancel.Token
            );
            if (client.State?.State.GalleryId != value.Id)
                throw new InvalidDataException("직원 기준 적용 버전 불일치");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            importFailed = true;
            galleryPending = true;
            message.Text = "직원 기준 적용 재시도 · " + ex.GetBaseException().Message;
        }
        finally
        {
            galleryLoading = false;
            if (ReferenceEquals(galleryStop, cancel))
                galleryStop = null;
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
    private void Refresh()
    {
        if (disposed || suspended)
            return;
        while (client.Notifications.TryDequeue(out var packet))
        {
            switch (packet.Type)
            {
                case "ready":
                    status.Text = "카메라 연결 · AI 준비 중";
                    break;
                case "recording_status":
                    RecordingStatusChanged?.Invoke(packet.Message ?? "녹화 상태 확인 필요");
                    break;
                case "progress":
                    if (client.State is null)
                        status.Text = packet.Message ?? "AI 준비 중";
                    break;
                case "queued":
                    if (packet.RequestId == client.LatestRequestId)
                    {
                        requiredCommand = packet.Id;
                        awaitingCommand = false;
                    }

                    break;
                case "reference" when packet.Reference is { } reference:
                    ReferenceReceived?.Invoke(reference);
                    break;
                case "error":
                    message.Text = packet.Error ?? "명령 처리 오류";
                    requiredCommand = 0;
                    awaitingCommand = false;
                    break;
                case "fatal":
                    status.Text = packet.Error ?? "AI 연결 종료";
                    match.Text = "비교 중지 · AI 연결 없음";
                    rendered = null;
                    StateUpdated?.Invoke(new AiState(), false);
                    _ = RecoverAsync(packet.Error ?? "AI 연결 종료");
                    return;
            }
        }

        if (suspended)
            return;
        var video = client.TakeLive();
        if (video is not null)
        {
            SetImage(live, video.Jpeg);
            LiveFrameUpdated?.Invoke(video);
        }

        var value = client.State;
        if (value is null)
            return;
        var state = value.State;
        var age = state.AgeMs + Stopwatch.GetElapsedTime(value.ReceivedAt).TotalMilliseconds;
        AnalysisAgeMs = age;
        // Wait for the first actual AI result, so startup cannot expire a load
        // command while native libraries/models are still being prepared.
        if (
            gallery is not null
            && galleryPending
            && !galleryLoading
            && client.Running
            && age <= 2500
            && (
                galleryAttemptAt == 0
                || Stopwatch.GetElapsedTime(galleryAttemptAt).TotalSeconds >= 5
            )
        )
            _ = LoadGalleryAsync(gallery);
        if (
            gallery is null
            && client.Running
            && !awaitingCommand
            && !importFailed
            && age <= 2500
            && inbox.TryTakePending(out var pending)
        )
            _ = LoadPendingReferenceAsync(pending!);
        var fresh =
            client.Running
            && !awaitingCommand
            && !inbox.HasPending
            && !importFailed
            && !galleryLoading
            && !galleryPending
            && (
                gallery is null
                || state.GalleryId == gallery.Id && state.GalleryCount == gallery.People.Count
            )
            && age <= 2500
            && state.CommandId >= requiredCommand;
        if (!ReferenceEquals(rendered, value))
        {
            SetImage(analyzed, value.Jpeg);
            rendered = value;
        }

        analyzedCaption.Text = $"마지막 분석 프레임 · {age / 1000:0.00}초 전";
        analyzedCaption.Foreground = Brushes.SlateGray;
        status.Text = !fresh
            ? "새 분석 결과 대기 · 이전 비교 결과 숨김"
            : state.Reason switch
            {
                "OK" => "얼굴 검출 정상",
                "MULTIPLE_FACES" or "MULTIPLE_DLIB_FACES" =>
                    "여러 얼굴 감지 · 한 명씩 인증해 주세요",
                "NO_MESH" or "DLIB_NO_FACE" => "얼굴 검출 대기 · 카메라를 바라봐 주세요",
                "MOVE_CLOSER" => "카메라에 조금 가까이 와 주세요",
                "BLUR_OR_LOW_DETAIL" => "얼굴 선명도 부족 · 자세와 조명을 확인해 주세요",
                _ => "분석 대기: " + state.Reason
            };
        direction.Text =
            $"현재: {PoseName(state.Pose)} · {(state.AutoEnrollment ? "권장 방향" : "다음 촬영")}: {(state.Expected is null ? "—" : PoseName(state.Expected))} · 안정된 분석 {state.Stable}회";
        if (state.GuidedEnrollment)
            direction.Text = $"현재 자세: {PoseName(state.Pose)} · 움직임에서 샘플 자동 선택";
        int target = state.EnrollmentTarget is 5 or 9 ? state.EnrollmentTarget : 9;
        progress.Text = state.Enrolling
            ? $"등록 진행 {state.PendingCount}/{target} · 이전 기준 {state.ReferenceCount}개 유지"
            : $"인증 기준 {state.ReferenceCount}개";
        if (state.GalleryMode)
            progress.Text =
                $"자동 직원 기준 {state.GalleryCount}명 · {(fresh ? "적용 완료" : "동기화/분석 대기")}";
        progress.Text +=
            state.GalleryMode || state.ReferenceLabel.Length == 0
                ? ""
                : " · 기준 표시: " + state.ReferenceLabel;
        if (role == "enrollment")
        {
            enrollmentProgress.Opacity = fresh ? 1 : .35;
            enrollmentProgress.Value =
                state.Enrolling ? state.PendingCount * 100.0 / 9
                : state.ReferenceCount == 9 ? 100
                : 0;
            livePose.Text = "현재 감지 방향: " + (fresh ? PoseName(state.Pose) : "분석 대기");
            bool returning = state.Enrolling && state.Expected == "FRONT" && state.PendingCount > 0;
            string guide = state.Expected switch
            {
                "FRONT" => "정면을 바라봐 주세요. 정면 기준을 자동으로 잡습니다.",
                "LEFT" => "고개를 천천히 왼쪽으로 돌렸다가 정면으로 돌아와 주세요.",
                "RIGHT" => "고개를 천천히 오른쪽으로 돌렸다가 정면으로 돌아와 주세요.",
                "UP" => "고개를 천천히 위로 들었다가 정면으로 돌아와 주세요.",
                "DOWN" => "턱을 천천히 내렸다가 정면으로 돌아와 주세요.",
                _ => "안내를 따라 천천히 움직여 주세요."
            };
            coverageHint.Text =
                !fresh ? "카메라·AI 상태 확인 중 · 새 분석을 기다려 주세요."
                : state.Enrolling
                    ? (returning ? "정면으로 돌아와 주세요. 다음 방향을 안내합니다." : guide)
                : state.ReferenceCount == 9
                    ? "샘플 준비 완료 · 선택 직원을 확인하고 DB 저장을 누르세요."
                : "정면을 바라본 상태에서 등록 시작을 누르세요.";
            foreach (var (key, row) in stages)
            {
                int required =
                    key == "FRONT" ? 1
                    : state.Enrolling || state.Coverage.Any(p => p.EndsWith("#2")) ? 2
                    : 1;
                int count =
                    (state.Coverage.Contains(key) ? 1 : 0)
                    + (required == 2 && state.Coverage.Contains(key + "#2") ? 1 : 0);
                bool complete = fresh && count >= required;
                bool active = fresh && state.Enrolling && key == state.Expected;
                row.Text =
                    (
                        active ? "●  "
                        : complete ? "✓  "
                        : "○  "
                    )
                    + PoseName(key)
                    + (fresh ? $"  {count}/{required}" : "  확인 대기")
                    + (returning && key == "FRONT" ? " · 복귀" : "");
                row.Foreground =
                    active ? Brushes.RoyalBlue
                    : complete ? Brushes.DarkGreen
                    : Brushes.SlateGray;
            }

            progress.Text =
                !fresh ? "등록 상태 확인 중"
                : state.Enrolling
                    ? $"자동 수집·선택 {state.PendingCount * 100 / 9}% · 적합 후보 {state.AcceptedCandidates}개 · 기존 기준 유지"
                : $"인증 기준 {state.ReferenceCount}개";
        }

        string Millis(string key) =>
            state.Timings.TryGetValue(key, out var number) && number.HasValue
                ? number.Value.ToString("0")
                : "—";
        timing.Text =
            $"처리(ms): Mesh {Millis("mesh_ms")} / 검출·정렬 {Millis("dlib_ms")} / ArcFace {Millis("embedding_ms")} / 전체 {Millis("total_ms")}";
        message.Text = Explain(state.Message);
        if (
            fresh
            && state.Matching
            && state.Comparison is { ValueKind: JsonValueKind.Array } comparison
            && comparison.GetArrayLength() == 3
        )
        {
            var distance = comparison[0].GetDouble();
            var candidate = comparison[2].GetBoolean();
            match.Text =
                $"{(candidate ? "등록 얼굴 일치 후보" : "등록 얼굴 불일치")} · 거리 {distance:0.000} / 기준 0.450 · 마지막 분석 프레임 기준";
            match.Foreground = candidate ? Brushes.DarkGreen : Brushes.Firebrick;
        }
        else
        {
            match.Text =
                role == "recognition" && fresh && state.AutomaticRecognition
                    ? state.ReferenceCount is not (5 or 9)
                        ? "서버에서 등록 직원 기준을 자동 동기화합니다."
                        : !state.AutoStartEnabled && !state.Matching
                            ? "얼굴 비교 중지 · 비교 시작 버튼으로 다시 켜세요."
                            : !state.Calibrated
                                ? "정면을 바라보고 잠시 멈춰 주세요 · 자동 보정 대기"
                                : state.Matching
                                    ? "얼굴 비교 중 · 한 명의 선명한 얼굴을 기다립니다."
                                    : "자동 비교 시작 대기"
                    : "얼굴 비교 대기/중지";
            match.Foreground = Brushes.DimGray;
        }

        if (fresh && state.Identification?.Result == "AMBIGUOUS")
        {
            match.Text = "식별 보류 · 두 직원의 얼굴 점수가 비슷합니다. 정면·조명을 조정해 주세요.";
            match.Foreground = Brushes.DarkOrange;
        }

        compare.Content = state.Matching ? "얼굴 비교 중지" : "얼굴 비교 시작";
        calibrate.IsEnabled = fresh && state.Reason == "OK";
        enroll.IsEnabled = fresh && state.Reason == "OK" && !state.Enrolling;
        capture.IsEnabled = client.Running && state.Enrolling;
        compare.IsEnabled =
            fresh && state.Calibrated && state.ReferenceCount is 5 or 9 && !state.Enrolling;
        StateUpdated?.Invoke(state, fresh);
    }

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
