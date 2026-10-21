// 파일 역할: C#에서 Python 자식 프로세스를 실행하고 JSON 메시지를 교환합니다. 직원 기준 적용 ACK와 최신 분석 상태를 관리합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace AccessControl.Ai;

public sealed class IdentificationState
{
    [JsonPropertyName("person_id")]
    public string PersonId { get; set; } = "";

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("runner_up_distance")]
    public double? RunnerUpDistance { get; set; }
}

public sealed class AiState
{
    [JsonPropertyName("gallery_mode")]
    public bool GalleryMode { get; set; }

    [JsonPropertyName("gallery_id")]
    public string GalleryId { get; set; } = "";

    [JsonPropertyName("gallery_count")]
    public int GalleryCount { get; set; }

    [JsonPropertyName("identification")]
    public IdentificationState? Identification { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("pose")]
    public string Pose { get; set; } = "UNKNOWN";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("command_id")]
    public int CommandId { get; set; }

    [JsonPropertyName("age_ms")]
    public double AgeMs { get; set; }

    [JsonPropertyName("reference_count")]
    public int ReferenceCount { get; set; }

    [JsonPropertyName("pending_count")]
    public int PendingCount { get; set; }

    [JsonPropertyName("enrollment_target")]
    public int EnrollmentTarget { get; set; } = 5;

    [JsonPropertyName("coverage")]
    public string[] Coverage { get; set; } = [];

    [JsonPropertyName("auto_enrollment")]
    public bool AutoEnrollment { get; set; }

    [JsonPropertyName("guided_enrollment")]
    public bool GuidedEnrollment { get; set; }

    [JsonPropertyName("accepted_candidates")]
    public int AcceptedCandidates { get; set; }

    [JsonPropertyName("enrolling")]
    public bool Enrolling { get; set; }

    [JsonPropertyName("calibrated")]
    public bool Calibrated { get; set; }

    [JsonPropertyName("automatic_recognition")]
    public bool AutomaticRecognition { get; set; }

    [JsonPropertyName("auto_start_enabled")]
    public bool AutoStartEnabled { get; set; }

    [JsonPropertyName("matching")]
    public bool Matching { get; set; }

    [JsonPropertyName("stable")]
    public int Stable { get; set; }

    [JsonPropertyName("expected")]
    public string? Expected { get; set; }

    [JsonPropertyName("reference_label")]
    public string ReferenceLabel { get; set; } = "";

    [JsonPropertyName("comparison")]
    public JsonElement? Comparison { get; set; }

    [JsonPropertyName("timings")]
    public Dictionary<string, double?> Timings { get; set; } = new();
}

public sealed class AiPacket
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("jpeg")]
    public string? Jpeg { get; set; }

    [JsonPropertyName("state")]
    public AiState? State { get; set; }

    [JsonPropertyName("reference")]
    public JsonElement? Reference { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("request_id")]
    public int RequestId { get; set; }
}

public sealed record ReceivedState(AiState State, string? Jpeg, long ReceivedAt);

public sealed record ReceivedLive(string Jpeg, long ReceivedAt);

