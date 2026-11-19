using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Yuandl.ModbusAssistant.ViewModels.Pages;

namespace Yuandl.ModbusAssistant.Controls;

public partial class OscilloscopeControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(RegisterMonitorViewModel),
            typeof(OscilloscopeControl),
            new PropertyMetadata(null, OnViewModelChanged));

    public RegisterMonitorViewModel? ViewModel
    {
        get => (RegisterMonitorViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public OscilloscopeControl()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (ViewModel == null && e.NewValue is RegisterMonitorViewModel vm)
                ViewModel = vm;
        };
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OscilloscopeControl ctrl)
        {
            if (e.OldValue is RegisterMonitorViewModel oldVm)
                oldVm.ScopeUpdated -= ctrl.OnScopeUpdated;
            if (e.NewValue is RegisterMonitorViewModel newVm)
                newVm.ScopeUpdated += ctrl.OnScopeUpdated;
            ctrl.Surface.Attach(e.NewValue as RegisterMonitorViewModel);
        }
    }

    private void OnScopeUpdated(object? sender, EventArgs e)
    {
        Surface.InvalidateVisual();
    }
}

public sealed class OscilloscopeSurface : FrameworkElement
{
    private RegisterMonitorViewModel? _vm;
    private Point? _hoverPoint;

    public OscilloscopeSurface()
    {
        MouseMove += (_, e) =>
        {
            _hoverPoint = e.GetPosition(this);
            InvalidateVisual();
        };
        MouseLeave += (_, _) =>
        {
            _hoverPoint = null;
            InvalidateVisual();
        };
    }

    public void Attach(RegisterMonitorViewModel? vm)
    {
        _vm = vm;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 40 || h <= 40) return;

        // Instrument chassis backdrop
        var bgBrush = new SolidColorBrush(Color.FromRgb(6, 10, 16));
        dc.DrawRectangle(bgBrush, null, new Rect(0, 0, w, h));

        const double padL = 56;
        const double padR = 18;
        const double padT = 18;
        const double padB = 28;
        double plotW = Math.Max(10, w - padL - padR);
        double plotH = Math.Max(10, h - padT - padB);

