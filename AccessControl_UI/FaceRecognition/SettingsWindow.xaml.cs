// 파일 역할: 서버 연결·카메라·녹화 설정 패널을 별도 창에서 보여 줍니다. 패널 소유권을 원래 화면으로 돌려줍니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Windows;

namespace FaceRecognition;

public partial class SettingsWindow : Window
{
    // 기어 메뉴에서 선택한 설정만 공개합니다. 탭으로 다른 설정을 노출하지 않습니다.
    public void SelectSection(int index)
    {
        index = Math.Clamp(index, 0, 2);
        Section0.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Section1.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Section2.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = new[] { "서버 연결", "카메라·AI", "녹화·보관" }[index];
        Title = "클라이언트 설정 · " + SectionTitle.Text;
    }

    public SettingsWindow(UIElement connection, UIElement camera, UIElement recording)
    {
        InitializeComponent();
        SelectSection(0);
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
