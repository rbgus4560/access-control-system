// 파일 역할: 이전 1:1 확인용 직원 목록 창입니다. 현재 1:N 인증 메인 화면에서는 이 창을 호출하지 않습니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;
using System.Windows.Controls;
using AccessControl.Faces;

namespace FaceRecognition;

public partial class UsersWindow : Window
{
    private List<FaceEmployee> people = [];
    private readonly Action<FaceEmployee> select;
    private readonly CancellationTokenSource lifetime = new();
    private bool closing,
        busy;

    public UsersWindow(UIElement referenceControls, Action<FaceEmployee> selected)
    {
        InitializeComponent();
        ReferenceHost.Content = referenceControls;
        select = selected;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            closing = true;
            lifetime.Cancel();
            ReferenceHost.Content = null;
            lifetime.Dispose();
        };
    }

    private void Filter(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var q = Search.Text.Trim();
        Users.ItemsSource = people
            .Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.EmployeeNo.Contains(q, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
    }

    private async void Reload(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (busy)
            return;
        busy = true;
        ReloadButton.IsEnabled = false;
        try
        {
            var reply = await FaceApi.CallAsync(new FaceRequest(), lifetime.Token);
            if (closing)
                return;
            people = reply.Employees ?? [];
            Refresh();
            Status.Text =
                $"DB 직원 {people.Count}명 · 직원 정보/권한 수정 후 목록을 새로고침하세요.";
        }
        catch (Exception ex)
        {
            if (!closing)
                Status.Text = "조회 실패: " + ex.Message;
        }
        finally
        {
            busy = false;
            if (!closing)
                ReloadButton.IsEnabled = true;
        }
    }

    private void SelectEmployee(object sender, SelectionChangedEventArgs e)
    {
        if (Users.SelectedItem is FaceEmployee person)
            select(person);
    }
}