        // Grid (10x8 divisions)
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 1) { DashStyle = DashStyles.Dash };
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 148, 163, 184)), 1);
        for (int i = 0; i <= 10; i++)
        {
            double x = padL + (plotW * i / 10.0);
            dc.DrawLine(i == 5 ? axisPen : gridPen, new Point(x, padT), new Point(x, padT + plotH));
        }
        for (int j = 0; j <= 8; j++)
        {
            double y = padT + (plotH * j / 8.0);
            dc.DrawLine(j == 4 ? axisPen : gridPen, new Point(padL, y), new Point(padL + plotW, y));
        }
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1), new Rect(padL, padT, plotW, plotH));

        var typeface = new Typeface(new FontFamily("Consolas, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_vm == null || _vm.Samples.Count == 0)
        {
            var emptyText = new FormattedText(
                "等待采样数据... 点击“开始监听”以实时绘制波形（支持数值曲线与布尔方波）",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                dpi);
            dc.DrawText(emptyText, new Point(padL + 16, padT + (plotH / 2) - 8));
            return;
        }

        var samples = _vm.Samples;
        DateTime latestTime = samples[^1].Timestamp;
        DateTime windowStart = latestTime.AddSeconds(-_vm.TimeWindowSec);
        var visibleSamples = samples.Where(s => s.Timestamp >= windowStart).ToList();
        if (visibleSamples.Count == 0)
            visibleSamples = new List<Models.ScopeSample> { samples[^1] };

        var activeChannels = _vm.ScopeChannels.Where(c => c.Enabled).ToList();
        if (activeChannels.Count == 0) return;

        // Compute Y range
        double yMin = _vm.ManualYMin;
        double yMax = _vm.ManualYMax;
        if (_vm.AutoScaleY)
        {
            bool hasValue = false;
            double minV = double.MaxValue;
            double maxV = double.MinValue;
            foreach (var s in visibleSamples)
            {
                foreach (var ch in activeChannels)
                {
                    if (s.Values.TryGetValue(ch.Id, out double val) && double.IsFinite(val))
                    {
                        hasValue = true;
                        if (val < minV) minV = val;
                        if (val > maxV) maxV = val;
                    }
                }
            }
            if (!hasValue)
            {
                yMin = 0;
                yMax = 1;
            }
            else if (Math.Abs(maxV - minV) < 1e-9)
            {
                if (activeChannels.All(c => c.IsDigital))
                {
                    yMin = -0.15;
                    yMax = 1.15;
                }
                else
                {
                    yMin = minV - 1;
                    yMax = maxV + 1;
                }
            }
            else
            {
                double pad = (maxV - minV) * 0.12;
                yMin = minV - pad;
                yMax = maxV + pad;
            }
        }
        if (yMax <= yMin) yMax = yMin + 1;

        double MapX(DateTime t) =>
            padL + Math.Clamp((t - windowStart).TotalSeconds / _vm.TimeWindowSec, 0, 1) * plotW;

        double MapY(double v) =>
            padT + (1.0 - Math.Clamp((v - yMin) / (yMax - yMin), 0, 1)) * plotH;

        // Draw Y-axis ticks
        var labelBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        for (int j = 0; j <= 4; j++)
        {
            double val = yMax - ((yMax - yMin) * j / 4.0);
            double y = padT + (plotH * j / 4.0);
            var ft = new FormattedText(val.ToString("0.##", CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(ft, new Point(4, y - 6));
        }

        // Draw X-axis time labels
        for (int i = 0; i <= 4; i++)
        {
            double secOffset = -_vm.TimeWindowSec + (_vm.TimeWindowSec * i / 4.0);
            double x = padL + (plotW * i / 4.0);
            string txt = secOffset == 0 ? "0s" : $"{secOffset:0}s";
            var ft = new FormattedText(txt, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(ft, new Point(x - 10, padT + plotH + 6));
        }

        // Trigger line
        if (_vm.TriggerEnabled)
        {
            double trigY = MapY(_vm.TriggerLevel);
            var trigPen = new Pen(new SolidColorBrush(Color.FromRgb(251, 146, 60)), 1.2) { DashStyle = DashStyles.Dot };
            dc.DrawLine(trigPen, new Point(padL, trigY), new Point(padL + plotW, trigY));
        }

        // Draw channel waveforms
        foreach (var ch in activeChannels)
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(ch.ColorHex); }
            catch { color = Colors.Cyan; }

            var traceBrush = new SolidColorBrush(color);
            var tracePen = new Pen(traceBrush, ch.IsDigital ? 2.0 : 1.8);
            var geom = new StreamGeometry();
            var trigDots = new List<Point>();

            using (var ctx = geom.Open())
            {
                bool started = false;
                Point prevPt = default;
                double prevVal = 0;

                foreach (var s in visibleSamples)
                {
                    if (!s.Values.TryGetValue(ch.Id, out double val) || !double.IsFinite(val))
                        continue;

                    double x = MapX(s.Timestamp);
                    double y = MapY(val);
                    var pt = new Point(x, y);

                    if (!started)
                    {
                        ctx.BeginFigure(pt, false, false);
                        started = true;
                    }
                    else
                    {
                        if (ch.IsDigital)
                        {
                            // Square wave step-hold
                            ctx.LineTo(new Point(x, prevPt.Y), true, false);
                        }
                        ctx.LineTo(pt, true, false);

                        if (_vm.TriggerEnabled && _vm.TriggerChannel?.Id == ch.Id)
                        {
                            bool crossed = _vm.TriggerEdge == TriggerEdgeMode.Rising
                                ? (prevVal < _vm.TriggerLevel && val >= _vm.TriggerLevel)
                                : (prevVal > _vm.TriggerLevel && val <= _vm.TriggerLevel);
                            if (crossed) trigDots.Add(pt);
                        }
                    }

                    prevPt = pt;
                    prevVal = val;
                }
            }
            geom.Freeze();
            dc.DrawGeometry(null, tracePen, geom);

            foreach (var dot in trigDots)
                dc.DrawEllipse(Brushes.Orange, null, dot, 3.5, 3.5);
        }

        // Hover crosshair & HUD
        if (_hoverPoint is { } hp && hp.X >= padL && hp.X <= padL + plotW && hp.Y >= padT && hp.Y <= padT + plotH)
        {
            var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 226, 232, 240)), 1);
            dc.DrawLine(crossPen, new Point(hp.X, padT), new Point(hp.X, padT + plotH));
            dc.DrawLine(crossPen, new Point(padL, hp.Y), new Point(padL + plotW, hp.Y));

            double ratio = (hp.X - padL) / plotW;
            DateTime targetTime = windowStart.AddSeconds(ratio * _vm.TimeWindowSec);
            var nearest = visibleSamples.MinBy(s => Math.Abs((s.Timestamp - targetTime).TotalMilliseconds));
            if (nearest != null)
            {
                var hudLines = new List<string> { $"时间: {nearest.Timestamp:HH:mm:ss.fff}" };
                foreach (var ch in activeChannels.Take(6))
                {
                    if (nearest.Values.TryGetValue(ch.Id, out double v))
                        hudLines.Add($"{ch.Label}: {(ch.IsDigital ? (v >= 0.5 ? "ON (1)" : "OFF (0)") : v.ToString("G7"))}");
                }
                string hudText = string.Join("\n", hudLines);
                var ft = new FormattedText(hudText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, Brushes.White, dpi);
                double boxW = ft.Width + 14;
                double boxH = ft.Height + 10;
                double bx = hp.X + 12 + boxW > padL + plotW ? hp.X - boxW - 12 : hp.X + 12;
                double by = padT + 8;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 23, 42)), new Pen(new SolidColorBrush(Color.FromRgb(51, 65, 85)), 1), new Rect(bx, by, boxW, boxH), 4, 4);
                dc.DrawText(ft, new Point(bx + 7, by + 5));
            }
        }
    }
}
