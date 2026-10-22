using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AccessControl.Ai;

// 등록 전용 안내 장식입니다. 영상·특징·각도 판정에는 영향을 주지 않습니다.
public sealed class EnrollmentMotionGuide : Border
{
    private readonly TextBlock arrow = new()
    {
        FontSize = 38, Foreground = Brushes.LightSkyBlue,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransform = new TranslateTransform()
    };
    private string? stage;

    public EnrollmentMotionGuide()
    {
        Width = 76;
        Height = 64;
        Margin = new Thickness(0, 0, 16, 0);
        IsHitTestVisible = false;
        Child = arrow;
        Visibility = Visibility.Collapsed;
        Unloaded += (_, _) => ShowStage(null);
    }

    public void ShowStage(string? value)
    {
        if (stage == value) return;
        stage = value;
        var transform = (TranslateTransform)arrow.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        arrow.BeginAnimation(OpacityProperty, null);
        transform.X = transform.Y = 0;
        Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        arrow.Text = value switch
        {
            "LEFT" => "←", "RIGHT" => "→", "UP" => "↑", "DOWN" => "↓", _ => "◎"
        };
        if (value is null) return;
        // 40ms 화면 갱신마다 다시 시작하지 않고 방향이 바뀔 때만 애니메이션을 갱신합니다.
        var animation = new DoubleAnimation(0, value is "LEFT" or "UP" ? -9 : 9,
            TimeSpan.FromSeconds(.75))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        if (value is "LEFT" or "RIGHT")
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        else if (value is "UP" or "DOWN")
            transform.BeginAnimation(TranslateTransform.YProperty, animation);
        else
            arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, TimeSpan.FromSeconds(.75))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
}
