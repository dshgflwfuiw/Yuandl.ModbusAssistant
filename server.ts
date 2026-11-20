import express from 'express';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocketServer, WebSocket } from 'ws';
import { createServer as createViteServer } from 'vite';
import type {
  AppSettings,
  CommunicationType,
  DeviceProfile,
  ModbusServerConfiguration,
  ModbusServerSecurityOptions,
  ModbusServerTransportMode,
  ModbusTransport,
  SerialPortParameters,
  ServerRegisterArea,
  ServerRegisterDefinition,
  TcpClientParameters,
  TcpServerParameters,
  UdpParameters,
} from './src/core/types.js';
import { ByteUtils } from './src/core/utils/byteUtils.js';
import { ServerRegisterCodec } from './src/protocol/modbus/serverRegisterCodec.js';
import {
  CommunicationManager,
  ModbusTcpServerService,
  normalizeServerSecurityOptions,
} from './src/server/modbusCore.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = 3000;

function normalizeSettings(input?: Partial<AppSettings>): AppSettings {
  const timeoutRaw = Number(input?.defaultTimeoutMs);
  const retryRaw = Number(input?.defaultRetryTimes);
  return {
    theme: input?.theme === 'dark' ? 'dark' : 'light',
    defaultTimeoutMs: Number.isFinite(timeoutRaw)
      ? Math.min(30000, Math.max(100, Math.floor(timeoutRaw)))
      : 1000,
    defaultRetryTimes: Number.isFinite(retryRaw)
      ? Math.min(10, Math.max(0, Math.floor(retryRaw)))
      : 2,
  };
}

