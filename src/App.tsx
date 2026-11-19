import React, { useState } from 'react';
import {
  Plug,
  Cpu,
  ArrowLeftRight,
  Gauge,
  FileSearch,
  Monitor,
  Settings,
  Contrast,
  PanelLeftClose,
  PanelLeftOpen,
  Cable,
} from 'lucide-react';
import type { NavPageId } from './core/types';
import { AppProvider, useAppContext } from './context/AppContext';
import { SnackbarHost } from './components/SnackbarHost';
import { ConnectionPage } from './pages/ConnectionPage';
import { ModbusDebugPage } from './pages/ModbusDebugPage';
import { RawDebugPage } from './pages/RawDebugPage';
import { RegisterMonitorPage } from './pages/RegisterMonitorPage';
import { PacketAnalysisPage } from './pages/PacketAnalysisPage';
import { DeviceManagementPage } from './pages/DeviceManagementPage';
import { SettingsPage } from './pages/SettingsPage';

interface NavItem {
  id: NavPageId;
  label: string;
  tooltip: string;
  icon: React.ReactNode;
}

const NAV_ITEMS: NavItem[] = [
  {
    id: 'connection',
    label: '通讯连接',
    tooltip: '配置串口与网络通讯链路',
    icon: <Plug className="w-4 h-4 shrink-0" />,
  },
  {
    id: 'modbus-debug',
    label: 'Modbus 调试',
    tooltip: '标准功能码读写与异常分析',
    icon: <Cpu className="w-4 h-4 shrink-0" />,
  },
  {
    id: 'raw-debug',
    label: '无协议调试',
    tooltip: '纯净原始字节流/HEX/ASCII收发',
    icon: <ArrowLeftRight className="w-4 h-4 shrink-0" />,
  },
  {
    id: 'register-monitor',
    label: '数据监听',
    tooltip: '高频寄存器与服务器实时示波器波形监听',
    icon: <Gauge className="w-4 h-4 shrink-0" />,
  },
  {
    id: 'packet-analysis',
    label: '报文分析',
    tooltip: '请求响应匹配与时延统计',
    icon: <FileSearch className="w-4 h-4 shrink-0" />,
  },
  {
    id: 'device-management',
    label: '设备管理',
    tooltip: '设备配置方案导入导出',
    icon: <Monitor className="w-4 h-4 shrink-0" />,
  },
  {
    id: 'settings',
    label: '系统设置',
    tooltip: '外观风格与通讯参数设定',
    icon: <Settings className="w-4 h-4 shrink-0" />,
  },
];

