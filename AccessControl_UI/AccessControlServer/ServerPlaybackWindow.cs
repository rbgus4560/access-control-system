// 파일 역할: 서버 타임라인 재생 창과 클립 요청 창입니다. 추출 결과는 스크롤 영역, 폴더 버튼은 하단에 배치합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AccessControl.Faces;

namespace AccessControlServer;

public sealed class ServerPlaybackWindow : Window
{
    private readonly PlaybackPanel playback = new(autoLoad: true, includeLive: false);
    private readonly MediaCoordinator media;

    public ServerPlaybackWindow(Func<FaceDatabase?> database, MediaCoordinator media)
    {
        this.media = media;
        Title = "서버 · 녹화 타임라인 / 클라이언트 원본 재생";
        Width = 1150;
        Height = 850;
        MinWidth = 850;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        playback.ReadDayAsync = (date, token) =>
            (
                database() ?? throw new InvalidOperationException("서버 DB에 연결하세요.")
            ).RecordingDayAsync(date, token);
        playback.PrepareAsync = media.PrepareAsync;
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var tools = new StackPanel();
        DockPanel.SetDock(tools, Dock.Bottom);
        root.Children.Add(tools);
        var clip = new Wpf.Ui.Controls.Button
        {
            Content = "선택 시각 클립 요청",
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        clip.Click += (_, _) =>
            new ServerClipWindow(
                media,
                playback.SelectedDate,
                playback.SelectedTime,
                playback.SelectedEventId
            )
            {
                Owner = this
            }.Show();
        tools.Children.Add(clip);
        tools.Children.Add(playback);
        root.Children.Add(playback.Screen);
        Closed += (_, _) => playback.Dispose();
    }

    public Task<EventPlaybackReply> OpenAsync(
        EventPlaybackRequest request,
        CancellationToken token
    ) => playback.OpenEventAsync(request, token);
}

public sealed class ServerClipWindow : Window
{
    private readonly DatePicker date = new();
    private readonly TextBox start = new() { Width = 130 },
        end = new() { Width = 130 };
    private readonly CheckBox link = new() { Content = "선택 출입 기록 연결" };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 12, 0, 0)
    };
    private readonly Button export = new()
    {
        Content = "클라이언트에서 추출 → 서버 수신",
        Padding = new Thickness(12)
    };
    private readonly CancellationTokenSource stop = new();

    public ServerClipWindow(
        MediaCoordinator media,
        DateTime initialDate,
        string initialTime,
        Guid? eventId
    )
    {
        Title = "서버 · MP4 클립 요청";
        Width = 720;
        Height = 540;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(24) };
        Content = layout;
        var root = new StackPanel();
        root.Children.Add(
            new TextBlock
            {
                Text = "클라이언트 원본에서 클립 생성",
                FontSize = 23,
                FontWeight = FontWeights.Bold
            }
        );
        root.Children.Add(
            new TextBlock
            {
                Text = "최대 5분 · 원본은 클라이언트에 유지 · 검증된 MP4 사본만 서버 보관",
                Margin = new Thickness(0, 12, 0, 16),
                TextWrapping = TextWrapping.Wrap
            }
        );
        date.SelectedDate = initialDate;
        root.Children.Add(date);
        if (!RecordingTimeline.TryTime(initialDate, initialTime, out var at))
            at = RecordingTimeline.LocalMidnight(initialDate);
        start.Text = at.LocalDateTime.ToString("HH:mm:ss.fff");
        end.Text = at.AddSeconds(5).LocalDateTime.ToString("HH:mm:ss.fff");
        var times = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        times.Children.Add(new TextBlock { Text = "시작  " });
        times.Children.Add(start);
        times.Children.Add(new TextBlock { Text = "  종료  " });
        times.Children.Add(end);
        root.Children.Add(times);
        link.IsEnabled = eventId is not null;
        link.IsChecked = eventId is not null;
        root.Children.Add(link);
        root.Children.Add(export);
        root.Children.Add(status);
        var folder = new Button
        {
            Content = "서버 클립 폴더 열기",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DockPanel.SetDock(folder, Dock.Bottom);
        layout.Children.Add(folder);
        layout.Children.Add(
            new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 20, 0)
            }
        );
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(ClipFiles.Root);
                Process.Start(new ProcessStartInfo(ClipFiles.Root) { UseShellExecute = true });
            }
            catch (Exception)
            {
                status.Text = "폴더 열기 실패";
            }
        };
        export.Click += async (_, _) =>
        {
            if (
                date.SelectedDate is not { } selected
                || !RecordingTimeline.TryTime(selected, start.Text, out var from)
                || !RecordingTimeline.TryTime(selected, end.Text, out var to)
            )
            {
                status.Text = "시각 형식: HH:mm:ss 또는 HH:mm:ss.fff";
                return;
            }

            export.IsEnabled =
                date.IsEnabled =
                start.IsEnabled =
                end.IsEnabled =
                link.IsEnabled =
                    false;
            try
            {
                status.Text =
                    "클라이언트 추출·전송·SHA-256 검증 중… 창을 닫아도 접수된 작업은 서버가 보관합니다.";
                var clip = await media.ExportAsync(
                    from,
                    to,
                    link.IsChecked == true ? eventId : null,
                    stop.Token
                );
                status.Text =
                    clip.Status == "READY"
                        ? $"서버 MP4 저장·해시 검증 완료 · {clip.DurationSeconds:0.0}초\n{clip.FilePath}\nDB 반영 실패 시 자동 재시도합니다."
                        : "클립 추출 실패 · 선택 범위의 원본·녹화 공백을 확인하고 새 범위로 요청하세요.\n실패 사유: "
                            + await File.ReadAllTextAsync(clip.FilePath + ".error.txt", stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text =
                    "요청 실패/대기 · "
                    + ex.Message
                    + "\n이미 접수된 작업은 서버 클립 폴더의 remotejob.json으로 재시도합니다.";
            }
            finally
            {
                if (!stop.IsCancellationRequested)
                {
                    export.IsEnabled = date.IsEnabled = start.IsEnabled = end.IsEnabled = true;
                    link.IsEnabled = eventId is not null;
                }
            }
        };
        Closed += (_, _) =>
        {
            stop.Cancel();
        };
    }
}
