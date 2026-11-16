using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace WebMap.World
{
    // Reads the shared map that a player explicitly writes to a Cartography
    // Table. Individual player maps stay client-side and are never read here.
    // ZDO access stays on the game thread; decompressing and parsing happens on
    // the thread pool, then new fog is applied back on the game thread.
    internal static class CartographyTables
    {
        private const string PrefabName = "piece_cartographytable";
        private const int MaxCompressedBytes = 16 * 1024 * 1024;
        private const int MaxRawBytes = 32 * 1024 * 1024;
        private const int MaxTextureSize = 4096;
        private const int MaxPinsPerTable = 10000;
        private const int RectsPerFrame = 8;
        private const float MapHalfSize = 12288f;

        private struct Rect
        {
            public float minX, minZ, maxX, maxZ;
        }

        private sealed class TableState
        {
            public ulong hash;
            public int length;
            public List<Markers.SharedTablePin> pins = new List<Markers.SharedTablePin>();
        }

        private sealed class ParsedTable
        {
            public int generation;
            public ZDOID uid;
            public ulong hash;
            public List<Rect> rects;
            public List<Markers.SharedTablePin> pins;
            public string error;
        }

        private sealed class PendingReveal
        {
            public ZDOID uid;
            public List<Rect> rects;
            public int offset;
            public int revealed;
        }

        private static readonly Dictionary<ZDOID, TableState> tables = new Dictionary<ZDOID, TableState>();
        private static readonly List<ZDO> scanList = new List<ZDO>();
        private static readonly HashSet<ZDOID> seen = new HashSet<ZDOID>();
        private static readonly object resultLock = new object();
        private static readonly List<ParsedTable> results = new List<ParsedTable>();
        private static readonly Queue<PendingReveal> pending = new Queue<PendingReveal>();
        private static int scanIndex, generation;
        private static bool initialized, scanning, scannedOnce;
        private static float nextScan;
        private static int lastLoggedCount = -1;

        public static void Init()
        {
            Interlocked.Increment(ref generation);
            tables.Clear(); scanList.Clear(); seen.Clear(); pending.Clear();
            lock (resultLock) results.Clear();
            scanIndex = 0; scanning = false; scannedOnce = false; lastLoggedCount = -1;
            nextScan = Time.realtimeSinceStartup;
            initialized = true;
            Markers.SetSharedTablePins(null);
        }

        public static void Stop()
        {
            initialized = false;
            Interlocked.Increment(ref generation);
            tables.Clear(); scanList.Clear(); seen.Clear(); pending.Clear();
            lock (resultLock) results.Clear();
        }

        public static IEnumerator Loop()
        {
            while (true)
            {
                Tick();
                yield return null;
            }
        }

        private static void Tick()
        {
            if (!initialized || (!WebMapConfig.REVEAL_CARTOGRAPHY_TABLES && !WebMapConfig.SHOW_CARTOGRAPHY_PINS)) return;
            DrainResults();
            ApplyPending();

            if (!scanning && Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + WebMapConfig.CARTOGRAPHY_SCAN_INTERVAL;
                scanIndex = 0;
                scanList.Clear();
                scanning = true;
            }
            if (scanning) ScanStep();
        }

        private static void ScanStep()
        {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { scanning = false; return; }
            try
            {
                if (!zdoMan.GetAllZDOsWithPrefabIterative(PrefabName, scanList, ref scanIndex)) return;
            }
            catch (Exception e)
            {
                ZLog.LogWarning("WebMap: cartography table scan failed: " + e.Message);
                scanning = false;
                return;
            }
            scanning = false;
            ProcessScan();
        }

        private static void ProcessScan()
        {
            seen.Clear();
            int withData = 0;
            bool sourcePinsChanged = false;
            foreach (ZDO zdo in scanList)
            {
                if (zdo == null || !seen.Add(zdo.m_uid)) continue;
                if (!tables.TryGetValue(zdo.m_uid, out TableState state))
                    tables[zdo.m_uid] = state = new TableState();

                byte[] data;
                try { data = zdo.GetByteArray(ZDOVars.s_data, null); }
                catch (Exception e) { ZLog.LogWarning("WebMap: could not read a cartography table: " + e.Message); continue; }
                if (data == null || data.Length == 0)
                {
                    state.hash = 0; state.length = 0;
                    if (state.pins.Count > 0) { state.pins.Clear(); sourcePinsChanged = true; }
                    continue;
                }
                withData++;
                if (data.Length > MaxCompressedBytes)
                {
                    ZLog.LogWarning("WebMap: cartography table " + zdo.m_uid + " holds " + data.Length + " bytes; ignoring it");
                    continue;
                }
                ulong hash = Fnv1a64(data);
                if (state.length == data.Length && state.hash == hash) continue;
                state.length = data.Length;
                state.hash = hash;
                var item = new ParsedTable { generation = Volatile.Read(ref generation), uid = zdo.m_uid, hash = hash };
                byte[] copy = (byte[])data.Clone();
                ThreadPool.QueueUserWorkItem(_ => ParseWorker(item, copy));
            }

            var gone = new List<ZDOID>();
            foreach (ZDOID uid in tables.Keys)
                if (!seen.Contains(uid)) gone.Add(uid);
            foreach (ZDOID uid in gone)
            {
                if (tables[uid].pins.Count > 0) sourcePinsChanged = true;
                tables.Remove(uid);
            }

            if (!scannedOnce || lastLoggedCount != tables.Count)
            {
                ZLog.Log("WebMap: found " + tables.Count + " cartography table(s), " + withData + " with shared map data");
                lastLoggedCount = tables.Count;
            }
            scannedOnce = true;
            scanList.Clear();
            if (sourcePinsChanged) PublishSharedPins();
        }

        private static void ParseWorker(ParsedTable item, byte[] compressed)
        {
            try
            {
                byte[] raw = global::Utils.Decompress(compressed);
                if (raw == null || raw.Length > MaxRawBytes) throw new InvalidDataException("shared map is too large");
                ParseSharedMap(raw, out item.rects, out item.pins);
            }
            catch (Exception e) { item.error = e.Message; }
            if (item.generation != Volatile.Read(ref generation)) return;
            lock (resultLock) results.Add(item);
        }

        private static void DrainResults()
        {
            List<ParsedTable> batch = null;
            lock (resultLock)
            {
                if (results.Count > 0) { batch = new List<ParsedTable>(results); results.Clear(); }
            }
            if (batch == null) return;
            bool sourcePinsChanged = false;
            foreach (ParsedTable item in batch)
            {
                if (!tables.TryGetValue(item.uid, out TableState state) || state.hash != item.hash) continue;
                if (item.error != null)
                {
                    ZLog.LogWarning("WebMap: could not read cartography table " + item.uid + ": " + item.error);
                    continue;
                }
                if (WebMapConfig.REVEAL_CARTOGRAPHY_TABLES && item.rects != null && item.rects.Count > 0)
                    pending.Enqueue(new PendingReveal { uid = item.uid, rects = item.rects });
                if (WebMapConfig.SHOW_CARTOGRAPHY_PINS)
                {
                    state.pins = item.pins ?? new List<Markers.SharedTablePin>();
                    sourcePinsChanged = true;
                }
            }
            if (sourcePinsChanged) PublishSharedPins();
        }

        private static void PublishSharedPins()
        {
            var pins = new List<Markers.SharedTablePin>();
            foreach (var state in tables.Values)
                if (state.pins != null) pins.AddRange(state.pins);
            Markers.SetSharedTablePins(pins);
        }

        private static void ApplyPending()
        {
            if (pending.Count == 0) return;
            PendingReveal work = pending.Peek();
            int end = Math.Min(work.offset + RectsPerFrame, work.rects.Count);
            for (; work.offset < end; work.offset++)
            {
                Rect r = work.rects[work.offset];
                work.revealed += Fog.RevealRect(r.minX, r.minZ, r.maxX, r.maxZ);
            }
            if (work.offset < work.rects.Count) return;
            pending.Dequeue();
            if (work.revealed > 0)
                ZLog.Log("WebMap: cartography table " + work.uid + " revealed " + work.revealed + " new fog cells");
        }

        // Minimap.GetSharedMapData: version, explored bitmap length, bitmap,
        // then (from version 2) pin count and pin records. Pin records are read
        // with ZPackage so Valheim's own string/vector serialization is retained.
        private static void ParseSharedMap(byte[] raw, out List<Rect> rects, out List<Markers.SharedTablePin> pins)
        {
            var package = new ZPackage(raw);
            int version = package.ReadInt();
            int length = package.ReadInt();
            int mapOffset = package.GetPos();
            int size = (int)Math.Round(Math.Sqrt(length));
            if (length <= 0 || size * size != length || size > MaxTextureSize || mapOffset > raw.Length - length)
                throw new InvalidDataException("unexpected explored bitmap size: " + length);

            float pixel = MapHalfSize * 2f / size;
            float origin = -(size / 2f) * pixel - pixel / 2f;
            rects = new List<Rect>();
            for (int row = 0; row < size; row++)
            {
                int rowStart = mapOffset + row * size, runStart = -1;
                for (int col = 0; col <= size; col++)
                {
                    bool set = col < size && raw[rowStart + col] != 0;
                    if (set && runStart < 0) runStart = col;
                    if (set || runStart < 0) continue;
                    rects.Add(new Rect
                    {
                        minX = origin + runStart * pixel,
                        maxX = origin + col * pixel,
                        minZ = origin + row * pixel,
                        maxZ = origin + (row + 1) * pixel,
                    });
                    runStart = -1;
                }
            }

            pins = new List<Markers.SharedTablePin>();
            package.SetPos(mapOffset + length);
            if (version < 2 || package.GetPos() >= package.Size()) return;
            int count = package.ReadInt();
            if (count < 0 || count > MaxPinsPerTable) throw new InvalidDataException("unexpected shared pin count: " + count);
            for (int i = 0; i < count; i++)
            {
                package.ReadLong(); // owner; personal identity is not published to the web map
                string label = package.ReadString();
                Vector3 position = package.ReadVector3();
                int pinType = package.ReadInt(); // Minimap.PinType; kept for icon/cat, ignored when merging
                bool checkedOff = package.ReadBool();
                if (version >= 3) package.ReadString(); // author platform ID
                pins.Add(new Markers.SharedTablePin
                {
                    x = position.x, y = position.y, z = position.z,
                    label = label, pinType = pinType, checkedOff = checkedOff,
                });
            }
        }

        private static ulong Fnv1a64(byte[] data)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in data) hash = (hash ^ b) * 1099511628211UL;
            return hash;
        }
    }
}