const MainShell: React.FC = () => {
  const { activePage, setActivePage, status, toggleTheme } = useAppContext();
  const [sidebarOpen, setSidebarOpen] = useState(true);

  const renderPage = () => {
    switch (activePage) {
      case 'connection':
        return <ConnectionPage />;
      case 'modbus-debug':
        return <ModbusDebugPage />;
      case 'raw-debug':
        return <RawDebugPage />;
      case 'register-monitor':
        return <RegisterMonitorPage />;
      case 'packet-analysis':
        return <PacketAnalysisPage />;
      case 'device-management':
        return <DeviceManagementPage />;
      case 'settings':
        return <SettingsPage />;
    }
  };

  return (
    <div className="h-screen w-screen flex flex-col overflow-hidden bg-[var(--app-bg)] text-[var(--text-primary)] select-none">
      {/* 顶部标题栏 (40px) */}
      <header className="h-10 shrink-0 px-3 flex items-center justify-between border-b border-[var(--border-subtle)] bg-[var(--app-bg)]">
        <div className="flex items-center gap-2.5">
          <button
            type="button"
            onClick={() => setSidebarOpen((v) => !v)}
            className="p-1.5 rounded hover:bg-[var(--surface-hover)] text-[var(--text-secondary)] cursor-pointer"
            title={sidebarOpen ? '收起导航栏' : '展开导航栏'}
          >
            {sidebarOpen ? (
              <PanelLeftClose className="w-4 h-4" />
            ) : (
              <PanelLeftOpen className="w-4 h-4" />
            )}
          </button>
          <span className="text-xs font-semibold tracking-tight">
            Yuandl Modbus Assistant
          </span>
        </div>

        <button
          type="button"
          onClick={toggleTheme}
          title="切换深浅主题"
          className="p-1.5 rounded hover:bg-[var(--surface-hover)] text-[var(--text-primary)] cursor-pointer"
        >
          <Contrast className="w-4 h-4" />
        </button>
      </header>

      {/* 中部主体：侧边导航 + 主展示区 */}
      <div className="flex-1 min-h-0 flex p-1.5 gap-1.5">
        {/* 左侧导航栏 */}
        <aside
          className={`shrink-0 flex flex-col transition-all duration-200 py-2 px-1.5 ${
            sidebarOpen ? 'w-[216px]' : 'w-[52px]'
          }`}
        >
          {sidebarOpen && (
            <div className="px-2.5 pb-2 text-[11px] font-semibold text-[var(--text-secondary)]">
              功能导航
            </div>
          )}
          <nav className="flex-1 space-y-1">
            {NAV_ITEMS.map((item) => {
              const isActive = activePage === item.id;
              return (
                <button
                  key={item.id}
                  type="button"
                  title={item.tooltip}
                  onClick={() => setActivePage(item.id)}
                  className={`w-full flex items-center gap-2.5 px-2.5 py-2 rounded-md text-xs transition-colors cursor-pointer relative ${
                    isActive
                      ? 'bg-[var(--surface-card)] font-semibold text-[var(--text-primary)] shadow-xs border border-[var(--border-subtle)]'
                      : 'text-[var(--text-secondary)] hover:bg-[var(--surface-hover)] hover:text-[var(--text-primary)]'
                  }`}
                >
                  {isActive && (
                    <span className="absolute left-0 top-1.5 bottom-1.5 w-[3px] rounded-full bg-[var(--accent-primary)]" />
                  )}
                  <span
                    className={
                      isActive ? 'text-[var(--accent-primary)]' : undefined
                    }
                  >
                    {item.icon}
                  </span>
                  {sidebarOpen && (
                    <span className="truncate">{item.label}</span>
                  )}
                </button>
              );
            })}
          </nav>
        </aside>

        {/* 主视图内容区 */}
        <main className="flex-1 min-w-0 min-h-0 rounded-lg border border-[var(--border-subtle)] bg-[var(--app-bg)] overflow-hidden select-text">
          {renderPage()}
        </main>
      </div>

      {/* 底部工业通讯状态监视栏 (34px) */}
      <footer className="h-[34px] shrink-0 px-4 flex items-center gap-4 text-xs border-t border-[var(--border-subtle)] bg-[var(--surface-secondary)]">
        {/* 1. 连接状态指示灯与文字 */}
        <div className="flex items-center gap-2 shrink-0">
          <span
            className="w-2.5 h-2.5 rounded-full inline-block"
            style={{
              backgroundColor: status.isConnected
                ? 'var(--status-connected)'
                : 'var(--status-disconnected)',
            }}
          />
          <span>{status.stateText}</span>
        </div>

        <span className="h-3.5 w-px bg-[var(--border-subtle)]" />

        {/* 2. 通讯链路类型 */}
        <div className="flex items-center gap-1.5 shrink-0 max-w-[280px] truncate">
          <Cable className="w-3.5 h-3.5 text-[var(--text-secondary)] shrink-0" />
          <span className="truncate">{status.communicationType}</span>
        </div>

        <span className="h-3.5 w-px bg-[var(--border-subtle)]" />

        {/* 3. 收发流量统计 */}
        <div className="flex items-center gap-2 shrink-0 tabular-nums">
          <span>
            <span className="text-[var(--text-secondary)]">TX: </span>
            <strong>{status.statistics.bytesSent}</strong>
          </span>
          <span>
            <span className="text-[var(--text-secondary)]">RX: </span>
            <strong>{status.statistics.bytesReceived}</strong>
          </span>
        </div>

        <span className="h-3.5 w-px bg-[var(--border-subtle)]" />

        {/* 4. 通讯异常错误计数 */}
        <div className="flex items-center shrink-0 tabular-nums">
          <span className="text-[var(--text-secondary)]">错误:&nbsp;</span>
          <strong
            className={
              status.statistics.errorsCount > 0
                ? 'text-[var(--danger)]'
                : undefined
            }
          >
            {status.statistics.errorsCount}
          </strong>
        </div>

        <div className="flex-1" />

        {/* 5. 当前设备状态信息 */}
        <div className="flex items-center gap-1.5 min-w-0 truncate">
          <Monitor className="w-3.5 h-3.5 text-[var(--text-secondary)] shrink-0" />
          <span className="truncate">{status.deviceInfo}</span>
        </div>
      </footer>

      <SnackbarHost />
    </div>
  );
};

export function App() {
  return (
    <AppProvider>
      <MainShell />
    </AppProvider>
  );
}

export default App;
