import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import net from 'node:net';
import dgram from 'node:dgram';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type {
  ModbusTransport,
  RegisterByteOrder,
  RegisterDataType,
  ServerRegisterByteOrder,
  ServerRegisterDataType,
} from '../src/core/types.js';
import { ByteUtils } from '../src/core/utils/byteUtils.js';
import { ModbusFrame } from '../src/protocol/modbus/modbusFrame.js';
import { RegisterValueConverter } from '../src/protocol/modbus/registerValueConverter.js';
import { ServerRegisterCodec } from '../src/protocol/modbus/serverRegisterCodec.js';
import {
  CommunicationManager,
  ModbusTcpServerService,
  tryExtractMatchingFrame,
  tryTakeSerialRequestFrame,
} from '../src/server/modbusCore.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const vectorsPath = path.resolve(__dirname, '../test-vectors/modbus-vectors.json');
const vectors = JSON.parse(fs.readFileSync(vectorsPath, 'utf-8')) as {
  frames: Array<{
    name: string;
    kind: 'read' | 'writeSingle' | 'writeMultiple';
    transport: ModbusTransport;
    unitId: number;
    functionCode: number;
    address: number;
    quantity?: number;
    value?: number;
    values?: number[];
    transactionId: number;
    expectedHex: string;
  }>;
  byteOrderVectors: Array<{
    name: string;
    dataType: RegisterDataType;
    byteOrder: RegisterByteOrder;
    registers: number[];
    scale: number;
    expected: number;
    tolerance: number;
  }>;
  serverCodecRoundTrips: Array<{
    dataType: ServerRegisterDataType;
    byteOrder: ServerRegisterByteOrder;
    value: string;
  }>;
};

function getFreeTcpPort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      const port = typeof addr === 'object' && addr ? addr.port : 0;
      srv.close((err) => (err ? reject(err) : resolve(port)));
    });
  });
}

test('shared protocol vectors: Modbus RTU/TCP/ASCII framing & parsing', () => {
  for (const vec of vectors.frames) {
    let built: Uint8Array;
    if (vec.kind === 'read') {
      built = ModbusFrame.buildRequest(
        vec.unitId,
        vec.functionCode,
        vec.address,
        vec.quantity!,
        vec.transport,
        vec.transactionId
      );
    } else if (vec.kind === 'writeSingle') {
      built = ModbusFrame.buildWriteSingle(
        vec.unitId,
        vec.functionCode,
        vec.address,
        vec.value!,
        vec.transport,
        vec.transactionId
      );
    } else {
      built = ModbusFrame.buildWriteMultiple(
        vec.unitId,
        vec.functionCode,
        vec.address,
        vec.values!,
        vec.transport,
        vec.transactionId
      );
    }

    assert.equal(
      ByteUtils.bytesToHex(built),
      vec.expectedHex,
      `Frame mismatch for ${vec.name}`
    );

    const parsed = ModbusFrame.parseRequest(built, vec.transport);
    assert.equal(parsed.unitId, vec.unitId, vec.name);
    assert.equal(parsed.functionCode, vec.functionCode, vec.name);
    assert.equal(parsed.address, vec.address, vec.name);
    if (vec.values) {
      assert.deepEqual(parsed.values, vec.values, vec.name);
    }
  }
});

test('shared protocol vectors: 4 byte orders & ServerRegisterCodec round-trips', () => {
  for (const vec of vectors.byteOrderVectors) {
    const actual = RegisterValueConverter.convert(
      vec.registers,
      vec.dataType,
      vec.byteOrder,
      vec.scale
    );
    assert.ok(
      Math.abs(actual - vec.expected) <= vec.tolerance,
      `${vec.name}: expected ${vec.expected}, got ${actual}`
    );
  }

  for (const rt of vectors.serverCodecRoundTrips) {
    const regs = ServerRegisterCodec.encodeRegisters(
      rt.value,
      rt.dataType,
      rt.byteOrder
    );
    const decoded = ServerRegisterCodec.decodeRegisters(
      regs,
      rt.dataType,
      rt.byteOrder
    );
    assert.ok(
      Math.abs(Number(decoded) - Number(rt.value)) <= 1e-4,
      `Round-trip mismatch ${rt.dataType}/${rt.byteOrder}: expected ${rt.value}, got ${decoded}`
    );
  }
});

