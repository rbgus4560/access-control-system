// 파일 역할: 날짜·시각·구간 선택과 재생 상태를 관리합니다. 필요하면 클라이언트 원본을 서버 캐시에 요청합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AccessControl.Faces;

namespace AccessControlServer;

public sealed class PlaybackPanel : UserControl, IDisposable
{
    private sealed record SegmentChoice(RecordingEntry Entry)
    {
        public override string ToString() =>
            $"{Entry.StartTime.ToLocalTime():HH:mm:ss} ~ {Entry.EndTime!.Value.ToLocalTime():HH:mm:ss}";
    }

    private readonly DatePicker date = new()
    {
        SelectedDate = DateTime.Today,
        Width = 155,
        SelectedDateFormat = DatePickerFormat.Short,
        Margin = new Thickness(3)
    };
    private readonly TextBox time = new()
    {
        Text = "00:00:00",
        Width = 80,
        TextAlignment = TextAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(3),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox segments = new() { Width = 190, Margin = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new()
    {
        Text = "완료된 녹화 구간 조회 중",
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4)
    };
    private readonly TextBlock screenTitle = new()
    {
        Foreground = Brushes.White,
        FontSize = 20,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(16)
    };
    private readonly TextBlock screenStatus = new()
    {
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16)
    };
    private readonly ContentControl videoHost = new();
    private readonly RecordingTimelineBar bar = new();
    private readonly Button load,
        pause;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly CancellationTokenSource lifetime = new();
    private RecordingDay? day;
    private RecordingEntry? current;
    private MediaElement? player;
    private IDisposable? mediaUse;
    private bool playing,
        busy,
        disposed;
    private double requestedOffset;
    private long navigation;
    private string eventContext = "";
    public Func<DateTime, CancellationToken, Task<RecordingDay>> ReadDayAsync { get; set; } =
        RecordingTimeline.ReadAsync;
    public Func<RecordingEntry, CancellationToken, Task<RecordingEntry>>? PrepareAsync { get; set; }
    public bool Active { get; private set; }
    public DateTime SelectedDate => date.SelectedDate ?? DateTime.Today;
    public string SelectedTime => time.Text;
    public Guid? SelectedEventId { get; private set; }

