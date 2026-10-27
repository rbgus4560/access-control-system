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
        Loaded += (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--ui-preview")
                && Environment.GetCommandLineArgs().Contains("--employee-lifecycle-preview"))
            {
                var list = Enumerable.Range(1, 7).Select(i => new EmployeeRow(Guid.NewGuid(), $"EMP{i:000}", "시연 직원 " + i, "개발", "직원", true, false, DateTime.UtcNow, DateTime.UtcNow)).ToList();
                if (Environment.GetCommandLineArgs().Contains("--employee-pending-preview"))
                    list[0] = list[0] with { DeletedAt = DateTime.UtcNow, DeleteAfter = DateTime.UtcNow.AddDays(7), IsActive = false };
                Employees.ItemsSource = list;
                Employees.SelectedItem = list[0];
                StatusLabel.Text = "화면 미리보기 · 합성 직원 데이터";
            }
            else Search(this, new RoutedEventArgs());
        };
    }

    private void SetBusy(bool value)
    {
        busy = value;
        SearchArea.IsEnabled = !value;
        EditArea.IsEnabled = !value && editing?.DeletedAt is null;
        SaveButton.IsEnabled = !value && editing?.DeletedAt is null;
        DeleteButton.IsEnabled = !value && editing is not null && editing.PurgedAt is null;
        RestoreButton.IsEnabled = !value && editing?.DeletedAt is not null;
        Employees.IsEnabled = !value;
    }

    private void Populate(EmployeeRow? row)
    {
        editing = row;
        bool pending = row?.DeletedAt is not null;
        DeleteButton.Content = pending ? "빠른 삭제" : "직원 삭제";
        DeleteButton.IsEnabled = !busy && row is not null && row.PurgedAt is null;
        RestoreButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.IsEnabled = !busy && pending;
        SaveButton.IsEnabled = !busy && !pending;
        SaveButton.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        EditArea.IsEnabled = !busy && !pending;
        DeletionLabel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        if (pending)
        {
            var due = row!.DeleteAfter ?? row.DeletedAt!.Value.AddDays(EmployeeDeletionService.GraceDays);
            int days = Math.Max(0, (int)Math.Ceiling((due.ToUniversalTime() - DateTime.UtcNow).TotalDays));
            DeletionLabel.Text = $"삭제 대기 · 유예기간 {days}일 남음\n예정일 {due.ToLocalTime():yyyy-MM-dd HH:mm} · 정보 복원 가능";
        }
        UpdatedLabel.Text = "";
        EmployeeNoInput.Text = row?.EmployeeNo ?? "";
        NameInput.Text = row?.Name ?? "";
        DepartmentInput.Text = row?.Department ?? "";
        PositionInput.Text = row?.Position ?? "";
        ActiveInput.IsChecked = row?.IsActive ?? true;
        AccessInput.IsChecked = row?.AccessEnabled ?? false;
        EditTitle.Text = row == null ? "신규 직원" : "선택 직원 수정";
        SaveButton.Content = row == null ? "신규 직원 저장" : "변경사항 저장";
        UpdatedLabel.Text = "";
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
                cts.Token, true
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
                cts.Token, true
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

    private async void DeleteEmployee(object sender, RoutedEventArgs e)
    {
        if (busy || editing is not { } selected) return;
        if (selected.DeletedAt is not null) { await PurgeEmployeeAsync(selected); return; }
        if (!sessionIsCurrent()) { MessageBox.Show(this, "DB 연결이 변경됐습니다. 직원 관리 창을 다시 열어 주세요.", "DB 연결 확인"); return; }
        if (MessageBox.Show(this, $"{selected.EmployeeNo} · {selected.Name} 직원을 삭제 대기로 전환할까요?\n유예기간은 7일입니다. 출입 권한과 얼굴 등록을 중지하고 정보를 보존하므로 복원할 수 있습니다. 과거 출입 기록은 유지됩니다.",
            "직원 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        bool deleted = false;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await EmployeeService.DeleteAsync(endpoint, sessionPassword, selected, cancel.Token);
            deleted = true;
            if (closing) return;
            var list = await EmployeeService.ListAsync(endpoint, sessionPassword, SearchInput.Text, cancel.Token, true);
            if (closing) return;
            Employees.ItemsSource = list;
            var updated = list.Find(x => x.Id == selected.Id);
            Employees.SelectedItem = updated;
            Populate(updated);
            StatusLabel.Text = "삭제 대기로 전환했습니다. 정보를 복원할 수 있으며 과거 기록은 유지됩니다.";
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                string detail = deleted
                    ? "삭제 대기 전환은 완료됐으나 목록 갱신에 실패했습니다. 검색·새로고침을 눌러 주세요."
                    : Error(ex);
                MessageBox.Show(this, detail, "직원 삭제 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { if (!closing) SetBusy(false); }
    }

    private async Task PurgeEmployeeAsync(EmployeeRow selected)
    {
        if (!sessionIsCurrent()) return;
        if (MessageBox.Show(this, $"{selected.EmployeeNo} · {selected.Name}\n이 직원의 사원번호·이름·부서·직급과 모든 얼굴 특징을 DB에서 영구 삭제할까요?\n복원할 수 없습니다. 출입 기록은 삭제된 직원으로 보존됩니다.", "직원 정보 영구 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await EmployeeDeletionService.PurgeAsync(endpoint, sessionPassword, selected, cancel.Token);
            if (closing) return;
            Employees.ItemsSource = await EmployeeService.ListAsync(endpoint, sessionPassword, SearchInput.Text, cancel.Token, true);
            Populate(null);
            MessageBox.Show(this, "직원 정보와 얼굴 특징을 영구 삭제했습니다. 출입 기록은 보존됩니다.", "삭제 완료");
        }
        catch (Exception ex) { if (!closing) MessageBox.Show(this, Error(ex) + "\nDatabase/18_employee_purge.sql 적용 여부와 최신 목록을 확인해 주세요.", "삭제 결과 확인"); }
        finally { if (!closing) SetBusy(false); }
    }

    private async void RestoreEmployee(object sender, RoutedEventArgs e)
    {
        if (busy || editing is not { DeletedAt: not null } selected) return;
        if (!sessionIsCurrent()) { MessageBox.Show(this, "DB 연결이 변경됐습니다. 직원 관리 창을 다시 열어 주세요.", "DB 연결 확인"); return; }
        if (MessageBox.Show(this, "직원 정보를 복원할까요? 출입 권한은 자동 허용하지 않습니다. 복원 후 권한을 확인하고 얼굴을 재등록해 주세요.", "정보 복원", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        bool restored = false;
        try
        {
            await EmployeeDeletionService.ChangeAsync(endpoint, sessionPassword, selected, "restore", cancel.Token);
            restored = true;
            var list = await EmployeeService.ListAsync(endpoint, sessionPassword, SearchInput.Text, cancel.Token, true);
            if (closing) return;
            Employees.ItemsSource = list;
            var updated = list.Find(x => x.Id == selected.Id);
            Employees.SelectedItem = updated;
            Populate(updated);
            StatusLabel.Text = "직원 정보 복원 완료 · 활성/출입 권한 확인과 얼굴 재등록이 필요합니다.";
        }
        catch (Exception ex) { if (!closing) StatusLabel.Text = restored ? "복원은 완료됐으나 목록 갱신에 실패했습니다. 새로고침해 주세요." : Error(ex); }
        finally { if (!closing) SetBusy(false); }
    }

    private static string Error(Exception ex) =>
        ex switch
        {
            PostgresException p when p.SqlState == "23505" =>
                "같은 사원번호가 이미 있습니다. 기존 직원을 선택하거나 번호를 변경하세요.",
            PostgresException p when p.SqlState == "28P01" => "DB 계정 또는 비밀번호를 확인하세요.",
            PostgresException p when p.SqlState == "42P01" || p.SqlState == "3F000" =>
                "대상 DB의 access_control.persons 테이블을 확인하세요.",
            PostgresException p when p.SqlState == "42501" => "직원 조회·저장·삭제에 필요한 DB 권한이 없습니다.",
            PostgresException p when p.SqlState == "42703" => "삭제 기능에 필요한 DB 필드가 없습니다. Database/17_employee_delete_lifecycle.sql과 18_employee_purge.sql을 해당 DB에 적용해 주세요.",
            OperationCanceledException => "작업 시간 초과 또는 취소입니다.",
            InvalidOperationException i => i.Message,
            _ => "DB 작업 실패 · PostgreSQL 실행 상태와 접속 정보를 확인하세요."
        };
}