test('low-baud fragmented serial RTU/ASCII framing handles noise & 1-byte chunks', () => {
  const modbusServer = new ModbusTcpServerService();
  modbusServer.serverMode = 'TcpAndSerial';

  // Corrupted RTU prefix bytes followed by valid RTU FC03 request + ASCII FC03 request
  const rtuReq = ModbusFrame.buildRequest(1, 3, 0, 2, 'Rtu');
  const asciiReq = ModbusFrame.buildRequest(1, 3, 0, 2, 'Ascii');
  const streamBytes = new Uint8Array([0x01, 0x03, 0xfe, ...rtuReq, ...asciiReq]);

  const rxBuffer: number[] = [];
  const extracted: Uint8Array[] = [];

  for (const b of streamBytes) {
    rxBuffer.push(b);
    let frame: Uint8Array | null;
    while ((frame = tryTakeSerialRequestFrame(rxBuffer, 'TcpAndSerial')) !== null) {
      extracted.push(frame);
    }
  }

  assert.equal(extracted.length, 2);
  assert.equal(ByteUtils.bytesToHex(extracted[0]), ByteUtils.bytesToHex(rtuReq));
  assert.equal(ByteUtils.bytesToHex(extracted[1]), ByteUtils.bytesToHex(asciiReq));

  // Verify response extraction ignores corrupted RTU header byteCount and resyncs
  const validResp = new Uint8Array([0x01, 0x03, 0x04, 0x04, 0xd2, 0x16, 0x2e, 0x00, 0x00]);
  // Recompute exact CRC on validResp body
  const pduBody = new Uint8Array([0x01, 0x03, 0x04, 0x04, 0xd2, 0x16, 0x2e]);
  const fullValidResp = ModbusFrame.encode(pduBody, 'Rtu');
  void validResp;

  const noisePrefix = [0x01, 0x03, 0xff]; // invalid byteCount = 255
  const respBuf: number[] = [];
  let matched: Uint8Array | null = null;
  for (const b of [...noisePrefix, ...fullValidResp]) {
    respBuf.push(b);
    matched = tryExtractMatchingFrame(respBuf, 'Rtu', undefined, 1, 3);
    if (matched) break;
  }
  assert.ok(matched !== null);
  assert.equal(ByteUtils.bytesToHex(matched), ByteUtils.bytesToHex(fullValidResp));
});

test('TCP fragmented frames, stale TxId discard, auto-reconnect recovery & broadcast semantics', async () => {
  const port = await getFreeTcpPort();
  const modbusServer = new ModbusTcpServerService();
  modbusServer.configureRegisters([
    {
      area: 'HoldingRegister',
      address: 0,
      dataType: 'UInt16',
      byteOrder: 'BigEndian',
      initialValue: '100',
      note: 'reg0',
    },
    {
      area: 'Coil',
      address: 0,
      dataType: 'Bit',
      byteOrder: 'BigEndian',
      initialValue: 'false',
      note: 'coil0',
    },
  ]);

  let connectionCount = 0;
  let activeSockets = new Set<net.Socket>();
  let injectStaleFrameOnNext = false;

  const createMockServer = () =>
    net.createServer((socket) => {
      connectionCount++;
      activeSockets.add(socket);
      socket.setNoDelay(true);
      let buf = Buffer.alloc(0);

      socket.on('data', (chunk) => {
        buf = Buffer.concat([buf, chunk]);
        while (buf.length >= 7) {
          const len = buf.readUInt16BE(4);
          if (buf.length < 6 + len) break;
          const frame = buf.subarray(0, 6 + len);
          buf = buf.subarray(6 + len);

          const unitId = frame[6];
          const pdu = new Uint8Array(frame.subarray(7));
          const respPdu = modbusServer.processRequest(unitId, pdu);
          if (!respPdu) continue; // silent on broadcast!

          const response = Buffer.alloc(7 + respPdu.length);
          frame.copy(response, 0, 0, 4);
          response.writeUInt16BE(respPdu.length + 1, 4);
          response[6] = unitId;
          Buffer.from(respPdu).copy(response, 7);

          const sendFragmented = async () => {
            if (injectStaleFrameOnNext) {
              injectStaleFrameOnNext = false;
              const stale = Buffer.from(response);
              stale.writeUInt16BE(999, 0); // wrong transactionId
              socket.write(stale);
              await new Promise((r) => setTimeout(r, 10));
            }
            // Split real response into 3 small fragments across ticks
            const p1 = response.subarray(0, 3);
            const p2 = response.subarray(3, 7);
            const p3 = response.subarray(7);
            socket.write(p1);
            await new Promise((r) => setTimeout(r, 8));
            socket.write(p2);
            await new Promise((r) => setTimeout(r, 8));
            socket.write(p3);
          };
          void sendFragmented();
        }
      });

      socket.on('close', () => activeSockets.delete(socket));
    });

  let mockServer = createMockServer();
  await new Promise<void>((resolve) => mockServer.listen(port, '127.0.0.1', resolve));

  const comm = new CommunicationManager(modbusServer, () => {});
  try {
    await comm.connectTcp({
      host: '127.0.0.1',
      port,
      connectTimeoutMs: 2000,
      autoReconnect: true,
      reconnectIntervalMs: 200,
    });

    // 1. Fragmented response + stale TxId frame rejection
    injectStaleFrameOnNext = true;
    const req1 = ModbusFrame.buildRequest(1, 3, 0, 1, 'Tcp', 101);
    const resp1 = await comm.exchange(req1, 1500, 1, 'Tcp');
    const parsed1 = ModbusFrame.parseResponse(resp1, 'Tcp');
    assert.equal(parsed1.transactionId, 101);
    assert.deepEqual(parsed1.registers, [100]);

    // 2. Broadcast write (UnitId=0, FC=06) does not timeout and updates server state
    const bcastReq = ModbusFrame.buildWriteSingle(0, 6, 0, 777, 'Tcp', 102);
    const bcastResp = await comm.exchange(bcastReq, 400, 2, 'Tcp');
    assert.equal(bcastResp.length, 0);

    // 3. Force server drop & restart to verify TCP reconnect preserves read loop across 2 reconnect cycles
    for (let cycle = 0; cycle < 2; cycle++) {
      for (const s of activeSockets) s.destroy();
      await new Promise((r) => setTimeout(r, 400)); // allow reconnect loop to complete
      assert.equal(comm.getSnapshot().isConnected, true, `Reconnect cycle ${cycle + 1} connected`);

      const verifyReq = ModbusFrame.buildRequest(1, 3, 0, 1, 'Tcp', 200 + cycle);
      const verifyResp = await comm.exchange(verifyReq, 1500, 1, 'Tcp');
      const parsedVerify = ModbusFrame.parseResponse(verifyResp, 'Tcp');
      assert.deepEqual(parsedVerify.registers, [777]);
    }
    assert.ok(connectionCount >= 3);
  } finally {
    await comm.disconnect(false);
    for (const s of activeSockets) s.destroy();
    await new Promise<void>((resolve) => mockServer.close(() => resolve()));
  }
});

