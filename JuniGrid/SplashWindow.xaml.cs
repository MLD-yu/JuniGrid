using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace JuniGrid;

/// <summary>
/// PCL 风格透明启动窗口：logo + "JuniGrid" 字样逐字描边填充。
/// 描边设置为透明（Stroke=Transparent），仅用来驱动逐字动画的时序，
/// 视觉上只有白色填充按节奏浮现。生命周期由 App 控制：logo 淡入上浮
/// 左移，同时字样填色浮现；全部完成触发 IntroCompleted。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>logo 进场 + 字样填充全部完成时触发。</summary>
    public event Action? IntroCompleted;

    private bool _introDone;
    private Geometry _logoGeo = Geometry.Empty;
    private Rect _bounds;
    private const double LogoSize = 140;
    private const double Gap = 16;

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayIntro();
    }

    private void PlayIntro()
    {
        BuildWordGeometry();

        double stageW = Stage.Width;    // 560（固定，见 XAML）
        double stageH = Stage.Height;   // 240
        double halfH = stageH / 2;

        // logo 静态在最左侧；"JuniGrid" 在它右侧一个 Gap
        double wordX = LogoSize + Gap;
        double wordY = halfH - _bounds.Height / 2;

        Canvas.SetLeft(LogoImg, 0);
        Canvas.SetTop(LogoImg, halfH - LogoSize / 2);
        Canvas.SetLeft(StrokePath, wordX);
        Canvas.SetTop(StrokePath, wordY);
        Canvas.SetLeft(FillPath, wordX);
        Canvas.SetTop(FillPath, wordY);

        // v0.22.0：冻结 geometry 避免每帧 clone / 走 dispatcher checks
        if (_logoGeo.CanFreeze) _logoGeo.Freeze();

        StrokePath.Data = _logoGeo;
        FillPath.Data = _logoGeo;
        FillPath.Opacity = 0;   // 起手完全透明，wipe 前一帧再拉起

        // v0.27.0：直接动画 RectangleGeometry.RectProperty，每帧真实更新 clip 区域。
        // v0.22 曾用 ScaleTransform 套在 Geometry.Transform 上，但 WPF 里 Geometry
        // 被 Freezable 优化路径处理时，Transform 动画对 clip 剪切区不一定每帧更新，
        // 导致视觉上"字母突然出现"而不是从左到右扫。RectProperty 是 Freezable
        // animation 明确支持的路径，8 个字母的 Path 性能完全够。
        var wipeClip = new RectangleGeometry(new Rect(0, 0, 0, _bounds.Height));
        FillPath.Clip = wipeClip;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // a) 逐字描边（透明，仅驱动时序）：0.3s 起 → 1.5s
        var dash = Math.Max(_bounds.Width, _bounds.Height) * 3 + 30;
        StrokePath.StrokeDashArray = new DoubleCollection { dash, dash };
        StrokePath.StrokeDashOffset = dash;
        AnimateUI(StrokePath, Shape.StrokeDashOffsetProperty, dash, 0, 0.3, 1.5, ease);

        // b) 白色填色 wipe：ScaleX 从 0 → 1（GPU 直连，超流畅）
        //    1.8s 起 → 0.9s，起点前一帧瞬时把 FillPath.Opacity=1
        var opa = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(1795)
        };
        opa.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        FillPath.BeginAnimation(OpacityProperty, opa);

        // v0.28.0：wipe 改匀速线性扫过 —— CubicEase EaseOut 前快后慢，
        // 前 30% 时间就扫完 65% 宽度（前 7 个字母瞬间揭完，视觉上"突然出现"），
        // 只剩最后的 d 慢慢扫。改成无缓动（线性），并拉长到 1.3s，8 个字母均匀扫入。
        var rectAnim = new RectAnimation
        {
            From = new Rect(0, 0, 0, _bounds.Height),
            To = new Rect(0, 0, _bounds.Width, _bounds.Height),
            BeginTime = TimeSpan.FromMilliseconds(1800),
            Duration = TimeSpan.FromMilliseconds(1300)
            // 不设 EasingFunction —— 默认就是线性匀速
        };
        // wipe 完成后再多停 800ms 让用户看清完整字样，才触发 IntroCompleted
        rectAnim.Completed += (_, _) =>
        {
            var dwell = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(800) };
            dwell.Tick += (_, _) => { dwell.Stop(); FinishIntro(); };
            dwell.Start();
        };
        wipeClip.BeginAnimation(RectangleGeometry.RectProperty, rectAnim);
    }

    private void BuildWordGeometry()
    {
        var typeface = new Typeface(
            new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.ExtraBold, FontStretches.Normal);
        var ft = new FormattedText(
            "JuniGrid", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 96, Brushes.Black, 1.0);

        var geo = ft.BuildGeometry(new Point(0, 0));
        var b = geo.Bounds;
        // 把字形平移到左上角 (0,0)，便于 Stage 内定位
        geo.Transform = new TranslateTransform(-b.X, -b.Y);
        _bounds = new Rect(0, 0, geo.Bounds.Width, geo.Bounds.Height);
        _logoGeo = geo;
    }

    private void FinishIntro()
    {
        if (_introDone) return;
        _introDone = true;
        IntroCompleted?.Invoke();
    }

    public bool IntroDone => _introDone;

    /// <summary>直出：不做淡出动画，动画播完立即关闭 Splash，交棒主窗滑入。</summary>
    public void FadeOutAndClose()
    {
        Close();
    }

    private static void AnimateUI(UIElement target, DependencyProperty dp,
        double from, double to, double beginSec, double durSec, EasingFunctionBase ease)
    {
        var a = new DoubleAnimation
        {
            From = from, To = to,
            BeginTime = TimeSpan.FromSeconds(beginSec),
            Duration = TimeSpan.FromSeconds(durSec),
            EasingFunction = ease
        };
        target.BeginAnimation(dp, a);
    }
}