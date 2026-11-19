import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Eraser, Send, Trash2 } from 'lucide-react';
import type { DataEncoding, EndingDelimiter } from '../core/types';
import { ByteUtils } from '../core/utils/byteUtils';
import { useAppContext } from '../context/AppContext';

interface RawMessageItem {
  id: string;
  isTx: boolean;
  rawBytes: Uint8Array;
  formattedText: string;
  formattedTimestamp: string;
  sessionLabel?: string;
}

const MAX_MESSAGE_COUNT = 1000;
const ENCODING_OPTIONS: DataEncoding[] = ['Hex', 'Ascii', 'Utf8'];
const DELIMITER_OPTIONS: EndingDelimiter[] = ['None', 'CR', 'LF', 'CRLF'];

function formatTime(date: Date): string {
  const hh = String(date.getHours()).padStart(2, '0');
  const mm = String(date.getMinutes()).padStart(2, '0');
  const ss = String(date.getSeconds()).padStart(2, '0');
  const ms = String(date.getMilliseconds()).padStart(3, '0');
  return `${hh}:${mm}:${ss}.${ms}`;
}

export const RawDebugPage: React.FC = () => {
  const { status, showSnackbar, subscribeDataReceived } = useAppContext();

  const [sendEncoding, setSendEncoding] = useState<DataEncoding>('Hex');
  const [receiveEncoding, setReceiveEncoding] = useState<DataEncoding>('Hex');
  const [selectedDelimiter, setSelectedDelimiter] =
    useState<EndingDelimiter>('None');
  const [inputText, setInputText] = useState<string>('01 03 00 00 00 02 C4 0B');
  const [isPauseReceive, setIsPauseReceive] = useState<boolean>(false);
  const [isAutoScroll, setIsAutoScroll] = useState<boolean>(true);
  const [showTimestamp, setShowTimestamp] = useState<boolean>(true);
  const [isLoopSend, setIsLoopSend] = useState<boolean>(false);
  const [loopIntervalMs, setLoopIntervalMs] = useState<number>(1000);
  const [totalTxBytes, setTotalTxBytes] = useState<number>(0);
  const [totalRxBytes, setTotalRxBytes] = useState<number>(0);
  const [totalTxPackets, setTotalTxPackets] = useState<number>(0);
  const [totalRxPackets, setTotalRxPackets] = useState<number>(0);
  const [messages, setMessages] = useState<RawMessageItem[]>([]);
  const [sendHistory, setSendHistory] = useState<string[]>([]);
  const [selectedClientSessionId, setSelectedClientSessionId] =
    useState<string>('');

  const listRef = useRef<HTMLDivElement | null>(null);
  const receiveEncodingRef = useRef(receiveEncoding);
  receiveEncodingRef.current = receiveEncoding;
  const pauseRef = useRef(isPauseReceive);
  pauseRef.current = isPauseReceive;

  const isTcpServerMode = status.channelType === 'TcpServer';

  // Keep selected client session valid when clients connect/disconnect
  useEffect(() => {
    if (!isTcpServerMode) {
      setSelectedClientSessionId('');
      return;
    }
    const exists = status.tcpServerClients.some(
      (c) => c.sessionId === selectedClientSessionId
    );
    if (!exists) {
      setSelectedClientSessionId(status.tcpServerClients[0]?.sessionId ?? '');
    }
  }, [isTcpServerMode, status.tcpServerClients, selectedClientSessionId]);

  const appendMessage = useCallback((item: RawMessageItem) => {
    setMessages((prev) => {
      const next =
        prev.length >= MAX_MESSAGE_COUNT
          ? [...prev.slice(prev.length - MAX_MESSAGE_COUNT + 1), item]
          : [...prev, item];
      return next;
    });
  }, []);

  useEffect(() => {
    if (isAutoScroll && listRef.current) {
      listRef.current.scrollTop = listRef.current.scrollHeight;
    }
  }, [messages, isAutoScroll]);

  const handleReceiveEncodingChange = (nextEncoding: DataEncoding) => {
    setReceiveEncoding(nextEncoding);
    setMessages((prev) =>
      prev.map((m) => ({
        ...m,
        formattedText: ByteUtils.decodeReceived(m.rawBytes, nextEncoding),
      }))
    );
  };

  useEffect(() => {
    return subscribeDataReceived((evt) => {
      if (pauseRef.current) return;
      const bytes = evt.bytes;
      setTotalRxBytes((n) => n + bytes.length);
      setTotalRxPackets((n) => n + 1);
      appendMessage({
        id: `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
        isTx: false,
        rawBytes: bytes,
        formattedText: ByteUtils.decodeReceived(bytes, receiveEncodingRef.current),
        formattedTimestamp: formatTime(new Date(evt.timestamp)),
        sessionLabel: evt.sessionLabel,
      });
    });
  }, [subscribeDataReceived, appendMessage]);

  const sendOnce = useCallback(async () => {
    if (!inputText.trim()) {
      showSnackbar('发送提示', '请输入待发送的数据内容', 'info');
      return;
    }
    if (!status.isConnected) {
      showSnackbar(
        '无法发送',
        '当前尚未建立通讯连接，请先前往 [通讯连接] 页面打开通道',
        'warning'
      );
      setIsLoopSend(false);
      return;
    }
    if (isTcpServerMode && !selectedClientSessionId) {
      showSnackbar(
        '无法发送',
        '服务端模式下请先选择目标客户端会话后再发送',
        'warning'
      );
      setIsLoopSend(false);
      return;
    }

    let rawBytes: Uint8Array;
    try {
      rawBytes = ByteUtils.encodeInput(inputText, sendEncoding, selectedDelimiter);
    } catch (err) {
      showSnackbar(
        '数据格式错误',
        err instanceof Error ? err.message : '编码失败',
        'error'
      );
      setIsLoopSend(false);
      return;
    }

    if (rawBytes.length === 0) return;

    try {
      const res = await fetch('/api/comm/send', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          bytes: Array.from(rawBytes),
          sessionId: isTcpServerMode ? selectedClientSessionId : undefined,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        showSnackbar('发送失败', data.error || '发送异常', 'error');
        setIsLoopSend(false);
        return;
      }

      const targetSession = isTcpServerMode
        ? status.tcpServerClients.find(
            (c) => c.sessionId === selectedClientSessionId
          )
        : undefined;
      const sessionLabel = targetSession
        ? `${targetSession.sessionId} · ${targetSession.remoteAddress}:${targetSession.remotePort}`
        : undefined;

      setTotalTxBytes((n) => n + rawBytes.length);
      setTotalTxPackets((n) => n + 1);
      appendMessage({
        id: `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
        isTx: true,
        rawBytes,
        formattedText: ByteUtils.decodeReceived(rawBytes, sendEncoding),
        formattedTimestamp: formatTime(new Date()),
        sessionLabel,
      });

      setSendHistory((prev) => {
        if (prev.includes(inputText)) return prev;
        return [inputText, ...prev].slice(0, 30);
      });
    } catch (err) {
      showSnackbar(
        '发送失败',
        err instanceof Error ? err.message : '网络异常',
        'error'
      );
      setIsLoopSend(false);
    }
  }, [
    inputText,
    status.isConnected,
    status.tcpServerClients,
    isTcpServerMode,
    selectedClientSessionId,
    sendEncoding,
    selectedDelimiter,
    showSnackbar,
    appendMessage,
  ]);

  useEffect(() => {
    if (!isLoopSend) return;
    if (!status.isConnected) {
      setIsLoopSend(false);
      return;
    }
    const interval = Math.max(10, loopIntervalMs);
    const timer = window.setInterval(() => {
      void sendOnce();
    }, interval);
    return () => window.clearInterval(timer);
  }, [isLoopSend, loopIntervalMs, status.isConnected, sendOnce]);

  const handleClearMessages = () => {
    setMessages([]);
    setTotalTxBytes(0);
    setTotalRxBytes(0);
    setTotalTxPackets(0);
    setTotalRxPackets(0);
  };

  return (
    <div className="h-full flex flex-col p-4 gap-3 overflow-hidden">
      {/* 1. 顶部控制栏卡片 */}
      <div className="fluent-card px-3.5 py-2.5 flex flex-wrap items-center justify-between gap-3 shrink-0">
        <div className="flex flex-wrap items-center gap-4 text-xs">
          <div className="flex items-center gap-1.5">
            <span className="text-[var(--text-secondary)]">接收格式:</span>
            <select
              value={receiveEncoding}
              onChange={(e) =>
                handleReceiveEncodingChange(e.target.value as DataEncoding)
              }
              className="fluent-input py-1 w-24"
            >
              {ENCODING_OPTIONS.map((enc) => (
                <option key={enc} value={enc}>
                  {enc}
                </option>
              ))}
            </select>
          </div>

          <label className="inline-flex items-center gap-1.5 cursor-pointer">
            <input
              type="checkbox"
              checked={isPauseReceive}
              onChange={(e) => setIsPauseReceive(e.target.checked)}
            />
            <span>暂停接收</span>
          </label>

          <label className="inline-flex items-center gap-1.5 cursor-pointer">
            <input
              type="checkbox"
              checked={isAutoScroll}
              onChange={(e) => setIsAutoScroll(e.target.checked)}
            />
            <span>自动滚动</span>
          </label>

          <label className="inline-flex items-center gap-1.5 cursor-pointer">
            <input
              type="checkbox"
              checked={showTimestamp}
              onChange={(e) => setShowTimestamp(e.target.checked)}
            />
            <span>显示时间戳</span>
          </label>

          <div className="flex items-center gap-2 text-[var(--text-secondary)] tabular-nums">
            <span>
              TX: <strong className="text-[var(--text-primary)]">{totalTxPackets}</strong>{' '}
              帧 / <strong className="text-[var(--text-primary)]">{totalTxBytes}</strong> B
            </span>
            <span>|</span>
            <span>
              RX: <strong className="text-[var(--text-primary)]">{totalRxPackets}</strong>{' '}
              帧 / <strong className="text-[var(--text-primary)]">{totalRxBytes}</strong> B
            </span>
          </div>
        </div>

        <button
          type="button"
          onClick={handleClearMessages}
          className="fluent-btn text-xs py-1"
        >
          <Eraser className="w-3.5 h-3.5" />
          <span>清空记录</span>
        </button>
      </div>

      {/* 2. 中部数据流展示区 */}
      <div
        ref={listRef}
        className="flex-1 min-h-0 overflow-y-auto rounded-md border border-[var(--border-subtle)] bg-[var(--surface-secondary)] p-2 space-y-1.5"
      >
        {messages.length === 0 ? (
          <div className="h-full flex items-center justify-center text-xs text-[var(--text-secondary)]">
            暂无收发报文记录，建立连接或启动 TCP Server 监听后即可进行原始字节流收发
          </div>
        ) : (
          messages.map((msg) => (
            <div
              key={msg.id}
              className="rounded px-2.5 py-1.5 bg-[var(--surface-card)] border border-[var(--border-subtle)] flex items-start gap-2.5 text-xs"
            >
              {showTimestamp && (
                <span className="font-mono-code text-[var(--text-secondary)] shrink-0 mt-0.5 tabular-nums">
                  [{msg.formattedTimestamp}]
                </span>
              )}
              <span
                className={`px-1.5 py-0.5 rounded text-[11px] font-semibold shrink-0 ${
                  msg.isTx
                    ? 'bg-[#107C41]/15 text-[#107C41]'
                    : 'bg-[#0078D4]/15 text-[#0078D4]'
                }`}
              >
                {msg.isTx ? 'TX' : 'RX'}
              </span>
              {msg.sessionLabel && (
                <span className="font-mono-code text-[11px] text-[var(--text-secondary)] shrink-0 mt-0.5">
                  {msg.sessionLabel} ·
                </span>
              )}
              <span className="text-[11px] text-[var(--text-secondary)] shrink-0 mt-0.5 tabular-nums">
                ({msg.rawBytes.length} B)
              </span>
              <span className="font-mono-code text-[13px] break-all flex-1 select-all">
                {msg.formattedText}
              </span>
            </div>
          ))
        )}
      </div>

      {/* 3. 底部发送控制台 */}
      <div className="fluent-card p-3.5 space-y-2.5 shrink-0">
        <div className="flex flex-wrap items-center gap-4 text-xs">
          {isTcpServerMode && (
            <div className="flex items-center gap-1.5">
              <span className="text-[var(--text-secondary)] font-medium">
                目标客户端:
              </span>
              <select
                value={selectedClientSessionId}
                onChange={(e) => setSelectedClientSessionId(e.target.value)}
                className="fluent-input py-1 w-56 font-mono-code"
              >
                {status.tcpServerClients.length === 0 ? (
                  <option value="">暂无在线客户端</option>
                ) : (
                  status.tcpServerClients.map((c) => (
                    <option key={c.sessionId} value={c.sessionId}>
                      {c.sessionId} · {c.remoteAddress}:{c.remotePort}
                    </option>
                  ))
                )}
              </select>
            </div>
          )}

          <div className="flex items-center gap-1.5">
            <span className="text-[var(--text-secondary)]">发送格式:</span>
            <select
              value={sendEncoding}
              onChange={(e) => setSendEncoding(e.target.value as DataEncoding)}
              className="fluent-input py-1 w-24"
            >
              {ENCODING_OPTIONS.map((enc) => (
                <option key={enc} value={enc}>
                  {enc}
                </option>
              ))}
            </select>
          </div>

          <div className="flex items-center gap-1.5">
            <span className="text-[var(--text-secondary)]">追加结束符:</span>
            <select
              value={selectedDelimiter}
              onChange={(e) =>
                setSelectedDelimiter(e.target.value as EndingDelimiter)
              }
              className="fluent-input py-1 w-24"
            >
              {DELIMITER_OPTIONS.map((delim) => (
                <option key={delim} value={delim}>
                  {delim}
                </option>
              ))}
            </select>
          </div>

          <div className="flex items-center gap-1.5">
            <label className="inline-flex items-center gap-1.5 cursor-pointer">
              <input
                type="checkbox"
                checked={isLoopSend}
                onChange={(e) => setIsLoopSend(e.target.checked)}
              />
              <span>定时循环发送</span>
            </label>
            <input
              type="number"
              min={10}
              value={loopIntervalMs}
              onChange={(e) => setLoopIntervalMs(Number(e.target.value))}
              className="fluent-input py-1 w-16 text-center"
            />
            <span className="text-[var(--text-secondary)]">ms</span>
          </div>

          <div className="flex items-center gap-1.5">
            <span className="text-[var(--text-secondary)]">发送历史:</span>
            <select
              value=""
              onChange={(e) => {
                if (e.target.value) setInputText(e.target.value);
              }}
              className="fluent-input py-1 w-48"
            >
              <option value="">选择历史记录 ({sendHistory.length})</option>
              {sendHistory.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
            <button
              type="button"
              onClick={() => setSendHistory([])}
              title="清空历史"
              className="fluent-btn p-1.5"
            >
              <Trash2 className="w-3.5 h-3.5" />
            </button>
          </div>
        </div>

        <div className="flex gap-3">
          <textarea
            value={inputText}
            onChange={(e) => setInputText(e.target.value)}
            className="fluent-input font-mono-code flex-1 h-16 resize-none text-sm"
            placeholder="输入 HEX 字节或文本内容..."
          />
          <button
            type="button"
            onClick={() => void sendOnce()}
            className="fluent-btn fluent-btn-primary w-28 h-16"
          >
            <Send className="w-4 h-4" />
            <span>发送数据</span>
          </button>
        </div>
      </div>
    </div>
  );
};
