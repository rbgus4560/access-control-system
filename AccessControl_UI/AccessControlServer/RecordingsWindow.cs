// 파일 역할: DB 녹화 구간 목록과 상태를 보여 주고 선택 구간의 저장 위치 확인을 지원합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AccessControl.Faces;

namespace AccessControlServer;

public sealed class RecordingsWindow : Window
{
    private readonly Func<FaceDatabase?> database;
    private readonly CancellationTokenSource stop = new();
    private readonly DataGrid grid = new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = true,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single
    };
    private readonly TextBlock status = new()
    {
        Margin = new Thickness(8),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button refresh = new()
    {
        Content = "녹화 구간 DB 조회",
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(4)
    };
    private bool busy;

    public RecordingsWindow(Func<FaceDatabase?> database)
    {
        this.database = database;
        Title = "녹화 구간 목록 · 서버";
        Width = 1100;
        Height = 600;
        MinWidth = 800;
        MinHeight = 400;
        FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic");
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(
            new TextBlock
            {
                Text = "CAM-01 녹화 구간 · 최근 500건",
                FontSize = 22,
                FontWeight = FontWeights.Bold
            }
        );
        top.Children.Add(
            new TextBlock
            {
                Text =
                    "RECORDING: 녹화 중 / READY: 완료 원본 / FAILED: 확인 필요 / DELETED: 72시간 보관 정리. 삭제시각·사유는 오른쪽 열에서 확인합니다.",
                Margin = new Thickness(0, 8, 0, 4),
                TextWrapping = TextWrapping.Wrap
            }
        );
        var buttons = new WrapPanel();
        top.Children.Add(buttons);
        buttons.Children.Add(refresh);
        refresh.Click += async (_, _) => await Load();
        var folder = new Button
        {
            Content = "선택 구간 폴더 열기",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(4)
        };
        buttons.Children.Add(folder);
        folder.Click += (_, _) => OpenFolder();
        top.Children.Add(status);
        root.Children.Add(grid);
        Loaded += async (_, _) => await Load();
        Closed += (_, _) =>
        {
            stop.Cancel();
            if (!busy)
                stop.Dispose();
        };
    }

    private async Task Load()
    {
        if (busy)
            return;
        var db = database();
        if (db is null)
        {
            status.Text = "서버 DB 연결을 먼저 해 주세요.";
            return;
        }

        busy = true;
        refresh.IsEnabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(15000);
        try
        {
            var rows = await db.RecordingsAsync(timeout.Token);
            if (stop.IsCancellationRequested)
                return;
            if (!ReferenceEquals(db, database()))
            {
                grid.ItemsSource = null;
                status.Text = "DB 연결이 변경됐습니다. 다시 조회하세요.";
                return;
            }

            grid.ItemsSource = rows;
            status.Text = $"조회 {rows.Count}건 · {DateTime.Now:HH:mm:ss} · 자동 갱신 아님";
        }
        catch (Exception)
        {
            if (!stop.IsCancellationRequested)
                status.Text =
                    "녹화 목록 조회 실패 · DB 연결과 recording_segments 테이블을 확인하세요.";
        }
        finally
        {
            busy = false;
            if (!stop.IsCancellationRequested)
                refresh.IsEnabled = true;
            else
                stop.Dispose();
        }
    }

    private void OpenFolder()
    {
        if (grid.SelectedItem is not RecordingRow row)
        {
            status.Text = "녹화 구간을 선택해 주세요.";
            return;
        }

        if (row.파일위치.StartsWith("client://", StringComparison.Ordinal))
        {
            status.Text =
                "원본은 클라이언트 PC에 있습니다. 서버의 타임라인·클립에서 시각을 선택해 요청하세요.";
            return;
        }

        try
        {
            string path = Path.GetFullPath(row.파일위치);
            string prefix = Path.GetFullPath(RecordingFiles.Root) + Path.DirectorySeparatorChar;
            if (
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(Path.GetDirectoryName(path))
            )
                throw new IOException();
            Process.Start(
                new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true }
            );
        }
        catch (Exception)
        {
            status.Text = "현재 PC에서 해당 녹화 폴더를 찾을 수 없습니다.";
        }
    }
}
