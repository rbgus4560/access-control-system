// DB 세션 연결과 얼굴 API 요청 처리.

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

public partial class MainWindow
{
    private async void LoadDatabase(object sender, RoutedEventArgs e) => await ConnectDatabaseAsync();

    private async Task ConnectDatabaseAsync()
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

        // 입력값을 한 번에 고정하고 기존 직원 창을 닫아 다른 DB의 오래된 편집을 차단합니다.
        var endpoint = new DatabaseEndpoint(HostInput.Text.Trim(), port, DatabaseInput.Text.Trim(), UsernameInput.Text.Trim());
        string password = PasswordInput.Password;
        if (string.IsNullOrEmpty(password) && endpoint == connectedEndpoint)
            password = connectedPassword;
        employeesWindow?.Close();
        connectedEndpoint = null;
        connectedPassword = "";
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
                password,
                cts.Token
            );
            var db = new FaceDatabase(
                new DatabaseEndpoint(
                    HostInput.Text.Trim(),
                    port,
                    DatabaseInput.Text.Trim(),
                    UsernameInput.Text.Trim()
                ),
                password
            );
            var admins = await db.AdminsAsync(cts.Token);
            var history = await db.EventsAsync(cts.Token);
            if (closing || cts.IsCancellationRequested)
                return;
            Volatile.Write(ref faceDatabase, db);
            connectedEndpoint = endpoint;
            connectedPassword = password;
            rows.Clear();
            foreach (var row in history)
                rows.Add(row);
            HistoryStatus.Text = $"DB 최근 {rows.Count}건 조회";
            FaceAdmins.ItemsSource = admins;
            FaceAdmins.SelectedIndex = admins.Count > 0 ? 0 : -1;
            FaceDbStatus.Text =
                admins.Count == 0
                    ? "얼굴 저장 전 관리자 추가를 한 번 진행해 주세요."
                    : "직원·얼굴 요청 대기 중 · 서버 세션 사용 · 자동 연결 저장은 선택 설정에 따름";
            Clients.ItemsSource = snapshot.Clients;
            Cameras.ItemsSource = snapshot.Cameras;
            DeviceCount.Text =
                $"클라이언트 {snapshot.Clients.Count}개 / 카메라 {snapshot.Cameras.Count}개";
            DbStatus.Text = $"조회 성공 · {snapshot.Database} · {DateTime.Now:HH:mm:ss}";
            try
            {
                if (RememberConnection.IsChecked == true)
                {
                    int startupPort = int.TryParse(TcpPort.Text, out int configured) && configured is >= 1024 and <= 65533 ? configured : 5050;
                    startupSettings.Save(new(endpoint, password, startupPort));
                }
                else startupSettings.Forget();
            }
            catch (Exception)
            {
                DbStatus.Text += " · 자동 연결 설정 저장 실패";
            }
            if (!tcpManuallyStopped) await StartTcpAsync();
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
                "서버의 직원·권한 관리 창을 열었습니다. 연결된 DB로 조회·수정할 수 있습니다."
        };
    }

}
