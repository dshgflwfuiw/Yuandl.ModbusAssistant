import React, { useMemo, useRef, useState } from 'react';
import { Hammer, Send, FileSearch } from 'lucide-react';
import type { ModbusTransport } from '../core/types';
import { ByteUtils } from '../core/utils/byteUtils';
import { ModbusFrame } from '../protocol/modbus/modbusFrame';
import { ModbusException } from '../protocol/modbus/modbusException';
import { useAppContext } from '../context/AppContext';

const TRANSPORT_OPTIONS: ModbusTransport[] = ['Rtu', 'Ascii', 'Tcp'];
const FUNCTION_CODE_OPTIONS = [
  '01: 读取线圈',
  '02: 读取离散输入',
  '03: 读取保持寄存器',
  '04: 读取输入寄存器',
  '05: 写单个线圈',
  '06: 写单个寄存器',
  '0F: 写多个线圈',
  '10: 写多个寄存器',
];

export const ModbusDebugPage: React.FC = () => {
  const { settings, status } = useAppContext();
  const [slaveId, setSlaveId] = useState<number>(1);
  const [selectedTransport, setSelectedTransport] = useState<ModbusTransport>(
    status.channelType === 'TcpClient' ? 'Tcp' : 'Rtu'
  );
  const [selectedFunctionCode, setSelectedFunctionCode] = useState<string>(
    '03: 读取保持寄存器'
  );
  const [startAddress, setStartAddress] = useState<number>(0);
  const [quantity, setQuantity] = useState<number>(1);
  const [writeValue, setWriteValue] = useState<number>(0);
  const [multiValuesText, setMultiValuesText] = useState<string>('100, 200, 0x012C');
  const [requestHex, setRequestHex] = useState<string>('');
  const [responseHex, setResponseHex] = useState<string>('');
  const [statusText, setStatusText] = useState<string>('就绪');
  const [resultText, setResultText] = useState<string>('');
  const [isSending, setIsSending] = useState<boolean>(false);
  const txIdRef = useRef<number>(1);

  const fnCode = Number.parseInt(selectedFunctionCode.slice(0, 2), 16);
  const isBatchWrite = fnCode === 15 || fnCode === 16;
  const isSingleWrite = fnCode === 5 || fnCode === 6;

  const batchPreview = useMemo(() => {
    if (!isBatchWrite) return { count: quantity, error: null as string | null };
    try {
      const vals = ModbusFrame.parseMultiValues(multiValuesText, fnCode === 15);
      return { count: vals.length, error: null as string | null };
    } catch (err) {
      return {
        count: 0,
        error: err instanceof Error ? err.message : '数值格式无效',
      };
    }
  }, [isBatchWrite, multiValuesText, fnCode, quantity]);

  const handleFunctionChange = (nextOption: string) => {
    setSelectedFunctionCode(nextOption);
    const nextCode = Number.parseInt(nextOption.slice(0, 2), 16);
    if (nextCode === 15 && multiValuesText === '100, 200, 0x012C') {
      setMultiValuesText('true, false, 1, 0');
    } else if (nextCode === 16 && multiValuesText === 'true, false, 1, 0') {
      setMultiValuesText('100, 200, 0x012C');
    }
  };

  const buildRequest = (): string => {
    const txId = txIdRef.current++;
    if (txIdRef.current > 65535) txIdRef.current = 1;

    let frame: Uint8Array;
    if (isSingleWrite) {
      frame = ModbusFrame.buildWriteSingle(
        slaveId,
        fnCode,
        startAddress,
        writeValue,
        selectedTransport,
        txId
      );
    } else if (isBatchWrite) {
      const values = ModbusFrame.parseMultiValues(multiValuesText, fnCode === 15);
      setQuantity(values.length);
      frame = ModbusFrame.buildWriteMultiple(
        slaveId,
        fnCode,
        startAddress,
        values,
        selectedTransport,
        txId
      );
    } else {
      frame = ModbusFrame.buildRequest(
        slaveId,
        fnCode,
        startAddress,
        quantity,
        selectedTransport,
        txId
      );
    }
    const hex = ByteUtils.bytesToHex(frame);
    setRequestHex(hex);
    return hex;
  };

  const parseResponseHex = (hexToParse: string) => {
    try {
      const response = ModbusFrame.parseResponse(
        ByteUtils.hexToBytes(hexToParse),
        selectedTransport
      );
      if (response.isException) {
        const code = response.exceptionCode ?? 0;
        setResultText(
          `异常码 0x${code.toString(16).toUpperCase().padStart(2, '0')} (${ModbusException.describe(code)})`
        );
      } else if (response.functionCode === 15 || response.functionCode === 16) {
        const addr =
          response.payload.length >= 4
            ? (response.payload[0] << 8) | response.payload[1]
            : startAddress;
        const cnt =
          response.payload.length >= 4
            ? (response.payload[2] << 8) | response.payload[3]
            : batchPreview.count;
        setResultText(
          `批量写入成功 · 功能码 0x${response.functionCode.toString(16).toUpperCase().padStart(2, '0')} · 起始地址 ${addr} · 写入数量 ${cnt}`
        );
      } else if (response.functionCode === 1 || response.functionCode === 2) {
        const byteCount = response.payload[0] ?? 0;
        const bits: string[] = [];
        for (let b = 0; b < byteCount && 1 + b < response.payload.length; b++) {
          const byteVal = response.payload[1 + b];
          for (let bit = 0; bit < 8; bit++) {
            bits.push(((byteVal >> bit) & 1) === 1 ? '1' : '0');
          }
        }
        setResultText(
          `功能码 0x${response.functionCode.toString(16).toUpperCase().padStart(2, '0')}，线圈/离散量: [${bits.slice(0, quantity).join(', ')}]`
        );
      } else {
        const regsText =
          response.registers.length > 0
            ? `，寄存器值: [${response.registers.join(', ')}]`
            : '';
        setResultText(
          `功能码 0x${response.functionCode.toString(16).toUpperCase().padStart(2, '0')}，寄存器数 ${response.registers.length}${regsText}`
        );
      }
    } catch (err) {
      setResultText(err instanceof Error ? err.message : '解析失败');
    }
  };

  const handleBuild = () => {
    try {
      buildRequest();
      setStatusText('请求报文已生成');
    } catch (err) {
      setStatusText(`生成失败：${err instanceof Error ? err.message : '参数无效'}`);
    }
  };

  const handleSend = async () => {
    setIsSending(true);
    try {
      const hex = buildRequest();
      const res = await fetch('/api/modbus/exchange', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          requestHex: hex,
          timeoutMs: settings.defaultTimeoutMs,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setStatusText(`请求失败：${data.error || '发送失败'}`);
        return;
      }
      setResponseHex(data.responseHex);
      parseResponseHex(data.responseHex);
      setStatusText('请求完成');
    } catch (err) {
      setStatusText(`请求失败：${err instanceof Error ? err.message : '网络错误'}`);
    } finally {
      setIsSending(false);
    }
  };

  return (
    <div className="h-full overflow-y-auto p-6 space-y-4">
      <div>
        <h1 className="text-xl font-semibold">Modbus 调试</h1>
        <p className="text-xs text-[var(--text-secondary)] mt-1">
          自动生成 RTU、ASCII、TCP 请求（支持 0F/10 多值批量写入），校验 CRC/LRC 并解析响应。
        </p>
      </div>

      <div className="fluent-card p-5 space-y-4">
        <div className="flex flex-wrap items-end gap-3">
          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              从站地址
            </label>
            <input
              type="number"
              min={1}
              max={247}
              value={slaveId}
              onChange={(e) => setSlaveId(Number(e.target.value))}
              className="fluent-input w-20"
            />
          </div>

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              传输模式
            </label>
            <select
              value={selectedTransport}
              onChange={(e) =>
                setSelectedTransport(e.target.value as ModbusTransport)
              }
              className="fluent-input w-36"
            >
              {TRANSPORT_OPTIONS.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              功能码
            </label>
            <select
              value={selectedFunctionCode}
              onChange={(e) => handleFunctionChange(e.target.value)}
              className="fluent-input w-56"
            >
              {FUNCTION_CODE_OPTIONS.map((opt) => (
                <option key={opt} value={opt}>
                  {opt}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              起始地址
            </label>
            <input
              type="number"
              min={0}
              max={65535}
              value={startAddress}
              onChange={(e) => setStartAddress(Number(e.target.value))}
              className="fluent-input w-24"
            />
          </div>

          {!isSingleWrite && (
            <div>
              <label className="block text-xs text-[var(--text-secondary)] mb-1">
                数量
              </label>
              <input
                type="number"
                min={1}
                max={2000}
                value={isBatchWrite ? batchPreview.count : quantity}
                disabled={isBatchWrite}
                onChange={(e) => setQuantity(Number(e.target.value))}
                className="fluent-input w-24"
              />
            </div>
          )}

          {isSingleWrite && (
            <div>
              <label className="block text-xs text-[var(--text-secondary)] mb-1">
                {fnCode === 5 ? '写入值 (0 / 0xFF00=65280)' : '写入值'}
              </label>
              <input
                type="number"
                min={0}
                max={65535}
                value={writeValue}
                onChange={(e) => setWriteValue(Number(e.target.value))}
                className="fluent-input w-36"
              />
            </div>
          )}
        </div>

        {/* 0F / 10 批量自定义值列表编辑区 */}
        {isBatchWrite && (
          <div className="space-y-1.5 p-3 rounded bg-[var(--surface-secondary)] border border-[var(--border-subtle)]">
            <div className="flex items-center justify-between text-xs">
              <span className="font-semibold">
                {fnCode === 15 ? '批量写入线圈值列表 (0F)' : '批量写入寄存器值列表 (10)'}
              </span>
              <span
                className={
                  batchPreview.error
                    ? 'text-[var(--danger)]'
                    : 'text-[var(--text-secondary)] tabular-nums'
                }
              >
                {batchPreview.error
                  ? batchPreview.error
                  : `已解析 ${batchPreview.count} 项`}
              </span>
            </div>
            <textarea
              value={multiValuesText}
              onChange={(e) => setMultiValuesText(e.target.value)}
              rows={2}
              className="fluent-input font-mono-code w-full text-xs resize-y"
              placeholder={
                fnCode === 15
                  ? '输入线圈值（支持 true/false/1/0），用逗号、空格或换行分隔'
                  : '输入寄存器值（十进制 0~65535 或 0x 十六进制），用逗号、空格或换行分隔'
              }
            />
            <div className="text-[11px] text-[var(--text-secondary)]">
              {fnCode === 15
                ? '支持 true / false / 1 / 0，以逗号、空格或换行分隔（最多 1968 项）'
                : '支持十进制 (0~65535) 与十六进制 (0x0000~0xFFFF)，以逗号、空格或换行分隔（最多 123 项）'}
            </div>
          </div>
        )}

        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={handleBuild} className="fluent-btn">
            <Hammer className="w-3.5 h-3.5" />
            <span>生成请求</span>
          </button>
          <button
            type="button"
            disabled={isSending}
            onClick={handleSend}
            className="fluent-btn fluent-btn-primary"
          >
            <Send className="w-3.5 h-3.5" />
            <span>{isSending ? '发送中...' : '发送请求'}</span>
          </button>
          <button
            type="button"
            onClick={() => parseResponseHex(responseHex)}
            className="fluent-btn"
          >
            <FileSearch className="w-3.5 h-3.5" />
            <span>解析响应</span>
          </button>
        </div>

        <div className="space-y-3">
          <div>
            <label className="block text-xs font-semibold mb-1">请求 HEX</label>
            <input
              type="text"
              value={requestHex}
              onChange={(e) => setRequestHex(e.target.value)}
              className="fluent-input w-full font-mono-code"
              placeholder="点击“生成请求”自动构建报文..."
            />
          </div>
          <div>
            <label className="block text-xs font-semibold mb-1">响应 HEX</label>
            <input
              type="text"
              value={responseHex}
              onChange={(e) => setResponseHex(e.target.value)}
              className="fluent-input w-full font-mono-code"
              placeholder="发送请求后自动接收响应报文..."
            />
          </div>
        </div>

        <div className="text-xs text-[var(--text-secondary)]">{statusText}</div>
        {resultText && (
          <div className="p-3 rounded bg-[var(--surface-secondary)] border border-[var(--border-subtle)] text-xs font-mono-code break-words">
            {resultText}
          </div>
        )}
      </div>
    </div>
  );
};
