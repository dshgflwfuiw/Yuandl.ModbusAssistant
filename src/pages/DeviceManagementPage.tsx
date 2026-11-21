import React, { useRef, useState } from 'react';
import { Plus, Save, RefreshCw, Trash2, Plug, Upload, Download } from 'lucide-react';
import type { DeviceProfile, ModbusServerConfiguration } from '../core/types';
import { useAppContext } from '../context/AppContext';

export const DeviceManagementPage: React.FC = () => {
  const {
    profiles,
    addProfile,
    removeProfile,
    reloadProfiles,
    setCommForm,
    setActivePage,
    showSnackbar,
    status,
    settings,
    updateSettings,
    refreshState,
    persistServerConfig,
  } = useAppContext();

  const [newProfileName, setNewProfileName] = useState<string>('新设备');
  const [newProtocol, setNewProtocol] = useState<string>('Modbus TCP');
  const [newEndpoint, setNewEndpoint] = useState<string>('127.0.0.1:5020');
  const [newStationId, setNewStationId] = useState<number>(1);
  const [statusText, setStatusText] = useState<string>('就绪');
  const workspaceInput = useRef<HTMLInputElement>(null);

  const handleAdd = async () => {
    await addProfile({
      name: newProfileName,
      protocol: newProtocol,
      endpoint: newEndpoint,
      stationId: newStationId,
    });
    setStatusText('设备配置已添加');
    showSnackbar('添加成功', `已保存设备方案 "${newProfileName}"`, 'success');
  };

  const handleSave = () => {
    const blob = new Blob([JSON.stringify(profiles, null, 2)], {
      type: 'application/json',
    });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'devices.json';
    a.click();
    URL.revokeObjectURL(url);
    setStatusText(`已保存 ${profiles.length} 个设备配置`);
  };

  const handleReload = async () => {
    await reloadProfiles();
    setStatusText(`已加载 ${profiles.length} 个设备配置`);
  };

  const exportWorkspace = () => {
    const security = status.serverSecurity ?? { bindAddress: '127.0.0.1', maxClients: 32, idleTimeoutMs: 60000, maxFrameLength: 260, sendQueueLimit: 64, allowedClientIps: '' };
    const workspace = {
      format: 'yuandl-modbus-workspace', version: 1,
      profiles, settings: { isDarkTheme: settings.theme === 'dark', defaultTimeoutMs: settings.defaultTimeoutMs, defaultRetryTimes: settings.defaultRetryTimes },
      serverConfig: {
        version: 2, bindAddress: security.bindAddress, port: status.serverPort, station: status.serverStation,
        maxClients: security.maxClients, idleTimeoutMs: security.idleTimeoutMs,
        maxFrameLength: security.maxFrameLength, sendQueueLimit: security.sendQueueLimit,
        allowedClientIps: security.allowedClientIps, serverMode: status.serverMode, serverSerial: status.serverSerial,
        registers: status.serverRegisters.map(({ currentValue: _currentValue, ...definition }) => definition),
      },
    };
    const url = URL.createObjectURL(new Blob([JSON.stringify(workspace, null, 2)], { type: 'application/json' }));
    const link = document.createElement('a'); link.href = url; link.download = 'yuandl-modbus-workspace.json'; link.click(); URL.revokeObjectURL(url);
  };

  const importWorkspace = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]; if (!file) return;
    try {
      const payload = JSON.parse(await file.text()) as { format?: string; version?: number; profiles?: DeviceProfile[]; settings?: { isDarkTheme?: boolean; theme?: 'light' | 'dark'; defaultTimeoutMs?: number; defaultRetryTimes?: number }; serverConfig?: ModbusServerConfiguration };
      if (payload.format !== 'yuandl-modbus-workspace' || payload.version !== 1) throw new Error('不支持的工作区格式或版本');
      if (!Array.isArray(payload.profiles) || !payload.settings || !payload.serverConfig) throw new Error('工作区文件缺少设备、设置或从站配置');
      const serverConfig = payload.serverConfig;
      if (!window.confirm(`将导入 ${payload.profiles.length} 个设备方案、系统设置和从站点表，并覆盖当前对应配置。继续吗？`)) return;
      const response = await fetch('/api/state/restore', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ profiles: payload.profiles, settings: { theme: payload.settings.theme ?? (payload.settings.isDarkTheme ? 'dark' : 'light'), defaultTimeoutMs: payload.settings.defaultTimeoutMs ?? 1000, defaultRetryTimes: payload.settings.defaultRetryTimes ?? 2 }, serverConfig: payload.serverConfig }) });
      const result = await response.json(); if (!response.ok) throw new Error(result.error || '导入失败');
      await Promise.all([reloadProfiles(), updateSettings({ theme: payload.settings.theme ?? (payload.settings.isDarkTheme ? 'dark' : 'light'), defaultTimeoutMs: payload.settings.defaultTimeoutMs ?? 1000, defaultRetryTimes: payload.settings.defaultRetryTimes ?? 2 }), refreshState()]);
      persistServerConfig(serverConfig);
      showSnackbar('工作区已导入', '设备方案、系统设置和 Modbus 点表已恢复', 'success');
    } catch (error) { showSnackbar('导入失败', error instanceof Error ? error.message : '文件格式错误', 'error'); }
    finally { event.target.value = ''; }
  };

  const handleApplyToConnection = (profile: DeviceProfile) => {
    if (profile.protocol.toUpperCase().includes('TCP')) {
      const [host, portStr] = profile.endpoint.split(':');
      setCommForm((prev) => ({
        ...prev,
        selectedCommTypeIndex: 1,
        tcpHost: host || '127.0.0.1',
        tcpPort: Number(portStr) || 502,
      }));
    } else if (profile.protocol.toUpperCase().includes('UDP')) {
      const [host, portStr] = profile.endpoint.split(':');
      setCommForm((prev) => ({
        ...prev,
        selectedCommTypeIndex: 2,
        udpRemoteHost: host || '127.0.0.1',
        udpRemotePort: Number(portStr) || 502,
      }));
    } else {
      const portName = profile.endpoint.split(' ')[0] || 'COM1';
      setCommForm((prev) => ({
        ...prev,
        selectedCommTypeIndex: 0,
        selectedPort: portName,
      }));
    }
    setActivePage('connection');
    showSnackbar('已应用方案', `已载入 "${profile.name}" 的连接参数`, 'info');
  };

  return (
    <div className="h-full overflow-y-auto p-3 sm:p-6 space-y-4">
      <div>
        <h1 className="text-xl font-semibold">设备管理</h1>
        <p className="text-xs text-[var(--text-secondary)] mt-1">
          保存常用通讯设备配置，启动调试时快速复用。
        </p>
      </div>

      <div className="fluent-card p-5 space-y-4">
        <div className="flex flex-wrap items-end gap-2.5">
          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              名称
            </label>
            <input
              type="text"
              value={newProfileName}
              onChange={(e) => setNewProfileName(e.target.value)}
              className="fluent-input w-40"
            />
          </div>

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              协议
            </label>
            <input
              type="text"
              value={newProtocol}
              onChange={(e) => setNewProtocol(e.target.value)}
              className="fluent-input w-36"
            />
          </div>

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              地址
            </label>
            <input
              type="text"
              value={newEndpoint}
              onChange={(e) => setNewEndpoint(e.target.value)}
              className="fluent-input w-52"
            />
          </div>

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              站号
            </label>
            <input
              type="number"
              min={1}
              max={247}
              value={newStationId}
              onChange={(e) => setNewStationId(Number(e.target.value))}
              className="fluent-input w-20"
            />
          </div>

          <button
            type="button"
            onClick={() => void handleAdd()}
            className="fluent-btn fluent-btn-primary h-[34px]"
          >
            <Plus className="w-3.5 h-3.5" />
            <span>添加</span>
          </button>
        </div>

        <div className="flex flex-wrap gap-2">
          <input ref={workspaceInput} type="file" accept="application/json,.json" className="hidden" onChange={importWorkspace} />
          <button type="button" onClick={() => workspaceInput.current?.click()} className="fluent-btn"><Upload className="w-3.5 h-3.5" /><span>导入工作区</span></button>
          <button type="button" onClick={exportWorkspace} className="fluent-btn"><Download className="w-3.5 h-3.5" /><span>导出工作区</span></button>
          <button type="button" onClick={handleSave} className="fluent-btn">
            <Save className="w-3.5 h-3.5" />
            <span>导出保存</span>
          </button>
          <button
            type="button"
            onClick={() => void handleReload()}
            className="fluent-btn"
          >
            <RefreshCw className="w-3.5 h-3.5" />
            <span>重新加载</span>
          </button>
        </div>

        <div className="border border-[var(--border-subtle)] rounded overflow-x-auto min-h-[260px]">
          <table className="w-full text-xs text-left border-collapse">
            <thead className="bg-[var(--surface-secondary)] border-b border-[var(--border-subtle)]">
              <tr>
                <th className="px-3 py-2 font-semibold">名称</th>
                <th className="px-3 py-2 font-semibold">协议</th>
                <th className="px-3 py-2 font-semibold">地址</th>
                <th className="px-3 py-2 font-semibold w-20">站号</th>
                <th className="px-3 py-2 font-semibold w-40">操作</th>
              </tr>
            </thead>
            <tbody>
              {profiles.length === 0 ? (
                <tr>
                  <td
                    colSpan={5}
                    className="px-3 py-8 text-center text-[var(--text-secondary)]"
                  >
                    暂无设备方案
                  </td>
                </tr>
              ) : (
                profiles.map((p) => (
                  <tr
                    key={p.id}
                    className="border-b border-[var(--border-subtle)] last:border-b-0 hover:bg-[var(--surface-hover)]"
                  >
                    <td className="px-3 py-2 font-medium">{p.name}</td>
                    <td className="px-3 py-2">{p.protocol}</td>
                    <td className="px-3 py-2 font-mono-code">{p.endpoint}</td>
                    <td className="px-3 py-2 tabular-nums">{p.stationId}</td>
                    <td className="px-3 py-1.5 flex items-center gap-2">
                      <button
                        type="button"
                        onClick={() => handleApplyToConnection(p)}
                        className="fluent-btn py-1 px-2 text-[11px]"
                      >
                        <Plug className="w-3 h-3" />
                        <span>载入</span>
                      </button>
                      <button
                        type="button"
                        onClick={() => void removeProfile(p.id)}
                        className="fluent-btn py-1 px-2 text-[11px]"
                        title="删除"
                      >
                        <Trash2 className="w-3 h-3" />
                      </button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <div className="text-xs text-[var(--text-secondary)]">{statusText}</div>
      </div>
    </div>
  );
};