test('long-running concurrent Modbus TCP polling stress test (150 cycles) + UDP loopback', async () => {
  const port = await getFreeTcpPort();
  const server = new ModbusTcpServerService();
  server.configureRegisters([
    {
      area: 'HoldingRegister',
      address: 0,
      dataType: 'Float',
      byteOrder: 'WordSwap',
      initialValue: '36.5',
      note: 'temp',
    },
    {
      area: 'Coil',
      address: 0,
      dataType: 'Bit',
      byteOrder: 'BigEndian',
      initialValue: 'true',
      note: 'run',
    },
  ]);

  await server.start(port, 1, 'Tcp');
  const comm = new CommunicationManager(server, () => {});
  try {
    await comm.connectTcp({
      host: '127.0.0.1',
      port,
      connectTimeoutMs: 2000,
      autoReconnect: false,
      reconnectIntervalMs: 500,
    });

    // Fire 150 exchanges (including concurrent batches to verify queue & TxId isolation)
    const promises: Promise<void>[] = [];
    for (let i = 1; i <= 150; i++) {
      const txId = i;
      const req = ModbusFrame.buildRequest(1, 3, 0, 2, 'Tcp', txId);
      promises.push(
        comm.exchange(req, 1500, 1, 'Tcp').then((resp) => {
          const parsed = ModbusFrame.parseResponse(resp, 'Tcp');
          assert.equal(parsed.transactionId, txId);
          const temp = RegisterValueConverter.convert(
            parsed.registers,
            'Float',
            'WordSwap',
            1
          );
          assert.ok(Math.abs(temp - 36.5) < 1e-4);
        })
      );
    }
    await Promise.all(promises);
    await comm.disconnect(false);

    // UDP echo responder test + clean async close
    const udpPeer = dgram.createSocket('udp4');
    await new Promise<void>((resolve) => udpPeer.bind(0, '127.0.0.1', resolve));
    const udpPort = udpPeer.address().port;
    udpPeer.on('message', (msg, rinfo) => {
      const parsed = ModbusFrame.parseRequest(new Uint8Array(msg), 'Rtu');
      const pdu = new Uint8Array([parsed.functionCode, 0x02, 0x04, 0xd2]);
      const respBody = new Uint8Array([parsed.unitId, ...pdu]);
      const reply = ModbusFrame.encode(respBody, 'Rtu');
      udpPeer.send(reply, rinfo.port, rinfo.address);
    });

    try {
      await comm.connectUdp({
        remoteHost: '127.0.0.1',
        remotePort: udpPort,
        localPort: 0,
      });
      const rtuReq = ModbusFrame.buildRequest(1, 3, 0, 1, 'Rtu');
      const rtuResp = await comm.exchange(rtuReq, 1000, 1, 'Rtu');
      const parsedUdp = ModbusFrame.parseResponse(rtuResp, 'Rtu');
      assert.deepEqual(parsedUdp.registers, [1234]);
    } finally {
      await comm.disconnect(false);
      await new Promise<void>((resolve) => udpPeer.close(() => resolve()));
    }
  } finally {
    await server.stop();
  }
});
