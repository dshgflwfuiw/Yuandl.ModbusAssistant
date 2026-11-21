import React, { useMemo, useState } from 'react';
import { Download, Search, Trash2, Radio, Pause } from 'lucide-react';
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
  const [directionFilter, setDirectionFilter] = useState<'all' | 'TX' | 'RX' | 'ERR'>('all');
  const [functionCodeFilter, setFunctionCodeFilter] = useState('all');
  const [searchText, setSearchText] = useState('');

  const filteredEntries = useMemo(() => {
    const query = searchText.trim().toLowerCase();
    return packetEntries.filter((entry) =>
      (directionFilter === 'all' || entry.direction === directionFilter) &&
      (functionCodeFilter === 'all' || new RegExp(`FC\\s+0x${functionCodeFilter}`, 'i').test(entry.result)) &&
      (!query || `${entry.hex} ${entry.result} ${entry.sessionLabel ?? ''} ${entry.transport}`.toLowerCase().includes(query))
    );
  }, [packetEntries, directionFilter, functionCodeFilter, searchText]);

  const handleAnalyze = () => {
    const res = analyzeManualPacket(inputHex, manualTransport);
    setAnalysisResult(res);
  };

  const handleClear = () => {
    clearPacketEntries();
    setAnalysisResult('');
  };

  const handleExport = () => {
    if (filteredEntries.length === 0) return;
    const quote = (value: string | number) => `"${String(value).replaceAll('"', '""')}"`;
    const rows = [
      ['时间', '方向', '协议', 'RTT(ms)', '会话', 'HEX', '解析结果'].map(quote).join(','),
      ...filteredEntries.map((entry) => [entry.timestamp, entry.direction, entry.transport, entry.rttMs ?? '', entry.sessionLabel ?? '', entry.hex, entry.result].map(quote).join(',')),
    ];
    const blob = new Blob([`\uFEFF${rows.join('\r\n')}`], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `modbus-packets-${new Date().toISOString().replaceAll(':', '-')}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="h-full overflow-y-auto p-3 sm:p-6 space-y-4">
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
          <label className="text-xs text-[var(--text-secondary)]" htmlFor="packet-direction-filter">方向</label>
          <select id="packet-direction-filter" value={directionFilter} onChange={(e) => setDirectionFilter(e.target.value as typeof directionFilter)} className="fluent-input text-xs py-1.5">
            <option value="all">全部</option><option value="TX">TX 发送</option><option value="RX">RX 接收</option><option value="ERR">ERR 错误</option>
          </select>
          <label className="text-xs text-[var(--text-secondary)]" htmlFor="packet-function-filter">功能码</label>
          <select id="packet-function-filter" value={functionCodeFilter} onChange={(e) => setFunctionCodeFilter(e.target.value)} className="fluent-input text-xs py-1.5">
            <option value="all">全部</option>
            {[1, 2, 3, 4, 5, 6, 15, 16].map((fc) => <option key={fc} value={fc.toString(16).padStart(2, '0')}>FC 0x{fc.toString(16).toUpperCase().padStart(2, '0')}</option>)}
          </select>
          <input value={searchText} onChange={(e) => setSearchText(e.target.value)} placeholder="搜索 HEX、解析结果或会话" aria-label="搜索报文" className="fluent-input text-xs min-w-[180px] flex-1" />
          <button type="button" onClick={handleExport} disabled={filteredEntries.length === 0} className="fluent-btn text-xs" title="导出当前筛选结果">
            <Download className="w-3.5 h-3.5" /><span>导出 CSV ({filteredEntries.length})</span>
          </button>
        </div>
        <div className="text-[11px] text-[var(--text-secondary)]" aria-live="polite">
          显示 {filteredEntries.length} 条 / 最多保留 500 条{packetEntries.length >= 500 ? '（已达到容量上限，旧记录会被移除）' : ''}
        </div>
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
            className="fluent-input font-mono-code flex-1 min-w-0 sm:min-w-[260px]"
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
              {filteredEntries.length === 0 ? (
                <tr>
                  <td
                    colSpan={6}
                    className="px-3 py-8 text-center text-[var(--text-secondary)]"
                  >
                    暂无分析记录，进行通讯收发或点击“手动分析”后将自动展示关联解析结果
                  </td>
                </tr>
              ) : (
                filteredEntries.map((entry) => (
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
