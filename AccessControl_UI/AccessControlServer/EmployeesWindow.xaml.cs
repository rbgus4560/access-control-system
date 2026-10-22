// 파일 역할: 관리 서버의 직원 목록·기본 정보·활성 상태·출입 권한 편집 화면입니다.
// 기능 기준: 12.4. 서버에서 검증한 접속 설정을 재사용하고 세션 변경 시 작업을 차단합니다.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Npgsql;

namespace AccessControlServer;

public partial class EmployeesWindow : Window
{
    private readonly DatabaseEndpoint endpoint;
    private string sessionPassword;
    private readonly Func<bool> sessionIsCurrent;
    private readonly CancellationTokenSource lifetime = new();
    private EmployeeRow? editing;
    private bool busy;
    private bool closing;

    public EmployeesWindow(DatabaseEndpoint endpoint, string password, Func<bool> sessionIsCurrent)
    {
        InitializeComponent();
        this.endpoint = endpoint;
        sessionPassword = password;
        this.sessionIsCurrent = sessionIsCurrent;
        EndpointLabel.Text =
            $"{endpoint.Host}:{endpoint.Port} / {endpoint.Database} / {endpoint.Username}";
        Closed += (_, _) =>
        {
            closing = true;
            lifetime.Cancel();
            lifetime.Dispose();
            sessionPassword = "";
        };
        Loaded += (_, _) => Search(this, new RoutedEventArgs());
    }

    private void SetBusy(bool value)
    {
        busy = value;
        SearchArea.IsEnabled = !value;
        EditArea.IsEnabled = !value;
        SaveButton.IsEnabled = !value;
        Employees.IsEnabled = !value;
    }

    private void Populate(EmployeeRow? row)
    {
        editing = row;
        EmployeeNoInput.Text = row?.EmployeeNo ?? "";
        NameInput.Text = row?.Name ?? "";
        DepartmentInput.Text = row?.Department ?? "";
        PositionInput.Text = row?.Position ?? "";
        ActiveInput.IsChecked = row?.IsActive ?? true;
        AccessInput.IsChecked = row?.AccessEnabled ?? false;
        EditTitle.Text = row == null ? "신규 직원" : "선택 직원 수정";
        SaveButton.Content = row == null ? "신규 직원 저장" : "변경사항 저장";
        UpdatedLabel.Text =
            row == null ? "" : $"마지막 수정: {row.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private void NewEmployee(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        Employees.SelectedItem = null;
        Populate(null);
        StatusLabel.Text = "신규 직원 입력 · 출입 허용 기본값은 해제입니다.";
    }

    private void SelectEmployee(object sender, SelectionChangedEventArgs e)
    {
        if (!busy && Employees.SelectedItem is EmployeeRow row)
            Populate(row);
    }

    private async void Search(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        if (!sessionIsCurrent())
        {
            StatusLabel.Text = "서버 설정에서 DB를 연결한 뒤 직원 관리를 다시 열어 주세요.";
            return;
        }
        SetBusy(true);
        StatusLabel.Text = "직원 목록 조회 중";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var rows = await EmployeeService.ListAsync(
                endpoint,
                sessionPassword,
                SearchInput.Text,
                cts.Token
            );
            if (closing)
                return;
            Employees.ItemsSource = rows;
            Employees.SelectedItem = null;
            Populate(null);
            StatusLabel.Text = $"조회 성공 · {rows.Count}명 (최대 500명) · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                Employees.ItemsSource = null;
                Populate(null);
                StatusLabel.Text = Error(ex);
            }
        }
        finally
        {
            if (!closing)
                SetBusy(false);
        }
    }

    private async void SaveEmployee(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        if (!sessionIsCurrent())
        {
            StatusLabel.Text = "DB 연결이 변경됐습니다. 직원 관리 창을 다시 열어 주세요.";
            return;
        }
        if (
            string.IsNullOrWhiteSpace(EmployeeNoInput.Text)
            || string.IsNullOrWhiteSpace(NameInput.Text)
        )
        {
            StatusLabel.Text = "사원번호와 이름을 입력해 주세요.";
            return;
        }

        SetBusy(true);
        StatusLabel.Text = "DB에 직원 정보 저장 중";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        EmployeeRow? saved = null;
        try
        {
            saved = await EmployeeService.SaveAsync(
                endpoint,
                sessionPassword,
                editing,
                EmployeeNoInput.Text,
                NameInput.Text,
                DepartmentInput.Text,
                PositionInput.Text,
                ActiveInput.IsChecked == true,
                AccessInput.IsChecked == true,
                cts.Token
            );
            if (closing)
                return;
            // Keep returned ID/version even if refreshing the grid subsequently fails.
            Populate(saved);
            SearchInput.Text = "";
            var rows = await EmployeeService.ListAsync(
                endpoint,
                sessionPassword,
                "",
                cts.Token
            );
            if (closing)
                return;
            Employees.ItemsSource = rows;
            Employees.SelectedItem = rows.Find(x => x.Id == saved.Id);
            StatusLabel.Text =
                $"저장 및 재조회 완료 · {saved.EmployeeNo} · {rows.Count}명 (최대 500명)";
        }
        catch (Exception ex)
        {
            if (!closing)
                StatusLabel.Text =
                    saved == null
                        ? Error(ex) + " 저장 결과가 불확실하면 사원번호로 다시 조회해 확인하세요."
                        : "직원 저장은 완료됐으나 목록 갱신에 실패했습니다. 검색·새로고침으로 확인하세요.";
        }
        finally
        {
            if (!closing)
                SetBusy(false);
        }
    }

    private static string Error(Exception ex) =>
        ex switch
        {
            PostgresException p when p.SqlState == "23505" =>
                "같은 사원번호가 이미 있습니다. 기존 직원을 선택하거나 번호를 변경하세요.",
            PostgresException p when p.SqlState == "28P01" => "DB 계정 또는 비밀번호를 확인하세요.",
            PostgresException p when p.SqlState == "42P01" || p.SqlState == "3F000" =>
                "대상 DB의 access_control.persons 테이블을 확인하세요.",
            PostgresException p when p.SqlState == "42501" => "직원 조회·저장 권한이 없습니다.",
            OperationCanceledException => "작업 시간 초과 또는 취소입니다.",
            InvalidOperationException i => i.Message,
            _ => "DB 작업 실패 · PostgreSQL 실행 상태와 접속 정보를 확인하세요."
        };
}
