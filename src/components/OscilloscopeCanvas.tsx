import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Pause, Play, RotateCcw, Download, Crosshair } from 'lucide-react';

export interface ScopeChannelConfig {
  id: string;
  label: string;
  color: string;
  enabled: boolean;
  isDigital?: boolean;
  unit?: string;
}

export interface ScopeSample {
  timestampMs: number;
  values: Record<string, number>;
}

interface OscilloscopeCanvasProps {
  channels: ScopeChannelConfig[];
  samples: ScopeSample[];
  isFrozen: boolean;
  onToggleFreeze: () => void;
  onClearSamples: () => void;
  onToggleChannel: (channelId: string) => void;
  timeWindowSec: number;
  onTimeWindowChange: (sec: number) => void;
}

export const SCOPE_CHANNEL_COLORS = [
  '#22D3EE', // CH1 Cyan
  '#F59E0B', // CH2 Amber
  '#34D399', // CH3 Emerald
  '#FB7185', // CH4 Rose
  '#60A5FA', // CH5 Cobalt
  '#A78BFA', // CH6 Violet
  '#F472B6', // CH7 Pink
  '#A3E635', // CH8 Lime
];

interface HoverReadout {
  xPx: number;
  yPx: number;
  sample: ScopeSample;
  sampleIndex: number;
}