    public StackPanel AuxiliaryActions { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    public event Action? ModeChanged;
    public Grid Screen { get; } =
        new() { Background = new SolidColorBrush(Color.FromRgb(22, 34, 44)) };

    public PlaybackPanel(bool compact = false, bool autoLoad = true, bool includeLive = true)
    {
        FontSize = 13;
        var segmentStyle = new Style(typeof(ComboBoxItem));
        segmentStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        segmentStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 5, 8, 5)));
        segmentStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 2, 0)));
        segments.ItemContainerStyle = segmentStyle;
        var root = new StackPanel { Margin = new Thickness(12, 4, 12, 4) };
        Content = root;
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        root.Children.Add(row);
        date.Height = time.Height = segments.Height = 32;
        time.BorderThickness = new Thickness(0);
        time.Padding = new Thickness(4, 0, 4, 0);
        segments.SetResourceReference(TemplateProperty, "StudioDropDownTemplate");
        segments.Background = Brushes.White;
        segments.BorderThickness = new Thickness(0);
        date.VerticalAlignment = time.VerticalAlignment = segments.VerticalAlignment = VerticalAlignment.Center;
        date.Margin = time.Margin = segments.Margin = new Thickness(0, 0, 6, 0);
        StackPanel Group(string label)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
            group.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new Border { Child = group, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(224, 231, 241)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 0, 10, 6) });
            return group;
        }
        var dateGroup = Group("날짜");
        dateGroup.Children.Add(date);
        load = Button("조회", async () => await Reload());
        dateGroup.Children.Add(load);
        var segmentGroup = Group("구간");
        segmentGroup.Children.Add(segments);
        segmentGroup.Children.Add(Button("재생", PlaySelected));
        var timeGroup = Group("시각");
        timeGroup.Children.Add(time);
        timeGroup.Children.Add(Button("재생", PlayTime));
        var controls = new WrapPanel();
        var controlRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        controlRow.ColumnDefinitions.Add(new ColumnDefinition());
        controlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlRow.Children.Add(controls);
        Grid.SetColumn(AuxiliaryActions, 1);
        controlRow.Children.Add(AuxiliaryActions);
        root.Children.Add(controlRow);
        pause = Button("일시정지", TogglePause);
        pause.IsEnabled = false;
        controls.Children.Add(pause);
        controls.Children.Add(Button("다음 구간", PlayNext));
        if (includeLive)
            controls.Children.Add(Button("LIVE 복귀", GoLive));
        controls.Children.Add(new TextBlock { Text = "보기", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) });
        controls.Children.Add(Button("24시간", () => bar.Zoom(86400)));
        controls.Children.Add(Button("1시간", () => bar.Zoom(3600)));
        controls.Children.Add(Button("10분", () => bar.Zoom(600)));
        if (compact)
        {
            root.Children.Remove(row);
            root.Children.Remove(controlRow);
            var tools = new StackPanel();
            tools.Children.Add(row);
            tools.Children.Add(controlRow);
            root.Children.Add(
                new Expander
                {
                    Header = "녹화 탐색 도구 · 날짜/시각/구간 선택",
                    Content = tools,
                    IsExpanded = false,
                    Margin = new Thickness(2, 0, 2, 6)
                }
            );
        }

        root.Children.Add(bar);
        // 하단 중복 안내는 표시하지 않고 재생 중 상태는 영상 영역에서 알립니다.
        Screen.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Screen.RowDefinitions.Add(new RowDefinition());
        Screen.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Screen.Children.Add(screenTitle);
        Grid.SetRow(videoHost, 1);
        Screen.Children.Add(videoHost);
        Grid.SetRow(screenStatus, 2);
        Screen.Children.Add(screenStatus);
        bar.Seek += seconds =>
        {
            if (day is not null && date.SelectedDate?.Date == day.Start.LocalDateTime.Date)
                PlayAt(day.Start.AddSeconds(seconds));
            else
                SetStatus("날짜를 변경했다면 구간 조회를 눌러 주세요.");
        };
        date.SelectedDateChanged += (_, _) =>
        {
            if (day is not null && date.SelectedDate?.Date != day.Start.LocalDateTime.Date)
            {
                GoLive();
                segments.ItemsSource = null;
                day = null;
                bar.Day = null;
                bar.InvalidateVisual();
                SetStatus("선택한 날짜의 구간 조회를 눌러 주세요.");
            }
        };
        segments.SelectionChanged += (_, _) =>
        {
            if (segments.SelectedItem is SegmentChoice choice)
            {
                var at = choice.Entry.StartTime < day!.Start ? day.Start : choice.Entry.StartTime;
                time.Text = at.ToLocalTime().ToString("HH:mm:ss");
                bar.Follow((at - day.Start).TotalSeconds);
            }
        };
        Loaded += async (_, _) =>
        {
            if (autoLoad && day is null && !disposed)
                await Reload();
        };
        clock.Tick += (_, _) => Tick();
        clock.Start();
    }

    private static Button Button(string title, Action action)
    {
        var b = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = title,
            Padding = new Thickness(10, 4, 10, 4),
            Height = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        b.Click += (_, _) => action();
        return b;
    }

    private void SetStatus(string text)
    {
        status.Text = text;
        if (Active)
            screenStatus.Text = text;
    }

    private async Task Reload()
    {
        if (busy || disposed || date.SelectedDate is not { } selected)
            return;
        busy = true;
        load.IsEnabled = false;
        SetStatus("완료된 녹화 구간 조회 중…");
        long ticket = ++navigation;
        try
        {
            var loaded = await ReadDayAsync(selected, lifetime.Token);
            if (disposed || ticket != navigation || date.SelectedDate?.Date != selected.Date)
                return;
            day = loaded;
            bar.Day = day;
            segments.ItemsSource = day.Segments.Select(e => new SegmentChoice(e)).ToList();
            segments.SelectedIndex = day.Segments.Count - 1;
            bar.InvalidateVisual();
            SetStatus(
                $"{selected:yyyy-MM-dd} · 완료 녹화 {day.Segments.Count}구간 · 완료 메타데이터 기준"
                    + (day.Unavailable > 0 ? $" · 파일/정보 확인 필요 {day.Unavailable}건" : "")
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!disposed)
                SetStatus("녹화 구간 조회 실패 · 서버 DB 연결·15번 SQL을 확인하세요.");
        }
        finally
        {
            busy = false;
            if (!disposed)
                load.IsEnabled = true;
        }
    }

    private void PlaySelected()
    {
        if (segments.SelectedItem is not SegmentChoice choice || day is null)
        {
            SetStatus("구간 조회 후 녹화 구간을 선택해 주세요.");
            return;
        }

        PlayAt(choice.Entry.StartTime < day.Start ? day.Start : choice.Entry.StartTime);
    }

    private void PlayTime()
    {
        if (day is null || date.SelectedDate?.Date != day.Start.LocalDateTime.Date)
        {
            SetStatus("먼저 선택 날짜의 구간 조회를 눌러 주세요.");
            return;
        }

        if (!RecordingTimeline.TryTime(date.SelectedDate!.Value, time.Text, out var at))
        {
            SetStatus("시각은 HH:mm:ss 형식으로 입력하세요.");
            return;
        }

        PlayAt(at);
    }

    private async void PlayAt(DateTimeOffset at, bool fromEvent = false)
    {
        if (disposed || day is null)
            return;
        long ticket = ++navigation;
        if (!fromEvent)
        {
            eventContext = "";
            SelectedEventId = null;
        }

        StopPlayer();
        Active = true;
        ModeChanged?.Invoke();
        bar.Follow((at - day.Start).TotalSeconds);
        time.Text = at.ToLocalTime().ToString("HH:mm:ss");
        current = RecordingTimeline.FindAt(day.Segments, at);
        if (current is null)
        {
            SetTitle(at);
            SetStatus("선택 시각에 완료된 녹화가 없습니다. 파란 구간이나 다른 시각을 선택하세요.");
            return;
        }

        try
        {
            if (PrepareAsync is { } prepare)
            {
                SetStatus("클라이언트 원본 요청·검증 중…");
                var prepared = await prepare(current, lifetime.Token);
                if (disposed || ticket != navigation)
                    return;
                current = prepared;
            }

            mediaUse =
                RecordingMediaUse.TryUse([current.FilePath])
                ?? throw new IOException("보관 정리 중인 파일");
            RecordingFiles.Validate(current);
            var file = new FileInfo(current.FilePath);
            if (!file.Exists || file.Length != current.FileSizeBytes)
                throw new IOException();
            var entry = current;
            requestedOffset = Math.Max(0, (at - entry.StartTime).TotalSeconds);
            var media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                Stretch = Stretch.Uniform,
                ScrubbingEnabled = true,
                Volume = 0
            };
            player = media;
            videoHost.Content = media;
            SetTitle(at);
            SetStatus("녹화 파일 여는 중 · 실시간 인증·녹화는 계속됩니다.");
            media.MediaOpened += (_, _) =>
            {
                if (disposed || !ReferenceEquals(player, media) || !Active)
                    return;
                if (
                    !media.NaturalDuration.HasTimeSpan
                    || requestedOffset >= media.NaturalDuration.TimeSpan.TotalSeconds
                )
                {
                    StopPlayer();
                    SetStatus("선택 위치가 실제 동영상 길이를 벗어납니다. 다른 구간을 선택하세요.");
                    return;
                }

                media.Position = TimeSpan.FromSeconds(requestedOffset);
                media.Play();
                playing = true;
                pause.IsEnabled = true;
                pause.Content = "일시정지";
                SetStatus("녹화 재생 중 · 현재 출입 판정은 서버 화면에서 확인하세요.");
            };
            media.MediaEnded += (_, _) =>
            {
                if (!ReferenceEquals(player, media) || disposed)
                    return;
                EndSegment();
            };
            media.MediaFailed += (_, _) =>
            {
                if (!ReferenceEquals(player, media) || disposed)
                    return;
                StopPlayer();
                SetStatus(
                    "Windows 영상 재생 실패 · 받은 MP4의 기본 플레이어 재생 여부를 확인하세요."
                );
            };
            media.Source = new Uri(file.FullName, UriKind.Absolute);
            media.Play();
        }
        catch (Exception ex)
        {
            if (!disposed && ticket == navigation)
            {
                StopPlayer();
                SetStatus("녹화 열기 실패 · " + ex.Message);
            }
        }
    }

    private void Tick()
    {
        if (!Active || !playing || player is null || current is null || day is null)
            return;
        var at = current.StartTime.AddSeconds(
            Math.Clamp(
                player.Position.TotalSeconds,
                0,
                (current.EndTime!.Value - current.StartTime).TotalSeconds
            )
        );
        if (at >= day.End)
        {
            player.Pause();
            playing = false;
            pause.IsEnabled = false;
            bar.Follow(86400);
            SetStatus("선택한 날짜의 재생이 끝났습니다. 다음 날짜를 조회할 수 있습니다.");
            return;
        }

        SetTitle(at);
        bar.Follow((at - day.Start).TotalSeconds);
    }

    private void TogglePause()
    {
        if (player is null || !Active)
            return;
        if (playing)
        {
            player.Pause();
            playing = false;
            pause.Content = "재생";
            SetStatus("녹화 일시정지");
        }
        else
        {
            player.Play();
            playing = true;
            pause.Content = "일시정지";
            SetStatus("녹화 재생 중");
        }
    }

    private void EndSegment()
    {
        if (day is null || current is null)
            return;
        var next = RecordingTimeline.Next(day.Segments, current);
        if (next is not null && RecordingTimeline.Continuous(current, next))
        {
            PlayAt(next.StartTime, fromEvent: true);
            return;
        }

        player?.Pause();
        playing = false;
        pause.IsEnabled = false;
        if (day is not null)
            bar.Follow((current.EndTime!.Value - day.Start).TotalSeconds);
        SetStatus(
            next is null
                ? "마지막 녹화 구간 재생 완료"
                : "녹화가 끊긴 구간입니다. 다음 구간 버튼으로 이동할 수 있습니다."
        );
    }

    private void PlayNext()
    {
        if (day is null)
        {
            SetStatus("먼저 구간 조회를 눌러 주세요.");
            return;
        }

        if (current is null)
        {
            PlaySelected();
            return;
        }

        var next = RecordingTimeline.Next(day.Segments, current);
        if (next is null)
        {
            SetStatus("다음 녹화 구간이 없습니다.");
            return;
        }

        PlayAt(next.StartTime);
    }

    private void StopPlayer()
    {
        playing = false;
        pause.IsEnabled = false;
        var previous = player;
        player = null;
        if (previous is not null)
        {
            previous.Stop();
            previous.Close();
            previous.Source = null;
        }

        videoHost.Content = null;
        mediaUse?.Dispose();
        mediaUse = null;
    }

    private void SetTitle(DateTimeOffset at) =>
        screenTitle.Text =
            $"PLAYBACK · {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            + (eventContext.Length > 0 ? "\n" + eventContext : "");

    public async Task<EventPlaybackReply> OpenEventAsync(
        EventPlaybackRequest request,
        CancellationToken token
    )
    {
        EventPlaybackPipe.Validate(request);
        var reply = new EventPlaybackReply
        {
            RequestId = request.RequestId,
            EventId = request.EventId
        };
        if (disposed)
            return reply with { Message = "재생 창 종료 중입니다." };
        GoLive();
        date.SelectedDate = request.EventTime.LocalDateTime.Date;
        long ticket = ++navigation;
        eventContext = request.Summary;
        SelectedEventId = request.EventId;
        Active = true;
        ModeChanged?.Invoke();
        SetTitle(request.EventTime);
        SetStatus("출입 기록 시각의 녹화 구간 조회 중…");
        try
        {
            var loaded = await ReadDayAsync(request.EventTime.LocalDateTime.Date, token);
            if (disposed || token.IsCancellationRequested || ticket != navigation)
                return reply with { Message = "다른 재생 또는 LIVE 전환으로 요청이 취소됐습니다." };
            day = loaded;
            bar.Day = day;
            segments.ItemsSource = day.Segments.Select(e => new SegmentChoice(e)).ToList();
            var found = RecordingTimeline.FindAt(day.Segments, request.EventTime);
            segments.SelectedItem = segments
                .Items.Cast<SegmentChoice>()
                .FirstOrDefault(s => s.Entry.SegmentId == found?.SegmentId);
            PlayAt(request.EventTime, fromEvent: true);
            return reply with
            {
                Success = true,
                HasRecording = found is not null,
                Message = found is null
                    ? "해당 출입 시각에 완료된 녹화가 없습니다. 녹화 중인 구간은 완료 후 다시 조회하세요."
                    : "서버 재생 창에서 해당 구간을 요청했습니다. 수신·재생 상태를 확인하세요."
            };
        }
        catch (OperationCanceledException)
        {
            if (!disposed && ticket == navigation)
                SetStatus("녹화 조회 시간이 초과됐습니다. 다시 시도하세요.");
            return reply with { Message = "녹화 조회 시간이 초과됐습니다. 다시 시도하세요." };
        }
        catch (Exception)
        {
            if (!disposed && ticket == navigation)
                SetStatus("출입 기록의 녹화 조회 실패 · 로컬 파일을 확인하세요.");
            return reply with { Message = "출입 기록의 녹화 파일을 조회하지 못했습니다." };
        }
    }

    public void GoLive()
    {
        if (disposed)
            return;
        navigation++;
        eventContext = "";
        SelectedEventId = null;
        StopPlayer();
        current = null;
        Active = false;
        ModeChanged?.Invoke();
        SetStatus("날짜·구간 조회 대기");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        clock.Stop();
        StopPlayer();
        lifetime.Dispose();
    }
}
