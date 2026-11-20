import React from 'react';
import { Contrast, Sliders } from 'lucide-react';
import { useAppContext } from '../context/AppContext';

export const SettingsPage: React.FC = () => {
  const { settings, updateSettings } = useAppContext();

  return (
    <div className="h-full overflow-y-auto p-6 space-y-5">
      <div>
        <h1 className="text-xl font-semibold">系统设置</h1>
        <p className="text-xs text-[var(--text-secondary)] mt-1">
          个性化界面风格、通讯超时与系统偏好配置
        </p>
      </div>

      {/* 外观表现 */}
      <div className="space-y-2.5">
        <h2 className="text-sm font-semibold">外观表现</h2>
        <div className="fluent-card p-4 flex items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            <Contrast className="w-5 h-5 text-[var(--text-secondary)] shrink-0" />
            <div>
              <div className="text-sm font-medium">应用程序主题</div>
              <div className="text-xs text-[var(--text-secondary)]">
                切换 Windows 11 Fluent 浅色或深色设计风格
              </div>
            </div>
          </div>
          <select
            value={settings.theme}
            onChange={(e) =>
              void updateSettings({
                ...settings,
                theme: e.target.value as 'light' | 'dark',
              })
            }
            className="fluent-input w-44"
          >
            <option value="light">浅色模式 (Light)</option>
            <option value="dark">深色模式 (Dark)</option>
          </select>
        </div>
      </div>

      {/* 默认通讯控制 */}
      <div className="space-y-2.5">
        <h2 className="text-sm font-semibold">默认通讯控制</h2>
        <div className="fluent-card divide-y divide-[var(--border-subtle)]">
          <div className="p-4 flex items-center justify-between gap-4">
            <div className="flex items-center gap-3">
              <Sliders className="w-5 h-5 text-[var(--text-secondary)] shrink-0" />
              <div>
                <div className="text-sm font-medium">通讯默认超时 (ms)</div>
                <div className="text-xs text-[var(--text-secondary)]">
                  请求发送后等待从站或对端返回的最长等待毫秒数 (100 ~ 30000)
                </div>
              </div>
            </div>
            <input
              type="number"
              min={100}
              max={30000}
              value={settings.defaultTimeoutMs}
              onChange={(e) => {
                const raw = Number(e.target.value);
                const clamped = Number.isFinite(raw)
                  ? Math.min(30000, Math.max(100, Math.floor(raw)))
                  : 1000;
                void updateSettings({
                  ...settings,
                  defaultTimeoutMs: clamped,
                });
              }}
              className="fluent-input w-32 text-center"
            />
          </div>

          <div className="p-4 flex items-center justify-between gap-4">
            <div className="flex items-center gap-3">
              <Sliders className="w-5 h-5 text-[var(--text-secondary)] shrink-0" />
              <div>
                <div className="text-sm font-medium">失败重试次数</div>
                <div className="text-xs text-[var(--text-secondary)]">
                  请求超时或无响应后的自动重试次数 (0 ~ 10，广播写不重试)
                </div>
              </div>
            </div>
            <input
              type="number"
              min={0}
              max={10}
              value={settings.defaultRetryTimes}
              onChange={(e) => {
                const raw = Number(e.target.value);
                const clamped = Number.isFinite(raw)
                  ? Math.min(10, Math.max(0, Math.floor(raw)))
                  : 2;
                void updateSettings({
                  ...settings,
                  defaultRetryTimes: clamped,
                });
              }}
              className="fluent-input w-32 text-center"
            />
          </div>
        </div>
      </div>

      {/* 软件信息 */}
      <div className="space-y-2.5">
        <h2 className="text-sm font-semibold">软件信息</h2>
        <div className="fluent-card p-4 space-y-1">
          <div className="text-sm font-semibold">Yuandl Modbus Assistant v1.1.0</div>
          <div className="text-xs text-[var(--text-secondary)]">
            工业通讯与协议分析套件（支持串口仿真、TCP/UDP 通讯、Modbus 帧编解码与内置 Modbus TCP/串口服务器）
          </div>
        </div>
      </div>
    </div>
  );
};
