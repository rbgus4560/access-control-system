using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AccessControl.Preview;

// 개발용 화면 검토 모드입니다. 카메라·DB·통신을 시작하지 않은 실제 WPF 창을 렌더링합니다.
public static class UiPreviewCapture
{
    public static void Queue(Window window)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--ui-preview")) return;
        int index = Array.IndexOf(args, "--capture-output");
        if (index < 0 || index + 1 >= args.Length) return;
        if (args.Contains("--compact-preview"))
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                window.UpdateLayout();
                var content = window.Content as FrameworkElement;
                double width = content is null ? window.ActualWidth : content.ActualWidth + content.Margin.Left + content.Margin.Right;
                double height = content is null ? window.ActualHeight : content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
                // 창을 렌더링해야 부모 배경색과 자식의 여백이 보존됩니다. 네이티브 창 테두리는 제외합니다.
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.GetFullPath(args[index + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var output = File.Create(path)) encoder.Save(output);
            }
            finally { Application.Current.Shutdown(); }
        };
        timer.Start();
    }
}
