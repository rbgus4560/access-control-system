// 파일 역할: 관리자 계정을 준비하고 얼굴 등록을 저장할 관리자 정보를 선택합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;
using System.Windows.Controls;

namespace AccessControlServer;

// The schema requires an actual administrator as enrollment author.
public sealed class AdminSetupWindow : Window
{
    public Guid CreatedId { get; private set; }

    private readonly CancellationTokenSource lifetime = new();

    public AdminSetupWindow(FaceDatabase db)
    {
        Title = "얼굴 등록 관리자 추가";
        Width = 460;
        Height = 390;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = panel;
        panel.Children.Add(
            new TextBlock
            {
                Text =
                    "얼굴 등록 이력에 기록할 관리자 계정입니다.\nDB 접속 비밀번호와 별도로 설정합니다.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            }
        );
        var login = new TextBox { MaxLength = 50 };
        var name = new TextBox { MaxLength = 100 };
        var password = new PasswordBox();
        foreach (
            var item in new (string Label, Control Input)[]
            {
                ("관리자 ID", login),
                ("이름", name),
                ("관리자 비밀번호 (10자 이상)", password)
            }
        )
        {
            panel.Children.Add(
                new TextBlock { Text = item.Label, Margin = new Thickness(0, 5, 0, 3) }
            );
            panel.Children.Add(item.Input);
        }

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var save = new Button
        {
            Content = "관리자 저장",
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 14, 0, 0)
        };
        panel.Children.Add(save);
        panel.Children.Add(status);
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var admin = await db.CreateAdminAsync(
                    login.Text,
                    name.Text,
                    password.Password,
                    lifetime.Token
                );
                CreatedId = admin.Id;
                password.Clear();
                DialogResult = true;
            }
            catch (InvalidOperationException ex)
            {
                status.Text = ex.Message;
            }
            catch (Exception)
            {
                status.Text = "저장 실패 · 관리자 ID 중복 또는 DB 상태를 확인해 주세요.";
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            password.Clear();
        };
    }
}