public sealed class AiClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> applied = new();
    private Process? process;
    private readonly SemaphoreSlim writes = new(1, 1);
    private Task? reader,
        errorReader;
    private bool stopping;
    private int requestId;
    private long liveCount,
        stateCount,
        startedAt;
    public int LatestRequestId => Volatile.Read(ref requestId);

    private ReceivedLive? live;
    private ReceivedState? state;
    private readonly ConcurrentQueue<string> diagnostics = new();
    public ConcurrentQueue<AiPacket> Notifications { get; } = new();
    public bool Running => process is { HasExited: false };
    public ReceivedState? State => Volatile.Read(ref state);

    public ReceivedLive? TakeLive() => Interlocked.Exchange(ref live, null);

    public string DiagnosticText =>
        "AI 연결 진단 6.1"
        + Environment.NewLine
        + $"실행 중: {Running} · 영상 {Interlocked.Read(ref liveCount)}개 · 분석 {Interlocked.Read(ref stateCount)}개"
        + (
            startedAt == 0
                ? ""
                : $" · 시작 후 {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:0}초"
        )
        + Environment.NewLine
        + string.Join(Environment.NewLine, diagnostics);

    private void Log(string line)
    {
        diagnostics.Enqueue(line.Length > 1000 ? line[..1000] : line);
        while (diagnostics.Count > 60)
            diagnostics.TryDequeue(out _);
    }

    // AI 출력은 JSON 전용 채널로 읽고 진단 출력은 별도로 보관합니다. 영상과 분석 상태는 최신 값으로 교체합니다.
    public void Start(AiSettings settings, string role)
    {
        if (process is not null)
            throw new InvalidOperationException("먼저 기존 연결을 종료해 주세요.");
        if (!File.Exists(settings.Python))
            throw new FileNotFoundException("가상환경의 python.exe 경로를 확인해 주세요.");
        var script = Path.Combine(settings.AiDirectory, "wpf_bridge.py");
        if (!File.Exists(script))
            throw new FileNotFoundException(
                "AI 폴더에 wpf_bridge.py가 없습니다. 이번 ZIP의 FaceAI 폴더를 선택해 주세요."
            );
        if (
            role == "recognition"
            && !File.Exists(Path.Combine(settings.AiDirectory, "face_gallery.py"))
        )
            throw new FileNotFoundException(
                "전체 직원 자동 식별에는 12.3의 FaceAI 폴더가 필요합니다. 카메라·AI 설정에서 이번 압축 파일의 FaceAI를 선택하세요."
            );
        var start = new ProcessStartInfo(settings.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = settings.AiDirectory
        };
        foreach (
            var arg in new[]
            {
                "-u",
                script,
                "--role",
                role,
                "--camera",
                settings.Camera.ToString(),
                "--backend",
                settings.Backend,
                "--yaw-sign",
                settings.ReverseYaw ? "-1" : "1",
                "--pitch-sign",
                settings.ReversePitch ? "-1" : "1"
            }
        )
            start.ArgumentList.Add(arg);
        if (role == "recognition")
        {
            start.ArgumentList.Add("--record-root");
            start.ArgumentList.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AccessControlSystem",
                    "recordings",
                    "CAM-01"
                )
            );
        }

        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["DEEPFACE_HOME"] = settings.ModelHome;
        // Match the native DLL search paths supplied by an activated Conda terminal.
        var pythonRoot = Path.GetDirectoryName(settings.Python)!;
        if (OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(pythonRoot, "conda-meta")))
        {
            var directories = new[]
            {
                pythonRoot,
                Path.Combine(pythonRoot, "Library", "mingw-w64", "bin"),
                Path.Combine(pythonRoot, "Library", "usr", "bin"),
                Path.Combine(pythonRoot, "Library", "bin"),
                Path.Combine(pythonRoot, "Scripts"),
                Path.Combine(pythonRoot, "bin")
            }.Where(Directory.Exists);
            start.Environment["PATH"] =
                string.Join(Path.PathSeparator, directories)
                + Path.PathSeparator
                + (start.Environment.TryGetValue("PATH", out var previousPath) ? previousPath : "");
            start.Environment["CONDA_PREFIX"] = pythonRoot;
        }

        while (diagnostics.TryDequeue(out _)) { }

        liveCount = stateCount = 0;
        startedAt = Stopwatch.GetTimestamp();
        Log("Python: " + settings.Python);
        Log("AI: " + script);
        Log("모델: " + settings.ModelHome);
        stopping = false;
        process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Python 실행 실패");
            Log("Python 프로세스 시작: " + process.Id);
        }
        catch
        {
            process.Dispose();
            process = null;
            throw;
        }

        var running = process;
        reader = Task.Run(async () =>
        {
            try
            {
                while (await running.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length > 3_000_000)
                        throw new InvalidDataException("AI 응답 크기 초과");
                    var packet =
                        JsonSerializer.Deserialize<AiPacket>(line)
                        ?? throw new InvalidDataException("AI 응답 없음");
                    if (packet.Type == "live" && packet.Jpeg is { } jpeg)
                    {
                        Interlocked.Increment(ref liveCount);
                        Interlocked.Exchange(
                            ref live,
                            new ReceivedLive(jpeg, Stopwatch.GetTimestamp())
                        );
                    }
                    else if (packet.Type == "state" && packet.State is { } value)
                    {
                        if (Interlocked.Increment(ref stateCount) == 1)
                            Log("첫 AI 분석 결과 수신");
                        Interlocked.Exchange(
                            ref state,
                            new ReceivedState(value, packet.Jpeg, Stopwatch.GetTimestamp())
                        );
                    }
                    else
                    {
                        if (
                            packet.Type == "queued"
                            && applied.TryGetValue(packet.RequestId, out var waiting)
                        )
                            waiting.TrySetResult(packet.Id);
                        Log(packet.Type + ": " + (packet.Message ?? packet.Error ?? ""));
                        Notifications.Enqueue(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stopping)
                    Notifications.Enqueue(new AiPacket { Type = "fatal", Error = ex.Message });
            }
            finally
            {
                if (!stopping)
                    Notifications.Enqueue(
                        new AiPacket
                        {
                            Type = "fatal",
                            Error = "AI 연결이 종료됐습니다. 진단 내용을 확인해 주세요."
                        }
                    );
            }
        });
        errorReader = Task.Run(async () =>
        {
            try
            {
                while (await running.StandardError.ReadLineAsync() is { } line)
                    Log(line);
            }
            catch (Exception ex)
            {
                Log(ex.Message);
            }
        });
    }

    public Task SendAsync(string key, object? reference = null, string? label = null) =>
        SendCoreAsync(key, reference, label, null);

    private async Task SendCoreAsync(
        string key,
        object? reference,
        string? label,
        Action<int>? register
    )
    {
        var request = Interlocked.Increment(ref requestId);
        register?.Invoke(request);
        await writes.WaitAsync();
        try
        {
            if (process is not { HasExited: false })
                throw new InvalidOperationException("먼저 카메라·AI를 연결해 주세요.");
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        key,
                        reference,
                        label,
                        request_id = request
                    }
                )
            );
            await process.StandardInput.FlushAsync();
        }
        finally
        {
            writes.Release();
        }
    }

    // 입력 전송 완료가 아닌 Python의 실제 명령 적용까지 기다립니다. 직원별 전송으로 명령 큐 용량을 지킵니다.
    public async Task SendAppliedAsync(string key, object reference, CancellationToken token)
    {
        int request = 0;
        var queued = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await SendCoreAsync(
                key,
                reference,
                null,
                id =>
                {
                    request = id;
                    applied[id] = queued;
                }
            );
            int command = await queued.Task.WaitAsync(deadline.Token);
            while (Running && (State?.State.CommandId ?? 0) < command)
                await Task.Delay(25, deadline.Token);
            if (!Running)
                throw new IOException("AI 연결이 종료됐습니다.");
            if (State?.State.Message.StartsWith("AI ERROR") == true)
                throw new InvalidDataException("직원 얼굴 기준 적용 실패 · AI 진단을 확인하세요.");
        }
        finally
        {
            applied.TryRemove(request, out _);
        }
    }

    public async Task StopAsync()
    {
        var running = process;
        if (running is null)
            return;
        stopping = true;
        try
        {
            if (!running.HasExited)
            {
                await SendAsync("quit");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await running.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!running.HasExited)
                        running.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            try
            {
                if (!running.HasExited)
                    running.Kill(entireProcessTree: true);
            }
            catch { }
        }

        if (reader is not null)
            await Task.WhenAny(reader, Task.Delay(1000));
        if (errorReader is not null)
            await Task.WhenAny(errorReader, Task.Delay(1000));
        running.Dispose();
        process = null;
        Interlocked.Exchange(ref state, null);
        Interlocked.Exchange(ref live, null);
        while (Notifications.TryDequeue(out _)) { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