async function main() {
  const app = express();
  app.use(express.json({ limit: '2mb' }));

  const server = http.createServer(app);
  const wss = new WebSocketServer({ server, path: '/ws' });

  const broadcast = (event: string, payload: unknown) => {
    const msg = JSON.stringify({ event, payload });
    for (const client of wss.clients) {
      if (client.readyState === WebSocket.OPEN) {
        client.send(msg);
      }
    }
  };

  const modbusServer = new ModbusTcpServerService();
  modbusServer.configureRegisters([
    {
      area: 'HoldingRegister',
      address: 0,
      dataType: 'UInt16',
      byteOrder: 'BigEndian',
      initialValue: '1234',
      note: '示例保持寄存器 #0',
    },
    {
      area: 'HoldingRegister',
      address: 1,
      dataType: 'Float',
      byteOrder: 'BigEndian',
      initialValue: '25.5',
      note: '温度传感器 (Float)',
    },
    {
      area: 'InputRegister',
      address: 0,
      dataType: 'Int16',
      byteOrder: 'BigEndian',
      initialValue: '500',
      note: '只读输入寄存器',
    },
    {
      area: 'Coil',
      address: 0,
      dataType: 'Bit',
      byteOrder: 'BigEndian',
      initialValue: 'true',
      note: '运行指示线圈',
    },
  ]);

  const commManager = new CommunicationManager(modbusServer, broadcast);
  modbusServer.onRegistersMutated = () => {
    broadcast('state', commManager.getSnapshot());
  };

  let deviceProfiles: DeviceProfile[] = [
    {
      id: 'default-local-tcp',
      name: '本地 Modbus TCP 仿真从站',
      protocol: 'Modbus TCP',
      endpoint: '127.0.0.1:5020',
      stationId: 1,
    },
    {
      id: 'default-serial-rtu',
      name: '虚拟串口 RTU 设备',
      protocol: 'Modbus RTU',
      endpoint: 'COM1 (9600,8,None,One)',
      stationId: 1,
    },
  ];

  let appSettings: AppSettings = {
    theme: 'light',
    defaultTimeoutMs: 1000,
    defaultRetryTimes: 2,
  };

  wss.on('connection', (ws) => {
    ws.send(
      JSON.stringify({
        event: 'state',
        payload: commManager.getSnapshot(),
      })
    );
  });

  // ====== API Routes ======
  app.get('/api/state', (_req, res) => {
    res.json({
      status: commManager.getSnapshot(),
      profiles: deviceProfiles,
      settings: appSettings,
    });
  });

  app.post('/api/comm/connect', async (req, res) => {
    try {
      const { type, serial, tcp, udp, tcpServer } = req.body as {
        type: CommunicationType;
        serial?: SerialPortParameters;
        tcp?: TcpClientParameters;
        udp?: UdpParameters;
        tcpServer?: TcpServerParameters;
      };
      if (type === 'SerialPort' && serial) {
        await commManager.connectSerial(serial);
      } else if (type === 'TcpClient' && tcp) {
        await commManager.connectTcp(tcp);
      } else if (type === 'Udp' && udp) {
        await commManager.connectUdp(udp);
      } else if (type === 'TcpServer' && tcpServer) {
        await commManager.connectTcpServer(tcpServer);
      } else {
        res.status(400).json({ error: '无效的通讯连接参数' });
        return;
      }
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '连接失败';
      res.status(400).json({ error: message, status: commManager.getSnapshot() });
    }
  });

  app.post('/api/comm/disconnect', async (_req, res) => {
    await commManager.disconnect(true);
    res.json({ ok: true, status: commManager.getSnapshot() });
  });

  app.post('/api/comm/server-clients/:sessionId/disconnect', (req, res) => {
    commManager.disconnectTcpServerClient(req.params.sessionId);
    res.json({ ok: true, status: commManager.getSnapshot() });
  });

  app.post('/api/comm/send', async (req, res) => {
    try {
      const { bytes, sessionId } = req.body as {
        bytes: number[];
        sessionId?: string;
      };
      if (!Array.isArray(bytes) || bytes.length === 0) {
        res.status(400).json({ error: '发送数据不能为空' });
        return;
      }
      const sent = await commManager.send(new Uint8Array(bytes), sessionId);
      res.json({ ok: true, sent, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '发送失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/state/restore', (req, res) => {
    try {
      const { profiles, settings, serverConfig } = req.body as {
        profiles?: DeviceProfile[];
        settings?: AppSettings;
        serverConfig?: ModbusServerConfiguration;
      };
      if (Array.isArray(profiles)) {
        deviceProfiles = profiles;
      }
      if (settings) {
        appSettings = normalizeSettings(settings);
      }
      if (
        serverConfig &&
        (serverConfig.version === 1 || serverConfig.version === 2) &&
        Array.isArray(serverConfig.registers)
      ) {
        modbusServer.port = serverConfig.port;
        modbusServer.station = serverConfig.station;
        if (serverConfig.serverMode) {
          modbusServer.serverMode = serverConfig.serverMode;
        }
        if (serverConfig.serverSerial) {
          modbusServer.serialParams = { ...serverConfig.serverSerial };
        }
        modbusServer.securityOptions = normalizeServerSecurityOptions(serverConfig, 260);
        modbusServer.configureRegisters(serverConfig.registers);
      }
      broadcast('state', commManager.getSnapshot());
      res.json({
        ok: true,
        status: commManager.getSnapshot(),
        profiles: deviceProfiles,
        settings: appSettings,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : '状态恢复失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/modbus/exchange', async (req, res) => {
    try {
      const { requestHex, timeoutMs, retries, transport } = req.body as {
        requestHex: string;
        timeoutMs?: number;
        retries?: number;
        transport?: ModbusTransport;
      };
      const requestBytes = ByteUtils.hexToBytes(requestHex ?? '');
      if (requestBytes.length === 0) {
        res.status(400).json({ error: '请求报文不能为空' });
        return;
      }
      const effectiveTimeout = timeoutMs ?? appSettings.defaultTimeoutMs;
      const effectiveRetries = retries ?? appSettings.defaultRetryTimes;
      const responseBytes = await commManager.exchange(
        requestBytes,
        effectiveTimeout,
        effectiveRetries,
        transport
      );
      res.json({
        ok: true,
        broadcast: responseBytes.length === 0,
        responseHex: ByteUtils.bytesToHex(responseBytes),
        responseBytes: Array.from(responseBytes),
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : '请求失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/start', async (req, res) => {
    try {
      const { port, station, mode, serial, security } = req.body as {
        port: number;
        station: number;
        mode?: ModbusServerTransportMode;
        serial?: SerialPortParameters;
        security?: ModbusServerSecurityOptions;
      };
      await modbusServer.start(
        Number(port),
        Number(station),
        mode ?? modbusServer.serverMode,
        serial,
        security
      );
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '服务器启动失败';
      modbusServer.statusText = `启动失败：${message}`;
      broadcast('state', commManager.getSnapshot());
      res.status(400).json({ error: message, status: commManager.getSnapshot() });
    }
  });

  app.post('/api/server/stop', async (_req, res) => {
    await modbusServer.stop();
    broadcast('state', commManager.getSnapshot());
    res.json({ ok: true, status: commManager.getSnapshot() });
  });

  app.post('/api/server/registers', (req, res) => {
    try {
      const def = req.body as ServerRegisterDefinition;
      ServerRegisterCodec.validate(def);
      const current = modbusServer.getDefinitionsWithCurrentValues();
      const updated = [
        ...current
          .filter((item) => item.area !== def.area || item.address !== def.address)
          .map(({ currentValue: _cv, ...rest }) => rest),
        {
          area: def.area,
          address: Number(def.address),
          dataType: def.dataType,
          byteOrder: def.byteOrder,
          initialValue: def.initialValue,
          note: def.note ?? '',
        },
      ];
      ServerRegisterCodec.validateNoOverlap(updated);
      modbusServer.configureRegisters(updated);
      modbusServer.statusText = modbusServer.isRunning
        ? modbusServer.buildRunningStatusText()
        : `已配置 ${updated.length} 项数据定义`;
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '建立数据定义失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/registers/runtime', (req, res) => {
    try {
      const { area, address, value } = req.body as {
        area: ServerRegisterArea;
        address: number;
        value: string;
      };
      modbusServer.setRuntimeValue(area, Number(address), String(value));
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '写入当前值失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/registers/reset', (_req, res) => {
    try {
      modbusServer.resetToInitialValues();
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '重置初始值失败';
      res.status(400).json({ error: message });
    }
  });

  app.delete('/api/server/registers', (req, res) => {
    try {
      const area = req.query.area as ServerRegisterArea;
      const address = Number(req.query.address);
      const current = modbusServer.getDefinitionsWithCurrentValues();
      const updated = current
        .filter((item) => item.area !== area || item.address !== address)
        .map(({ currentValue: _cv, ...rest }) => rest);
      modbusServer.configureRegisters(updated);
      modbusServer.statusText = modbusServer.isRunning
        ? modbusServer.buildRunningStatusText()
        : `已配置 ${updated.length} 项数据定义`;
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '删除数据定义失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/import', (req, res) => {
    try {
      const config = req.body as ModbusServerConfiguration;
      if (!config || (config.version !== 1 && config.version !== 2)) {
        throw new Error(`不支持的配置版本：${config?.version}`);
      }
      if (config.port < 1 || config.port > 65535) {
        throw new Error('服务器端口必须在 1 至 65535 之间');
      }
      if (config.station < 1 || config.station > 247) {
        throw new Error('站号必须在 1 至 247 之间');
      }
      if (!Array.isArray(config.registers)) {
        throw new Error('寄存器配置列表格式不正确');
      }
      modbusServer.securityOptions = normalizeServerSecurityOptions(config, 260);
      modbusServer.port = config.port;
      modbusServer.station = config.station;
      if (config.serverMode) {
        modbusServer.serverMode = config.serverMode;
      }
      if (config.serverSerial) {
        modbusServer.serialParams = { ...config.serverSerial };
      }
      modbusServer.configureRegisters(config.registers);
      modbusServer.statusText = modbusServer.isRunning
        ? modbusServer.buildRunningStatusText()
        : `已加载 ${config.registers.length} 项数据定义`;
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '配置加载失败';
      res.status(400).json({ error: message });
    }
  });

  app.get('/api/profiles', (_req, res) => {
    res.json({ profiles: deviceProfiles });
  });

  app.post('/api/profiles', (req, res) => {
    const profile = req.body as Omit<DeviceProfile, 'id'> & { id?: string };
    const created: DeviceProfile = {
      id: profile.id || `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
      name: profile.name || '新设备',
      protocol: profile.protocol || 'Modbus TCP',
      endpoint: profile.endpoint || '127.0.0.1:502',
      stationId: Number(profile.stationId) || 1,
    };
    deviceProfiles = [...deviceProfiles, created];
    res.json({ ok: true, profiles: deviceProfiles });
  });

  app.delete('/api/profiles/:id', (req, res) => {
    deviceProfiles = deviceProfiles.filter((p) => p.id !== req.params.id);
    res.json({ ok: true, profiles: deviceProfiles });
  });

  app.post('/api/settings', (req, res) => {
    const body = req.body as Partial<AppSettings>;
    appSettings = normalizeSettings(body);
    res.json({ ok: true, settings: appSettings });
  });

  if (process.env.NODE_ENV === 'production') {
    const distDir = path.resolve(__dirname, 'dist');
    app.use(express.static(distDir));
    app.get('*', (_req, res) => {
      res.sendFile(path.join(distDir, 'index.html'));
    });
  } else {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  }

  server.listen(PORT, '0.0.0.0', () => {
    console.log(`Yuandl Modbus Assistant server listening on http://0.0.0.0:${PORT}`);
  });
}

main().catch((err) => {
  console.error('Failed to start server:', err);
  process.exit(1);
});
