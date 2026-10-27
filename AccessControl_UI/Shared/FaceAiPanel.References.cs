// 등록 얼굴 기준의 가져오기·내보내기와 갤러리 적용.

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

}
