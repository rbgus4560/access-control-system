// 분석 최신성 검증과 화면 상태 갱신.

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

public sealed partial class FaceAiPanel
{
    private void Refresh()
    {
        UpdateVideoOverlay();
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
        {
            analysisView.Visibility = Visibility.Collapsed;
            analysisLabel.Text = "분석 화면 · 새 분석 결과 대기";
            return;
        }
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

        analysisView.Source = analyzed.Source;
        analysisView.Visibility = analysisVisible && fresh && value.Jpeg is not null && analyzed.Source is not null
            ? Visibility.Visible : Visibility.Collapsed;
        analysisLabel.Text = analysisView.Visibility == Visibility.Visible
            ? $"분석 프레임 · {age / 1000:0.00}초 전"
            : "분석 화면 · 새 분석 결과 대기";
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
            $"현재: {PoseName(MirrorPose(state.Pose))} · {(state.AutoEnrollment ? "권장 방향" : "다음 촬영")}: {(state.Expected is null ? "—" : PoseName(MirrorPose(state.Expected)))} · 안정된 분석 {state.Stable}회";
        if (state.GuidedEnrollment)
            direction.Text = $"현재 자세: {PoseName(MirrorPose(state.Pose))} · 움직임에서 샘플 자동 선택";
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
            livePose.Text = "현재 감지 방향: " + (fresh ? PoseName(MirrorPose(state.Pose)) : "분석 대기");
            livePose.Text += " · 방향은 화면 속 얼굴 기준";
            motionGuide.ShowStage(fresh && state.Enrolling && state.Reason == "OK"
                && animateGuide.IsChecked == true ? MirrorPose(state.Expected) : null);
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
            // 전체 진행률은 두 샘플이 선택된 뒤에 오릅니다. 그 전에도 부족한 각도를 안내합니다.
            if (fresh && state.Enrolling && state.GuidedEnrollment && state.Reason == "OK"
                && !string.IsNullOrWhiteSpace(state.EnrollmentHint))
                coverageHint.Text = state.EnrollmentHint;
            coverageHint.Text = coverageHint.Text.Replace("왼쪽", "\u0001")
                .Replace("오른쪽", "화면 왼쪽").Replace("\u0001", "화면 오른쪽");
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
                    + PoseName(MirrorPose(key))
                    + (key is "LEFT" or "RIGHT" ? " (화면 기준)" : "")
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
        if (role == "enrollment")
        {
            enroll.Visibility = state.Enrolling ? Visibility.Collapsed : Visibility.Visible;
            capture.Visibility = state.Enrolling ? Visibility.Visible : Visibility.Collapsed;
        }
        compare.IsEnabled =
            fresh && state.Calibrated && state.ReferenceCount is 5 or 9 && !state.Enrolling;
        StateUpdated?.Invoke(state, fresh);
    }

}
