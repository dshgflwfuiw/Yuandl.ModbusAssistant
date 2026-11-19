using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    private void OnScopeUpdated(object? sender, EventArgs e) => Surface.InvalidateVisual();
}

public sealed class OscilloscopeSurface : FrameworkElement
{
    private readonly DispatcherTimer _sweepTimer;
    private RegisterMonitorViewModel? _vm;
    private Point? _hoverPoint;

    public OscilloscopeSurface()
    {
        _sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _sweepTimer.Tick += (_, _) =>
        {
            if (_vm is { IsFrozen: false } && _vm.Samples.Count > 0)
                InvalidateVisual();
        };

        Loaded += (_, _) => _sweepTimer.Start();
        Unloaded += (_, _) => _sweepTimer.Stop();

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

        // Instrument obsidian CRT background
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 16, 24)), null, new Rect(0, 0, w, h));

        const double padL = 58;
        const double padR = 16;
        const double padT = 18;
        const double padB = 26;
        double plotW = Math.Max(40, w - padL - padR);
        double plotH = Math.Max(40, h - padT - padB);

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 22, 34)), null, new Rect(padL, padT, plotW, plotH));

        const int divsX = 10;
        const int divsY = 8;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(36, 148, 163, 184)), 1);
        var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(72, 148, 163, 184)), 1);

        for (int i = 0; i <= divsX; i++)
        {
            double x = padL + (plotW * i / divsX);
            dc.DrawLine(i == divsX / 2 ? centerPen : gridPen, new Point(x, padT), new Point(x, padT + plotH));
        }
        for (int j = 0; j <= divsY; j++)
        {
            double y = padT + (plotH * j / divsY);
            dc.DrawLine(j == divsY / 2 ? centerPen : gridPen, new Point(padL, y), new Point(padL + plotW, y));
        }

        // Fine DSO center subdivision ticks
        double cx = padL + plotW / 2.0;
        double cy = padT + plotH / 2.0;
        for (int i = 0; i <= divsX * 5; i++)
        {
            double x = padL + (plotW * i / (divsX * 5.0));
            dc.DrawLine(centerPen, new Point(x, cy - 3), new Point(x, cy + 3));
        }
        for (int j = 0; j <= divsY * 5; j++)
        {
            double y = padT + (plotH * j / (divsY * 5.0));
            dc.DrawLine(centerPen, new Point(cx - 3, y), new Point(cx + 3, y));
        }

        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 148, 163, 184)), 1), new Rect(padL, padT, plotW, plotH));

        var typeface = new Typeface(new FontFamily("Consolas, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_vm == null || _vm.Samples.Count == 0)
        {
            var emptyText = new FormattedText(
                "示波器等待采样数据 — 点击“读取一次” / “开始监听”，或切换至“服务器实时监听”捕获波形",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                dpi);
            dc.DrawText(emptyText, new Point(padL + 18, padT + (plotH / 2) - 8));
            return;
        }

        var samples = _vm.Samples;
        DateTime latestSampleTs = samples[^1].Timestamp;
        DateTime endTime = _vm.IsFrozen
            ? latestSampleTs
            : (DateTime.Now > latestSampleTs ? DateTime.Now : latestSampleTs);
        DateTime windowStart = endTime.AddSeconds(-_vm.TimeWindowSec);
        var visibleSamples = samples.Where(s => s.Timestamp >= windowStart.AddSeconds(-5)).ToList();
        if (visibleSamples.Count == 0)
            visibleSamples = new List<Models.ScopeSample> { samples[^1] };

        var activeChannels = _vm.ScopeChannels.Where(c => c.Enabled).ToList();
        bool allDigital = activeChannels.Count > 0 && activeChannels.All(c => c.IsDigital);

        // Y Bounds
        double yMin = _vm.ManualYMin;
        double yMax = _vm.ManualYMax;
        if (_vm.AutoScaleY || yMax <= yMin)
        {
            bool hasVal = false;
            double minV = double.MaxValue;
            double maxV = double.MinValue;
            foreach (var s in visibleSamples)
            {
                foreach (var ch in activeChannels)
                {
                    if (s.Values.TryGetValue(ch.Id, out double v) && double.IsFinite(v))
                    {
                        hasVal = true;
                        if (v < minV) minV = v;
                        if (v > maxV) maxV = v;
                    }
                }
            }

            if (!hasVal)
            {
                yMin = allDigital ? -0.2 : 0;
                yMax = allDigital ? 1.25 : 100;
            }
            else if (allDigital && minV >= 0 && maxV <= 1)
            {
                yMin = -0.2;
                yMax = 1.25;
            }
            else if (Math.Abs(maxV - minV) < 1e-9)
            {
                double pad = minV == 0 ? 1 : Math.Abs(minV) * 0.2;
                yMin = minV - pad;
                yMax = maxV + pad;
            }
            else
            {
                double pad = (maxV - minV) * 0.12;
                yMin = minV - pad;
                yMax = maxV + pad;
            }
        }

        double timeSpanSec = Math.Max(0.1, (endTime - windowStart).TotalSeconds);
        double MapX(DateTime t) => padL + ((t - windowStart).TotalSeconds / timeSpanSec) * plotW;
        double MapY(double v) => padT + (1.0 - ((v - yMin) / Math.Max(1e-9, yMax - yMin))) * plotH;

        // Y-axis tick labels
        var labelBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        for (int j = 0; j <= 4; j++)
        {
            double val = yMax - ((yMax - yMin) * j / 4.0);
            double y = padT + (plotH * j / 4.0);
            string formatted = Math.Abs(val) >= 1000 ? val.ToString("0", CultureInfo.InvariantCulture)
                : Math.Abs(val) >= 10 ? val.ToString("0.#", CultureInfo.InvariantCulture)
                : val.ToString("0.##", CultureInfo.InvariantCulture);
            var ft = new FormattedText(formatted, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(ft, new Point(Math.Max(2, padL - ft.Width - 6), y - 6));
        }

        // X-axis relative time labels
        for (int i = 0; i <= 5; i++)
        {
            double secOffset = -_vm.TimeWindowSec + (_vm.TimeWindowSec * i / 5.0);
            double x = padL + (plotW * i / 5.0);
            string txt = Math.Abs(secOffset) < 0.01 ? "0s" : $"{secOffset:0}s";
            var ft = new FormattedText(txt, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, padT + plotH + 5));
        }

        // Clip waveforms inside plot rect
        dc.PushClip(new RectangleGeometry(new Rect(padL, padT, plotW, plotH)));
        int triggerHitCount = 0;

        try
        {
            // Trigger line
            if (_vm.TriggerEnabled)
            {
                double trigY = MapY(_vm.TriggerLevel);
                var trigColor = Color.FromRgb(245, 158, 11);
                var trigPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 245, 158, 11)), 1.25) { DashStyle = DashStyles.Dash };
                dc.DrawLine(trigPen, new Point(padL, trigY), new Point(padL + plotW, trigY));

                string arrow = _vm.TriggerEdge == TriggerEdgeMode.Rising ? "↑" : "↓";
                var trigFt = new FormattedText($"TRIG {arrow} {_vm.TriggerLevel:G6}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, new SolidColorBrush(trigColor), dpi);
                dc.DrawText(trigFt, new Point(padL + 6, Math.Max(padT + 2, trigY - 14)));
            }

            // Channel waveforms
            foreach (var ch in activeChannels)
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(ch.ColorHex); }
                catch { color = Colors.Cyan; }

                var traceBrush = new SolidColorBrush(color);
                var glowBrush = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
                var tracePen = new Pen(traceBrush, 1.8) { LineJoin = PenLineJoin.Round };
                var glowPen = new Pen(glowBrush, 4.5) { LineJoin = PenLineJoin.Round };
                var geom = new StreamGeometry();
                var pts = new List<Point>();
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

                        var pt = new Point(MapX(s.Timestamp), MapY(val));
                        pts.Add(pt);

                        if (!started)
                        {
                            ctx.BeginFigure(pt, false, false);
                            started = true;
                        }
                        else
                        {
                            if (ch.IsDigital)
                                ctx.LineTo(new Point(pt.X, prevPt.Y), true, false);
                            ctx.LineTo(pt, true, false);

                            if (_vm.TriggerEnabled && _vm.TriggerChannel?.Id == ch.Id)
                            {
                                bool crossed = _vm.TriggerEdge == TriggerEdgeMode.Rising
                                    ? (prevVal < _vm.TriggerLevel && val >= _vm.TriggerLevel)
                                    : (prevVal > _vm.TriggerLevel && val <= _vm.TriggerLevel);
                                if (crossed)
                                {
                                    trigDots.Add(pt);
                                    triggerHitCount++;
                                }
                            }
                        }

                        prevPt = pt;
                        prevVal = val;
                    }
                }
                geom.Freeze();
                dc.DrawGeometry(null, glowPen, geom);
                dc.DrawGeometry(null, tracePen, geom);

                if (_vm.ShowMarkers || pts.Count == 1)
                {
                    foreach (var pt in pts)
                        dc.DrawEllipse(traceBrush, null, pt, 2.5, 2.5);
                }

                foreach (var dot in trigDots)
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(245, 158, 11)), null, dot, 3.8, 3.8);
            }

            // Hover crosshair & probe dots
            if (_hoverPoint is { } hp && hp.X >= padL && hp.X <= padL + plotW && hp.Y >= padT && hp.Y <= padT + plotH)
            {
                double ratio = (hp.X - padL) / plotW;
                DateTime targetTime = windowStart.AddSeconds(ratio * timeSpanSec);
                var nearest = visibleSamples.MinBy(s => Math.Abs((s.Timestamp - targetTime).TotalMilliseconds));
                if (nearest != null)
                {
                    double hx = MapX(nearest.Timestamp);
                    var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(145, 226, 232, 240)), 1) { DashStyle = DashStyles.Dot };
                    dc.DrawLine(crossPen, new Point(hx, padT), new Point(hx, padT + plotH));
                    dc.DrawLine(crossPen, new Point(padL, hp.Y), new Point(padL + plotW, hp.Y));

                    foreach (var ch in activeChannels)
                    {
                        if (!nearest.Values.TryGetValue(ch.Id, out double val) || !double.IsFinite(val)) continue;
                        Color col;
                        try { col = (Color)ColorConverter.ConvertFromString(ch.ColorHex); }
                        catch { col = Colors.Cyan; }
                        dc.DrawEllipse(new SolidColorBrush(col), new Pen(new SolidColorBrush(Color.FromRgb(11, 16, 24)), 1.5), new Point(hx, MapY(val)), 4, 4);
                    }

                    var hudLines = new List<string> { $"时间: {nearest.Timestamp:HH:mm:ss.fff}" };
                    foreach (var ch in activeChannels.Take(6))
                    {
                        if (nearest.Values.TryGetValue(ch.Id, out double v))
                            hudLines.Add($"{ch.Label}: {(ch.IsDigital ? (v >= 0.5 ? "1 (ON)" : "0 (OFF)") : v.ToString("G6"))}");
                    }
                    var ft = new FormattedText(string.Join("\n", hudLines), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, Brushes.White, dpi);
                    double boxW = ft.Width + 16;
                    double boxH = ft.Height + 10;
                    double bx = hx + 12 + boxW > padL + plotW ? hx - boxW - 12 : hx + 12;
                    double by = padT + 8;
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 15, 23, 42)), new Pen(new SolidColorBrush(Color.FromRgb(51, 65, 85)), 1), new Rect(bx, by, boxW, boxH), 4, 4);
                    dc.DrawText(ft, new Point(bx + 8, by + 5));
                }
            }
        }
        finally
        {
            dc.Pop();
        }

        // Top-right RUN/HOLD & Trigger counter HUD
        string badgeText = $"{(_vm.IsFrozen ? "HOLD" : "RUN")} · 样本 {samples.Count}" +
                           (_vm.TriggerEnabled ? $" · 触发命中 {triggerHitCount}" : string.Empty);
        var badgeFt = new FormattedText(badgeText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10.5, new SolidColorBrush(Color.FromRgb(226, 232, 240)), dpi);
        double badgeW = badgeFt.Width + 14;
        double badgeH = badgeFt.Height + 8;
        double badgeX = padL + plotW - badgeW - 8;
        double badgeY = padT + 8;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 2, 6, 23)), new Pen(new SolidColorBrush(Color.FromArgb(140, 51, 65, 85)), 1), new Rect(badgeX, badgeY, badgeW, badgeH), 4, 4);
        dc.DrawText(badgeFt, new Point(badgeX + 7, badgeY + 4));
    }
}
