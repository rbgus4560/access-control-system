using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AccessControl.Network;

namespace AccessControlServer;

// 영상만 반전하고, 실제 Uniform 영상 사각형 위에 글씨를 정상 방향으로 배치합니다.
public sealed class CameraTile : Border
{
    private string cameraCode;
    public event Action? SlotClicked;
    private string cameraName;
    private readonly Grid viewport = new() { ClipToBounds = true };
    private readonly Image image = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(-1, 1) };
    private readonly Grid overlay = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock time = OverlayText(12, HorizontalAlignment.Left, VerticalAlignment.Top);
    private readonly TextBlock name = OverlayText(12, HorizontalAlignment.Right, VerticalAlignment.Bottom);
    private readonly TextBlock result = OverlayText(15, HorizontalAlignment.Right, VerticalAlignment.Bottom);
    private readonly TextBlock waiting = new() { Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "영상 미연결" };
    private readonly TextBlock statusName = new() { Foreground = Brushes.LightSlateGray, FontSize = 10, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border cameraStatus = new() { Background = Brushes.Transparent, Padding = new Thickness(4, 2, 4, 2) };
    private readonly Border serverStatus = new() { Background = Brushes.Transparent, Padding = new Thickness(4, 2, 4, 2) };
    private readonly Border statusHint = new() { Background = Brushes.WhiteSmoke, Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false, MaxWidth = 250, Margin = new Thickness(5) };
    private readonly TextBlock hintText = new() { Foreground = Brushes.Black, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Ellipse cameraLight = Light();
    private readonly Ellipse serverLight = Light();
    private Window? expandedWindow;
    private CameraTile? expandedTile;
    private ImageSource? source;
    private bool cameraConnected, serverConnected;
    private AccessDecision? decision;
    private Guid? shownEvent;
    private long shownAt;

    public CameraTile(string code, string label, bool expandable = true)
    {
        cameraCode = code;
        MouseLeftButtonDown += (_, e) => { SlotClicked?.Invoke(); e.Handled = true; };
        cameraName = label;
        BorderBrush = new SolidColorBrush(Color.FromRgb(64, 76, 94));
        BorderThickness = new Thickness(.5);
        Background = new SolidColorBrush(Color.FromRgb(20, 32, 51));
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        viewport.Children.Add(image);
        viewport.Children.Add(waiting);
        overlay.Children.Add(time);
        overlay.Children.Add(name);
        overlay.Children.Add(result);
        viewport.Children.Add(overlay);
        if (expandable)
        {
            var expand = new Button
            {
                Content = new TextBlock { Text = "\uE740", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15 },
                Foreground = Brushes.White, Width = 28, Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3), ToolTip = "카메라 확대", Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            // 테마의 기본 버튼 테두리를 제거합니다.
            var factory = new FrameworkElementFactory(typeof(ContentPresenter));
            factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            expand.Template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
            System.Windows.Automation.AutomationProperties.SetName(expand, code + " 확대");
            expand.Click += (_, _) => OpenExpanded();
            viewport.Children.Add(expand);
        }
        root.Children.Add(viewport);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 8, 5) };
        statusName.Text = label;
        status.Children.Add(statusName);
        var cameraItems = new StackPanel { Orientation = Orientation.Horizontal };
        cameraItems.Children.Add(new TextBlock { Text = "\uE714", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        cameraItems.Children.Add(cameraLight);
        cameraStatus.Child = cameraItems;
        status.Children.Add(cameraStatus);
        var serverItems = new StackPanel { Orientation = Orientation.Horizontal };
        serverItems.Children.Add(new TextBlock { Text = "\uE968", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        serverItems.Children.Add(serverLight);
        serverStatus.Child = serverItems;
        status.Children.Add(serverStatus);
        statusHint.Child = hintText;
        viewport.Children.Add(statusHint);
        cameraStatus.MouseEnter += (_, _) => ShowHint(cameraConnected ? "영상 수신 중" : "영상 미수신", cameraStatus);
        serverStatus.MouseEnter += (_, _) => ShowHint(serverConnected ? "클라이언트 연결" : "클라이언트 미연결", serverStatus);
        cameraStatus.MouseLeave += (_, _) => statusHint.Visibility = Visibility.Collapsed;
        serverStatus.MouseLeave += (_, _) => statusHint.Visibility = Visibility.Collapsed;
        Grid.SetRow(status, 1);
        var statusBar = new Border { Background = new SolidColorBrush(Color.FromRgb(40, 56, 76)), Child = status };
        Grid.SetRow(statusBar, 1);
        root.Children.Add(statusBar);
        Child = root;
        viewport.SizeChanged += (_, _) => LayoutOverlay();
        Refresh(null, false, false, null);
    }

    public void AssignCamera(string code, string label)
    {
        if (cameraCode != code)
        {
            CloseExpanded();
            shownEvent = null;
            source = null;
            image.Source = null;
        }
        cameraCode = code;
        SetDisplayName(label);
    }

    private void ShowHint(string text, FrameworkElement target)
    {
        // 아이콘 위치를 타일 좌표로 변환해 설명을 바로 위에 배치합니다.
        hintText.Text = text;
        statusHint.Visibility = Visibility.Visible;
        var point = target.TranslatePoint(new Point(0, 0), this);
        statusHint.HorizontalAlignment = HorizontalAlignment.Left;
        statusHint.Measure(new Size(Math.Max(80, ActualWidth - 10), double.PositiveInfinity));
        double left = Math.Clamp(point.X, 4, Math.Max(4, ActualWidth - statusHint.DesiredSize.Width - 4));
        statusHint.Margin = new Thickness(left, 0, 4, 5);
    }

    private static Ellipse Light() => new() { Width = 8, Height = 8, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.Firebrick };
    private static TextBlock OverlayText(double size, HorizontalAlignment horizontal, VerticalAlignment vertical) => new()
    {
        FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
        Margin = new Thickness(7), HorizontalAlignment = horizontal, VerticalAlignment = vertical,
        TextWrapping = TextWrapping.Wrap,
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 }
    };

    public void SetDisplayName(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        cameraName = new string(label.Where(c => !char.IsControl(c)).Take(60).ToArray());
        name.Text = cameraName;
        statusName.Text = cameraConnected && source is not null ? cameraName : "빈 화면";
        if (expandedWindow is not null) expandedWindow.Title = cameraName + " · 확대 관제";
        expandedTile?.SetDisplayName(cameraName);
    }

    public void Refresh(ImageSource? frame, bool cameraReady, bool serverReady, AccessDecision? access)
    {
        source = frame; cameraConnected = cameraReady; serverConnected = serverReady; decision = access;
        image.Source = frame;
        waiting.Visibility = frame is null ? Visibility.Visible : Visibility.Collapsed;
        cameraLight.Fill = cameraReady ? Brushes.LimeGreen : Brushes.Firebrick;
        serverLight.Fill = serverReady ? Brushes.LimeGreen : Brushes.Firebrick;
        cameraLight.ToolTip = null;
        serverLight.ToolTip = null;
        waiting.Text = string.IsNullOrEmpty(cameraCode) ? "카메라를 선택한 뒤 이 화면을 클릭하세요" : cameraName + " · 영상 미수신";
        time.Text = DateTime.Now.ToString("yyyy-MM-dd'- 'HH:mm:ss");
        name.Text = cameraName;
        statusName.Text = cameraConnected && source is not null ? cameraName : "빈 화면";
        if (access is { Recorded: true, EventId: { } id, Result: "GRANTED" or "DENIED" } && cameraReady && serverReady)
        {
            if (id != shownEvent)
            {
                shownEvent = id; shownAt = Stopwatch.GetTimestamp();
                result.Text = (access.Result == "GRANTED" ? "출입 허가" : "출입 거부") + " · " + access.PersonName;
                result.Foreground = access.Result == "GRANTED" ? Brushes.LightGreen : Brushes.Salmon;
            }
            double age = Stopwatch.GetElapsedTime(shownAt).TotalSeconds;
            result.Visibility = age < 2.8 ? Visibility.Visible : Visibility.Collapsed;
            result.Opacity = age <= 2.2 ? 1 : Math.Max(0, (2.8 - age) / .6);
        }
        else result.Visibility = Visibility.Collapsed;
        name.Margin = new Thickness(7, 7, 7, result.Visibility == Visibility.Visible ? result.FontSize * 1.5 + 14 : 7);
        LayoutOverlay();
        expandedTile?.Refresh(frame, cameraReady, serverReady, access);
    }

    private void LayoutOverlay()
    {
        if (image.Source is not { } frame || frame.Width <= 0 || frame.Height <= 0)
        {
            overlay.Visibility = Visibility.Collapsed;
            return;
        }
        double scale = Math.Min(viewport.ActualWidth / frame.Width, viewport.ActualHeight / frame.Height);
        overlay.Width = Math.Max(0, frame.Width * scale);
        overlay.Height = Math.Max(0, frame.Height * scale);
        overlay.Visibility = Visibility.Visible;
    }

    public Window OpenExpanded()
    {
        if (expandedWindow is not null)
        {
            if (expandedWindow.WindowState == WindowState.Minimized) expandedWindow.WindowState = WindowState.Normal;
            expandedWindow.Activate(); return expandedWindow;
        }
        expandedTile = new CameraTile(cameraCode, cameraName, false);
        expandedTile.time.FontSize = 21; expandedTile.name.FontSize = 18; expandedTile.result.FontSize = 26;
        expandedWindow = new Window
        {
            Title = cameraName + " · 확대 관제", Width = 1000, Height = 760, MinWidth = 600, MinHeight = 440,
            Owner = Window.GetWindow(this), Content = expandedTile, Background = Background,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Window.GetWindow(this)?.Icon
        };
        expandedWindow.Closed += (_, _) => { expandedTile = null; expandedWindow = null; };
        expandedTile.Refresh(source, cameraConnected, serverConnected, decision);
        expandedWindow.Show();
        return expandedWindow;
    }

    public void CloseExpanded() => expandedWindow?.Close();
}
