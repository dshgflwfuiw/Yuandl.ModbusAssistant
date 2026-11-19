import React, { useState } from 'react';
import { Search, Trash2, Radio, Pause } from 'lucide-react';
import type { ModbusTransport } from '../core/types';
import { useAppContext } from '../context/AppContext';

const TRANSPORT_OPTIONS: ModbusTransport[] = ['Rtu', 'Ascii', 'Tcp'];

export const PacketAnalysisPage: React.FC = () => {
  const {
    isPacketCaptureEnabled,
    setIsPacketCaptureEnabled,
    packetCaptureTransport,
    setPacketCaptureTransport,
    packetEntries,
    packetStats,
    analyzeManualPacket,
    clearPacketEntries,
  } = useAppContext();

  const [manualTransport, setManualTransport] = useState<ModbusTransport>('Rtu');
  const [inputHex, setInputHex] = useState<string>('01 03 04 00 64 00 C8 FA 3D');
  const [analysisResult, setAnalysisResult] = useState<string>('');

  const handleAnalyze = () => {
    const res = analyzeManualPacket(inputHex, manualTransport);
    setAnalysisResult(res);
  };

  const handleClear = () => {
    clearPacketEntries();
    setAnalysisResult('');
  };

  return (
    <div className="h-full overflow-y-auto p-6 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">报文分析</h1>
          <p className="text-xs text-[var(--text-secondary)] mt-1">
            自动实时捕获全链路收发报文，关联请求响应、校验 CRC/LRC 并统计往返时延 (RTT)。
          </p>
        </div>

        <div className="flex items-center gap-2">
          <select
            value={packetCaptureTransport}
            onChange={(e) =>
              setPacketCaptureTransport(
                e.target.value as ModbusTransport | 'Auto'
              )
            }
            className="fluent-input text-xs py-1.5 w-36"
            title="实时捕获解析模式"
          >
            <option value="Auto">捕获协议: 自动</option>
            <option value="Tcp">捕获协议: TCP</option>
            <option value="Rtu">捕获协议: RTU</option>
            <option value="Ascii">捕获协议: ASCII</option>
          </select>

          <button
            type="button"
            onClick={() => setIsPacketCaptureEnabled(!isPacketCaptureEnabled)}
            className={`fluent-btn text-xs ${
              isPacketCaptureEnabled ? 'fluent-btn-primary' : ''
            }`}
          >
            {isPacketCaptureEnabled ? (
              <>
                <Pause className="w-3.5 h-3.5" />
                <span>实时捕获中</span>
              </>
            ) : (
              <>
                <Radio className="w-3.5 h-3.5" />
                <span>开启实时捕获</span>
              </>
            )}
          </button>
        </div>
      </div>

      {/* 时延与报文统计摘要栏 */}
      <div className="fluent-card px-5 py-3 flex flex-wrap items-center gap-6 text-xs tabular-nums">
        <div>
          <span className="text-[var(--text-secondary)]">总报文数: </span>
          <strong className="text-sm">{packetStats.totalPackets}</strong>
        </div>
        <span className="h-4 w-px bg-[var(--border-subtle)]" />
        <div>
          <span className="text-[var(--text-secondary)]">校验/异常错误: </span>
          <strong
            className={`text-sm ${
              packetStats.errorPackets > 0 ? 'text-[var(--danger)]' : ''
            }`}
          >
            {packetStats.errorPackets}
          </strong>
        </div>
        <span className="h-4 w-px bg-[var(--border-subtle)]" />
        <div>
          <span className="text-[var(--text-secondary)]">已匹配问答对: </span>
          <strong className="text-sm">{packetStats.matchedPairs}</strong>
        </div>
        <span className="h-4 w-px bg-[var(--border-subtle)]" />
        <div>
          <span className="text-[var(--text-secondary)]">平均 RTT: </span>
          <strong className="text-sm font-mono-code">
            {packetStats.avgRttMs !== null ? `${packetStats.avgRttMs} ms` : '—'}
          </strong>
        </div>
        <span className="h-4 w-px bg-[var(--border-subtle)]" />
        <div>
          <span className="text-[var(--text-secondary)]">最小 / 最大 RTT: </span>
          <strong className="text-sm font-mono-code">
            {packetStats.minRttMs !== null && packetStats.maxRttMs !== null
              ? `${packetStats.minRttMs} / ${packetStats.maxRttMs} ms`
              : '—'}
          </strong>
        </div>
      </div>

      <div className="fluent-card p-5 space-y-4">
        <div className="flex flex-wrap items-center gap-2">
          <select
            value={manualTransport}
            onChange={(e) =>
              setManualTransport(e.target.value as ModbusTransport)
            }
            className="fluent-input w-28"
          >
            {TRANSPORT_OPTIONS.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>

          <input
            type="text"
            value={inputHex}
            onChange={(e) => setInputHex(e.target.value)}
            placeholder="手动输入待分析的 Modbus HEX 报文..."
            className="fluent-input font-mono-code flex-1 min-w-[260px]"
          />

          <button
            type="button"
            onClick={handleAnalyze}
            className="fluent-btn fluent-btn-primary"
          >
            <Search className="w-3.5 h-3.5" />
            <span>手动分析</span>
          </button>

          <button type="button" onClick={handleClear} className="fluent-btn">
            <Trash2 className="w-3.5 h-3.5" />
            <span>清空</span>
          </button>
        </div>

        <div className="text-xs text-[var(--text-secondary)]">
          {analysisResult || '已开启全局 TX/RX 自动捕获；也可在上方粘贴单帧 HEX 手动解析'}
        </div>

        <div className="border border-[var(--border-subtle)] rounded overflow-x-auto min-h-[300px] max-h-[420px] overflow-y-auto">
          <table className="w-full text-xs text-left border-collapse">
            <thead className="bg-[var(--surface-secondary)] sticky top-0 border-b border-[var(--border-subtle)]">
              <tr>
                <th className="px-3 py-2 font-semibold w-28">时间</th>
                <th className="px-3 py-2 font-semibold w-16">方向</th>
                <th className="px-3 py-2 font-semibold w-16">协议</th>
                <th className="px-3 py-2 font-semibold w-24">RTT 时延</th>
                <th className="px-3 py-2 font-semibold">HEX 报文</th>
                <th className="px-3 py-2 font-semibold">解析结果</th>
              </tr>
            </thead>
            <tbody>
              {packetEntries.length === 0 ? (
                <tr>
                  <td
                    colSpan={6}
                    className="px-3 py-8 text-center text-[var(--text-secondary)]"
                  >
                    暂无分析记录，进行通讯收发或点击“手动分析”后将自动展示关联解析结果
                  </td>
                </tr>
              ) : (
                packetEntries.map((entry) => (
                  <tr
                    key={entry.id}
                    className="border-b border-[var(--border-subtle)] last:border-b-0 hover:bg-[var(--surface-hover)]"
                  >
                    <td className="px-3 py-2 tabular-nums text-[var(--text-secondary)]">
                      {entry.timestamp}
                    </td>
                    <td className="px-3 py-2">
                      <span
                        className={`px-1.5 py-0.5 rounded text-[11px] font-semibold ${
                          entry.direction === 'ERR'
                            ? 'bg-[#C42B1C]/15 text-[#C42B1C]'
                            : entry.direction === 'TX'
                              ? 'bg-[#107C41]/15 text-[#107C41]'
                              : 'bg-[#0078D4]/15 text-[#0078D4]'
                        }`}
                      >
                        {entry.direction}
                      </span>
                    </td>
                    <td className="px-3 py-2">{entry.transport}</td>
                    <td className="px-3 py-2 font-mono-code tabular-nums">
                      {entry.rttMs !== undefined ? `${entry.rttMs} ms` : '—'}
                    </td>
                    <td className="px-3 py-2 font-mono-code break-all max-w-[300px]">
                      {entry.hex}
                    </td>
                    <td className="px-3 py-2">
                      {entry.sessionLabel ? `${entry.sessionLabel} · ` : ''}
                      {entry.result}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};
