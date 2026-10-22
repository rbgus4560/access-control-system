// 파일 역할: 등록 앱 시작과 진단 모드를 처리합니다. 창 생성 실패는 로그와 메시지로 확인할 수 있게 합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Windows;

namespace FaceEnrollment;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args.Contains("--ui-smoke");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (smoke)
            {
                // Constructs real WPF controls and exercises ownership on Windows, without starting a camera or AI.
                window.VerifySettingsReopen();
                window.Closed += (_, _) => Shutdown(0);
                window.Close();
            }
            else
                ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            string log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessControlSystem",
                "enrollment-startup.log"
            );
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.WriteAllText(log, DateTimeOffset.Now + Environment.NewLine + ex);
            }
            catch (Exception) { }

            if (!smoke)
                MessageBox.Show(
                    "얼굴 등록 창 초기화 실패\n"
                        + ex.GetBaseException().Message
                        + "\n\n오류 기록: "
                        + log,
                    "얼굴 등록 시작 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            Shutdown(1);
        }
    }
}
