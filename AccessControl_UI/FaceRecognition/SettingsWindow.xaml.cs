// 파일 역할: 서버 연결·카메라·녹화 설정 패널을 별도 창에서 보여 줍니다. 패널 소유권을 원래 화면으로 돌려줍니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;

namespace FaceRecognition;

public partial class SettingsWindow : Window
{
    public SettingsWindow(UIElement connection, UIElement camera, UIElement recording)
    {
        InitializeComponent();
        ConnectionHost.Content = connection;
        CameraHost.Content = camera;
        RecordingHost.Content = recording;
        Closed += (_, _) =>
        {
            ConnectionHost.Content = null;
            CameraHost.Content = null;
            RecordingHost.Content = null;
        };
    }
}
