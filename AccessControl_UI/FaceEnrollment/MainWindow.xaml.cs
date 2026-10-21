// 파일 역할: 서버의 직원을 선택해 회전 등록을 진행하고, 완성된 얼굴 특징을 선택 직원의 새 등록으로 저장합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AccessControl.Ai;
using AccessControl.Faces;

namespace FaceEnrollment;

public partial class MainWindow : Window
{
    private readonly FaceAiPanel panel = new("enrollment");
    private readonly CancellationTokenSource lifetime = new();
    private bool closing,
        busy;
    private EnrollmentSettingsWindow? settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        AiHost.Content = panel;
        EnrollmentControlsHost.Content = panel.EnrollmentControls;
        panel.StateUpdated += (state, fresh) =>
        {
            bool ready = fresh && state.ReferenceCount is 5 or 9 && !state.Enrolling && !busy;
            SaveFace.IsEnabled =
                ready && FaceEmployees.SelectedItem is FaceEmployee { Active: true };
        };
    }

    internal void VerifySettingsReopen()
    {
        for (int i = 0; i < 2; i++)
        {
            ShowSettings(this, new RoutedEventArgs());
            settingsWindow!.Close();
        }
    }

    private void ShowSettings(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is not null)
        {
            if (settingsWindow.WindowState == WindowState.Minimized)
                settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            return;
        }

        settingsWindow = new EnrollmentSettingsWindow(panel.OperationsView) { Owner = this };
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    private void SelectEmployee(object sender, SelectionChangedEventArgs e)
    {
        Employee.IsReadOnly = PersonName.IsReadOnly = true;
        if (FaceEmployees.SelectedItem is not FaceEmployee)
        {
            Employee.Clear();
            PersonName.Clear();
        }

        if (FaceEmployees.SelectedItem is FaceEmployee person)
        {
            Employee.Text = person.EmployeeNo;
            PersonName.Text = person.Name;
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Employee.IsEnabled =
            PersonName.IsEnabled =
            FaceEmployees.IsEnabled =
            RefreshEmployees.IsEnabled =
                !value;
        SaveFace.IsEnabled = false;
    }

    private async void LoadEmployees(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        SetBusy(true);
        try
        {
            var reply = await FaceApi.CallAsync(new FaceRequest(), lifetime.Token);
            if (closing)
                return;
            FaceEmployees.ItemsSource = reply.Employees;
            FaceEmployees.SelectedIndex = -1;
            Status.Text =
                $"DB 직원 {reply.Employees?.Count ?? 0}명 조회 · 얼굴을 등록할 직원을 선택하세요.";
        }
        catch (Exception ex)
        {
            if (!closing)
                Status.Text = "직원 조회 실패: " + ex.Message;
        }
        finally
        {
            if (!closing)
                SetBusy(false);
        }
    }

    private async void SaveDatabase(object sender, RoutedEventArgs e)
    {
        if (busy || FaceEmployees.SelectedItem is not FaceEmployee person)
            return;
        if (
            person.EnrollmentId is not null
            && MessageBox.Show(
                "이 직원의 기존 얼굴 등록을 새 자동 선택 샘플로 교체합니다. 기존 등록 이력은 보존됩니다.",
                "얼굴 재등록",
                MessageBoxButton.YesNo
            ) != MessageBoxResult.Yes
        )
            return;
        SetBusy(true);
        try
        {
            Status.Text = "자동 선택한 특징 준비 및 DB 저장 중…";
            var reference = await panel.ExportReferenceAsync(lifetime.Token);
            var reply = await FaceApi.CallAsync(
                new FaceRequest
                {
                    Operation = "save",
                    PersonId = person.PersonId,
                    ExpectedEnrollmentId = person.EnrollmentId,
                    Reference = reference
                },
                lifetime.Token
            );
            if (closing)
                return;
            var face =
                reply.Face ?? throw new InvalidOperationException("서버 저장 응답이 없습니다.");
            if (face.PersonId != person.PersonId || face.EnrollmentId == Guid.Empty)
                throw new InvalidOperationException("저장된 직원 응답이 일치하지 않습니다.");
            FaceEmployees.ItemsSource = new[] { person with { EnrollmentId = face.EnrollmentId } };
            FaceEmployees.SelectedIndex = 0;
            Status.Text =
                $"DB 저장 완료 · {face.EmployeeNo} · {face.Name} · {FaceApi.ReferencePoses(face.Reference).Length}/{FaceApi.ReferencePoses(face.Reference).Length}. 클라이언트에 새 얼굴 기준이 자동 동기화됩니다.";
        }
        catch (Exception ex)
        {
            if (!closing)
                Status.Text = "DB 저장 실패: " + ex.Message;
        }
        finally
        {
            if (!closing)
                SetBusy(false);
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing)
            return;
        e.Cancel = true;
        closing = true;
        lifetime.Cancel();
        settingsWindow?.Close();
        await panel.DisposeAsync();
        lifetime.Dispose();
        Close();
    }
}