export const OscilloscopeCanvas: React.FC<OscilloscopeCanvasProps> = ({
  channels,
  samples,
  isFrozen,
  onToggleFreeze,
  onClearSamples,
  onToggleChannel,
  timeWindowSec,
  onTimeWindowChange,
}) => {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [size, setSize] = useState({ width: 800, height: 320 });

  // Vertical scale & trigger controls
  const [yScaleMode, setYScaleMode] = useState<'auto' | 'manual'>('auto');
  const [manualYMin, setManualYMin] = useState<string>('0');
  const [manualYMax, setManualYMax] = useState<string>('100');
  const [triggerMode, setTriggerMode] = useState<'off' | 'rising' | 'falling'>('off');
  const [triggerChannelId, setTriggerChannelId] = useState<string>('');
  const [triggerLevelInput, setTriggerLevelInput] = useState<string>('50');
  const [showMarkers, setShowMarkers] = useState<boolean>(false);
  const [hoverInfo, setHoverInfo] = useState<HoverReadout | null>(null);
  const [nowMs, setNowMs] = useState<number>(() => Date.now());

  const activeChannels = useMemo(
    () => channels.filter((c) => c.enabled),
    [channels]
  );

  // Keep triggerChannelId synced with first enabled channel
  useEffect(() => {
    if (
      activeChannels.length > 0 &&
      !activeChannels.some((c) => c.id === triggerChannelId)
    ) {
      setTriggerChannelId(activeChannels[0].id);
    }
  }, [activeChannels, triggerChannelId]);

  // Smooth oscilloscope sweep clock when not frozen
  useEffect(() => {
    if (isFrozen || samples.length === 0) return;
    const latest = samples[samples.length - 1]?.timestampMs ?? Date.now();
    setNowMs(Math.max(Date.now(), latest));
    const id = window.setInterval(() => {
      setNowMs(Date.now());
    }, 200);
    return () => window.clearInterval(id);
  }, [isFrozen, samples]);

  // Resize observer for crisp canvas scaling
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const update = () => {
      const rect = el.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) {
        setSize({
          width: Math.floor(rect.width),
          height: Math.floor(rect.height),
        });
      }
    };
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const visibleWindow = useMemo(() => {
    const endTime = isFrozen
      ? (samples[samples.length - 1]?.timestampMs ?? nowMs)
      : Math.max(nowMs, samples[samples.length - 1]?.timestampMs ?? nowMs);
    const startTime = endTime - timeWindowSec * 1000;
    const filtered = samples.filter((s) => s.timestampMs >= startTime - 5000);
    return { startTime, endTime, visibleSamples: filtered };
  }, [samples, timeWindowSec, isFrozen, nowMs]);

  const yBounds = useMemo(() => {
    if (yScaleMode === 'manual') {
      const min = Number(manualYMin);
      const max = Number(manualYMax);
      if (Number.isFinite(min) && Number.isFinite(max) && max > min) {
        return { min, max };
      }
    }

    const allDigital =
      activeChannels.length > 0 && activeChannels.every((c) => c.isDigital);

    let min = Infinity;
    let max = -Infinity;
    for (const sample of visibleWindow.visibleSamples) {
      for (const ch of activeChannels) {
        const val = sample.values[ch.id];
        if (typeof val === 'number' && Number.isFinite(val)) {
          if (val < min) min = val;
          if (val > max) max = val;
        }
      }
    }

    if (!Number.isFinite(min) || !Number.isFinite(max)) {
      return allDigital ? { min: -0.2, max: 1.2 } : { min: 0, max: 100 };
    }
    if (allDigital && min >= 0 && max <= 1) {
      return { min: -0.2, max: 1.25 };
    }
    if (min === max) {
      const pad = min === 0 ? 1 : Math.abs(min) * 0.2;
      return { min: min - pad, max: max + pad };
    }
    const span = max - min;
    const pad = span * 0.12;
    return { min: min - pad, max: max + pad };
  }, [yScaleMode, manualYMin, manualYMax, visibleWindow.visibleSamples, activeChannels]);

  // Per-channel live statistics over visible window
  const channelStats = useMemo(() => {
    const stats: Record<
      string,
      { latest: number | null; min: number | null; max: number | null; avg: number | null }
    > = {};
    for (const ch of channels) {
      let min = Infinity;
      let max = -Infinity;
      let sum = 0;
      let count = 0;
      let latest: number | null = null;

      for (const s of visibleWindow.visibleSamples) {
        if (s.timestampMs < visibleWindow.startTime) continue;
        const v = s.values[ch.id];
        if (typeof v === 'number' && Number.isFinite(v)) {
          if (v < min) min = v;
          if (v > max) max = v;
          sum += v;
          count += 1;
          latest = v;
        }
      }

      if (latest === null && samples.length > 0) {
        const lastSample = samples[samples.length - 1];
        const fallback = lastSample.values[ch.id];
        if (typeof fallback === 'number' && Number.isFinite(fallback)) {
          latest = fallback;
        }
      }

      stats[ch.id] = {
        latest,
        min: count > 0 ? min : null,
        max: count > 0 ? max : null,
        avg: count > 0 ? sum / count : null,
      };
    }
    return stats;
  }, [channels, visibleWindow, samples]);

  // Trigger crossing detection
  const triggerCrossings = useMemo(() => {
    if (triggerMode === 'off' || !triggerChannelId) return [];
    const level = Number(triggerLevelInput);
    if (!Number.isFinite(level)) return [];
    const list: Array<{ timestampMs: number; value: number }> = [];
    const arr = visibleWindow.visibleSamples;
    for (let i = 1; i < arr.length; i++) {
      const prev = arr[i - 1].values[triggerChannelId];
      const curr = arr[i].values[triggerChannelId];
      if (
        typeof prev !== 'number' ||
        typeof curr !== 'number' ||
        !Number.isFinite(prev) ||
        !Number.isFinite(curr)
      ) {
        continue;
      }
      if (triggerMode === 'rising' && prev < level && curr >= level) {
        list.push({ timestampMs: arr[i].timestampMs, value: curr });
      } else if (triggerMode === 'falling' && prev > level && curr <= level) {
        list.push({ timestampMs: arr[i].timestampMs, value: curr });
      }
    }
    return list;
  }, [triggerMode, triggerChannelId, triggerLevelInput, visibleWindow.visibleSamples]);

  // Draw oscilloscope graticule & waveforms
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const dpr = window.devicePixelRatio || 1;
    const width = size.width;
    const height = size.height;

    canvas.width = Math.floor(width * dpr);
    canvas.height = Math.floor(height * dpr);
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const padLeft = 58;
    const padRight = 16;
    const padTop = 16;
    const padBottom = 26;
    const plotW = Math.max(40, width - padLeft - padRight);
    const plotH = Math.max(40, height - padTop - padBottom);

    // 1. Instrument dark obsidian CRT background
    ctx.fillStyle = '#0B1018';
    ctx.fillRect(0, 0, width, height);

    ctx.fillStyle = '#0F1622';
    ctx.fillRect(padLeft, padTop, plotW, plotH);

    const divsX = 10;
    const divsY = 8;

    // 2. Graticule major lines
    ctx.strokeStyle = 'rgba(148, 163, 184, 0.14)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= divsX; i++) {
      const x = Math.round(padLeft + (plotW * i) / divsX) + 0.5;
      ctx.beginPath();
      ctx.moveTo(x, padTop);
      ctx.lineTo(x, padTop + plotH);
      ctx.stroke();
    }
    for (let j = 0; j <= divsY; j++) {
      const y = Math.round(padTop + (plotH * j) / divsY) + 0.5;
      ctx.beginPath();
      ctx.moveTo(padLeft, y);
      ctx.lineTo(padLeft + plotW, y);
      ctx.stroke();
    }

    // Center crosshair axes + fine subdivision ticks (like real DSO)
    const centerX = Math.round(padLeft + plotW / 2) + 0.5;
    const centerY = Math.round(padTop + plotH / 2) + 0.5;
    ctx.strokeStyle = 'rgba(148, 163, 184, 0.28)';
    ctx.beginPath();
    ctx.moveTo(centerX, padTop);
    ctx.lineTo(centerX, padTop + plotH);
    ctx.moveTo(padLeft, centerY);
    ctx.lineTo(padLeft + plotW, centerY);
    ctx.stroke();

    const subTicksX = divsX * 5;
    for (let i = 0; i <= subTicksX; i++) {
      const x = Math.round(padLeft + (plotW * i) / subTicksX) + 0.5;
      ctx.beginPath();
      ctx.moveTo(x, centerY - 3);
      ctx.lineTo(x, centerY + 3);
      ctx.stroke();
    }
    const subTicksY = divsY * 5;
    for (let j = 0; j <= subTicksY; j++) {
      const y = Math.round(padTop + (plotH * j) / subTicksY) + 0.5;
      ctx.beginPath();
      ctx.moveTo(centerX - 3, y);
      ctx.lineTo(centerX + 3, y);
      ctx.stroke();
    }

    // Outer frame border
    ctx.strokeStyle = 'rgba(148, 163, 184, 0.35)';
    ctx.strokeRect(padLeft + 0.5, padTop + 0.5, plotW - 1, plotH - 1);

    // Coordinate helpers
    const { startTime, endTime, visibleSamples } = visibleWindow;
    const timeSpan = Math.max(100, endTime - startTime);
    const ySpan = Math.max(1e-9, yBounds.max - yBounds.min);

    const mapX = (ts: number) =>
      padLeft + ((ts - startTime) / timeSpan) * plotW;
    const mapY = (val: number) =>
      padTop + plotH - ((val - yBounds.min) / ySpan) * plotH;

    // 3. Y-axis tick labels
    ctx.fillStyle = '#94A3B8';
    ctx.font = '11px "Cascadia Code", "Consolas", ui-monospace, monospace';
    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    const labelRows = [0, 2, 4, 6, 8];
    for (const row of labelRows) {
      const ratio = row / divsY;
      const val = yBounds.max - ratio * ySpan;
      const y = padTop + ratio * plotH;
      const text =
        Math.abs(val) >= 1000
          ? val.toFixed(0)
          : Math.abs(val) >= 10
            ? val.toFixed(1)
            : val.toFixed(2);
      ctx.fillText(text, padLeft - 6, y);
    }

    // 4. X-axis relative time labels (-60s ... 0s)
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (let i = 0; i <= divsX; i += 2) {
      const x = padLeft + (plotW * i) / divsX;
      const secOffset = -Math.round(((divsX - i) / divsX) * timeWindowSec);
      const label = secOffset === 0 ? '0s' : `${secOffset}s`;
      ctx.fillText(label, x, padTop + plotH + 6);
    }

    // Clip to waveform plot area
    ctx.save();
    ctx.beginPath();
    ctx.rect(padLeft, padTop, plotW, plotH);
    ctx.clip();

    // 5. Trigger threshold line
    if (triggerMode !== 'off') {
      const trigLevel = Number(triggerLevelInput);
      if (Number.isFinite(trigLevel)) {
        const ty = mapY(trigLevel);
        ctx.save();
        ctx.strokeStyle = 'rgba(245, 158, 11, 0.8)';
        ctx.setLineDash([5, 4]);
        ctx.lineWidth = 1.25;
        ctx.beginPath();
        ctx.moveTo(padLeft, ty);
        ctx.lineTo(padLeft + plotW, ty);
        ctx.stroke();
        ctx.restore();

        ctx.fillStyle = '#F59E0B';
        ctx.font = '10px "Cascadia Code", ui-monospace, monospace';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'bottom';
        ctx.fillText(
          `TRIG ${triggerMode === 'rising' ? '↑' : '↓'} ${trigLevel}`,
          padLeft + 6,
          Math.max(padTop + 12, ty - 2)
        );
      }
    }

    // 6. Draw each active channel trace
    for (const ch of activeChannels) {
      const pts: Array<{ x: number; y: number }> = [];
      for (const sample of visibleSamples) {
        const val = sample.values[ch.id];
        if (typeof val !== 'number' || !Number.isFinite(val)) continue;
        pts.push({ x: mapX(sample.timestampMs), y: mapY(val) });
      }
      if (pts.length === 0) continue;

      ctx.save();
      ctx.strokeStyle = ch.color;
      ctx.lineWidth = 1.8;
      ctx.lineJoin = 'round';
      ctx.lineCap = 'round';
      ctx.beginPath();
      for (let i = 0; i < pts.length; i++) {
        if (i === 0) {
          ctx.moveTo(pts[i].x, pts[i].y);
        } else if (ch.isDigital) {
          // Square-wave step transition for boolean / digital signals
          ctx.lineTo(pts[i].x, pts[i - 1].y);
          ctx.lineTo(pts[i].x, pts[i].y);
        } else {
          ctx.lineTo(pts[i].x, pts[i].y);
        }
      }
      ctx.stroke();

      // Subtle phosphor glow pass
      ctx.strokeStyle = `${ch.color}33`;
      ctx.lineWidth = 4.5;
      ctx.stroke();

      // Optional sample dots or single-point marker
      if (showMarkers || pts.length === 1) {
        ctx.fillStyle = ch.color;
        for (const pt of pts) {
          ctx.beginPath();
          ctx.arc(pt.x, pt.y, 2.5, 0, Math.PI * 2);
          ctx.fill();
        }
      }
      ctx.restore();
    }

    // 7. Highlight trigger crossing points
    if (triggerCrossings.length > 0) {
      ctx.save();
      ctx.fillStyle = '#F59E0B';
      for (const tc of triggerCrossings) {
        const tx = mapX(tc.timestampMs);
        const ty = mapY(tc.value);
        ctx.beginPath();
        ctx.arc(tx, ty, 3.5, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.restore();
    }

    // 8. Hover crosshair probe
    if (hoverInfo) {
      const hx = mapX(hoverInfo.sample.timestampMs);
      ctx.save();
      ctx.strokeStyle = 'rgba(226, 232, 240, 0.55)';
      ctx.setLineDash([3, 3]);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(hx, padTop);
      ctx.lineTo(hx, padTop + plotH);
      ctx.moveTo(padLeft, hoverInfo.yPx);
      ctx.lineTo(padLeft + plotW, hoverInfo.yPx);
      ctx.stroke();
      ctx.setLineDash([]);

      for (const ch of activeChannels) {
        const val = hoverInfo.sample.values[ch.id];
        if (typeof val !== 'number' || !Number.isFinite(val)) continue;
        const py = mapY(val);
        ctx.fillStyle = ch.color;
        ctx.beginPath();
        ctx.arc(hx, py, 4, 0, Math.PI * 2);
        ctx.fill();
        ctx.strokeStyle = '#0B1018';
        ctx.lineWidth = 1.5;
        ctx.stroke();
      }
      ctx.restore();
    }

    ctx.restore();
  }, [
    size,
    visibleWindow,
    yBounds,
    activeChannels,
    triggerMode,
    triggerLevelInput,
    triggerCrossings,
    showMarkers,
    hoverInfo,
    timeWindowSec,
  ]);

  const handleMouseMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    if (!canvas || visibleWindow.visibleSamples.length === 0) {
      setHoverInfo(null);
      return;
    }
    const rect = canvas.getBoundingClientRect();
    const xPx = e.clientX - rect.left;
    const yPx = e.clientY - rect.top;

    const padLeft = 58;
    const padRight = 16;
    const padTop = 16;
    const padBottom = 26;
    const plotW = Math.max(40, size.width - padLeft - padRight);
    const plotH = Math.max(40, size.height - padTop - padBottom);

    if (
      xPx < padLeft ||
      xPx > padLeft + plotW ||
      yPx < padTop ||
      yPx > padTop + plotH
    ) {
      setHoverInfo(null);
      return;
    }

    const ratio = (xPx - padLeft) / plotW;
    const targetTs =
      visibleWindow.startTime +
      ratio * (visibleWindow.endTime - visibleWindow.startTime);

    let bestIdx = 0;
    let bestDist = Infinity;
    const arr = visibleWindow.visibleSamples;
    for (let i = 0; i < arr.length; i++) {
      const d = Math.abs(arr[i].timestampMs - targetTs);
      if (d < bestDist) {
        bestDist = d;
        bestIdx = i;
      }
    }
    setHoverInfo({
      xPx,
      yPx,
      sample: arr[bestIdx],
      sampleIndex: bestIdx,
    });
  };

  const handleExportCsv = () => {
    if (samples.length === 0 || channels.length === 0) return;
    const headers = ['Timestamp', ...channels.map((c) => c.label)];
    const lines = [headers.join(',')];
    for (const s of samples) {
      const ts = new Date(s.timestampMs).toISOString();
      const row = [
        ts,
        ...channels.map((c) =>
          typeof s.values[c.id] === 'number' ? String(s.values[c.id]) : ''
        ),
      ];
      lines.push(row.join(','));
    }
    const blob = new Blob([lines.join('\n')], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `modbus-oscilloscope-${Date.now()}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="space-y-3">
      {/* 示波器控制工具栏 */}
      <div className="flex flex-wrap items-center justify-between gap-2 pb-2 border-b border-[var(--border-subtle)]">
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={onToggleFreeze}
            className={`fluent-btn h-8 text-xs whitespace-nowrap shrink-0 ${
              isFrozen ? 'fluent-btn-primary' : ''
            }`}
          >
            {isFrozen ? (
              <>
                <Play className="w-3.5 h-3.5" />
                <span>继续刷新 (HOLD)</span>
              </>
            ) : (
              <>
                <Pause className="w-3.5 h-3.5" />
                <span>冻结画面 (RUN)</span>
              </>
            )}
          </button>

          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-[var(--text-secondary)]">时基窗口</span>
            <select
              value={timeWindowSec}
              onChange={(e) => onTimeWindowChange(Number(e.target.value))}
              className="fluent-input py-1 px-2 text-xs"
            >
              <option value={15}>15 s (1.5s/div)</option>
              <option value={30}>30 s (3s/div)</option>
              <option value={60}>60 s (6s/div)</option>
              <option value={120}>120 s (12s/div)</option>
              <option value={300}>300 s (30s/div)</option>
            </select>
          </div>

          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-[var(--text-secondary)]">纵轴量程</span>
            <select
              value={yScaleMode}
              onChange={(e) => setYScaleMode(e.target.value as 'auto' | 'manual')}
              className="fluent-input py-1 px-2 text-xs"
            >
              <option value="auto">自动 (Auto)</option>
              <option value="manual">手动定标</option>
            </select>
            {yScaleMode === 'manual' && (
              <>
                <input
                  type="number"
                  value={manualYMin}
                  onChange={(e) => setManualYMin(e.target.value)}
                  placeholder="Min"
                  className="fluent-input w-20 py-1 px-2 text-xs font-mono-code"
                />
                <span className="text-[var(--text-secondary)]">~</span>
                <input
                  type="number"
                  value={manualYMax}
                  onChange={(e) => setManualYMax(e.target.value)}
                  placeholder="Max"
                  className="fluent-input w-20 py-1 px-2 text-xs font-mono-code"
                />
              </>
            )}
          </div>

          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-[var(--text-secondary)]">触发</span>
            <select
              value={triggerMode}
              onChange={(e) =>
                setTriggerMode(e.target.value as 'off' | 'rising' | 'falling')
              }
              className="fluent-input py-1 px-2 text-xs"
            >
              <option value="off">关闭</option>
              <option value="rising">上升沿 ↑</option>
              <option value="falling">下降沿 ↓</option>
            </select>
            {triggerMode !== 'off' && (
              <>
                <select
                  value={triggerChannelId}
                  onChange={(e) => setTriggerChannelId(e.target.value)}
                  className="fluent-input py-1 px-2 text-xs max-w-28"
                >
                  {activeChannels.map((ch) => (
                    <option key={ch.id} value={ch.id}>
                      {ch.label}
                    </option>
                  ))}
                </select>
                <input
                  type="number"
                  step="any"
                  value={triggerLevelInput}
                  onChange={(e) => setTriggerLevelInput(e.target.value)}
                  title="触发电平阈值"
                  className="fluent-input w-20 py-1 px-2 text-xs font-mono-code"
                />
              </>
            )}
          </div>

          <label className="inline-flex items-center gap-1.5 text-xs cursor-pointer pl-1">
            <input
              type="checkbox"
              checked={showMarkers}
              onChange={(e) => setShowMarkers(e.target.checked)}
            />
            <span>采样点</span>
          </label>
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={handleExportCsv}
            disabled={samples.length === 0}
            className="fluent-btn h-8 text-xs whitespace-nowrap shrink-0"
          >
            <Download className="w-3.5 h-3.5" />
            <span>导出 CSV</span>
          </button>
          <button
            type="button"
            onClick={onClearSamples}
            disabled={samples.length === 0}
            className="fluent-btn h-8 text-xs whitespace-nowrap shrink-0"
          >
            <RotateCcw className="w-3.5 h-3.5" />
            <span>清空波形</span>
          </button>
        </div>
      </div>

      {/* 通道选择器与实时遥测卡条 */}
      {channels.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {channels.map((ch) => {
            const st = channelStats[ch.id];
            return (
              <button
                key={ch.id}
                type="button"
                onClick={() => onToggleChannel(ch.id)}
                className={`flex items-center gap-2 px-2.5 py-1.5 rounded border text-left text-xs transition-colors cursor-pointer ${
                  ch.enabled
                    ? 'bg-[var(--surface-secondary)] border-[var(--border-subtle)] text-[var(--text-primary)]'
                    : 'bg-transparent border-[var(--border-subtle)] opacity-50 text-[var(--text-secondary)]'
                }`}
              >
                <span
                  className="w-2.5 h-2.5 rounded-xs shrink-0"
                  style={{ backgroundColor: ch.color }}
                />
                <span className="font-medium whitespace-nowrap">{ch.label}</span>
                <span className="font-mono-code tabular-nums font-semibold">
                  {st?.latest !== null && st?.latest !== undefined
                    ? ch.isDigital
                      ? st.latest >= 0.5
                        ? '1 (ON)'
                        : '0 (OFF)'
                      : Number(st.latest.toFixed(3))
                    : '—'}
                </span>
                {!ch.isDigital && st?.min !== null && st?.max !== null && (
                  <span className="text-[11px] font-mono-code tabular-nums text-[var(--text-secondary)]">
                    [{Number(st.min.toFixed(2))}..{Number(st.max.toFixed(2))}]
                  </span>
                )}
              </button>
            );
          })}
        </div>
      )}

      {/* 主示波器显示屏 */}
      <div
        ref={containerRef}
        className="relative w-full h-[330px] rounded border border-[var(--border-subtle)] overflow-hidden bg-[#0B1018]"
      >
        <canvas
          ref={canvasRef}
          onMouseMove={handleMouseMove}
          onMouseLeave={() => setHoverInfo(null)}
          className="block w-full h-full cursor-crosshair"
        />

        {/* 右上角采样态与触发统计文本 */}
        <div className="pointer-events-none absolute top-2.5 right-3 flex items-center gap-3 text-[11px] font-mono-code tabular-nums text-slate-300 bg-slate-950/75 px-2.5 py-1 rounded border border-slate-700/60">
          <span>
            {isFrozen ? 'HOLD' : 'RUN'} · 样本 {samples.length}
          </span>
          {triggerMode !== 'off' && (
            <span className="text-amber-400">
              触发命中 {triggerCrossings.length}
            </span>
          )}
        </div>

        {/* 悬停十字准星数值 HUD */}
        {hoverInfo && (
          <div className="pointer-events-none absolute top-2.5 left-16 bg-slate-950/88 text-slate-100 border border-slate-700/80 rounded px-2.5 py-1.5 text-[11px] font-mono-code tabular-nums space-y-0.5 max-w-xs">
            <div className="flex items-center gap-1.5 text-slate-300">
              <Crosshair className="w-3 h-3" />
              <span>
                {new Date(hoverInfo.sample.timestampMs).toLocaleTimeString()}
              </span>
            </div>
            {activeChannels.slice(0, 6).map((ch) => {
              const v = hoverInfo.sample.values[ch.id];
              return (
                <div key={ch.id} className="flex items-center justify-between gap-3">
                  <span className="flex items-center gap-1.5">
                    <span
                      className="w-2 h-2 rounded-xs inline-block"
                      style={{ backgroundColor: ch.color }}
                    />
                    <span className="truncate max-w-[110px]">{ch.label}</span>
                  </span>
                  <span className="font-semibold">
                    {typeof v === 'number' && Number.isFinite(v)
                      ? ch.isDigital
                        ? v >= 0.5
                          ? '1 (ON)'
                          : '0 (OFF)'
                        : Number(v.toFixed(4))
                      : '—'}
                  </span>
                </div>
              );
            })}
          </div>
        )}

        {/* 空状态提示 */}
        {samples.length === 0 && (
          <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center text-slate-400 text-xs gap-1">
            <span>示波器等待采样数据</span>
            <span className="text-[11px] text-slate-500">
              点击“读取一次” / “开始轮询”，或切换至“内置服务器实时监听”捕获波形
            </span>
          </div>
        )}
      </div>
    </div>
  );
};
