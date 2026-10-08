using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Core;

namespace DungeonRunners.Networking
{
    public static class WirePacketTally
    {
        private static HashSet<ushort> _monsterEntityIds = new HashSet<ushort>();
        private static HashSet<ushort> _monsterComponentIds = new HashSet<ushort>();
        private static int _totalTCP, _totalUDP, _totalChannel, _monsterHits;
        private static Dictionary<string, int> _tcpTypes = new Dictionary<string, int>();
        private static Dictionary<string, int> _udpTypes = new Dictionary<string, int>();
        private static Dictionary<string, int> _channelTypes = new Dictionary<string, int>();
        private static Dictionary<string, int> _streamSubMessages = new Dictionary<string, int>();
        private static Dictionary<string, int> _componentSubMessages = new Dictionary<string, int>();
        private static List<string> _monsterMessages = new List<string>();
        private static float _lastReport;
        private static bool _active;
        private static bool Enabled => ServerDiagnostics.IsEnabled("wirePacketTracking");
        private static bool Verbose => ServerDiagnostics.IsEnabled("verboseMonsterWireLogging");

        private static void IncrementCount(Dictionary<string, int> counts, string key)
        {
            if (counts.ContainsKey(key)) counts[key]++; else counts[key] = 1;
        }

        public static void RegisterMonster(ushort entityId, ushort behaviorId, ushort skillId, ushort manipulatorId, ushort modifierId)
        {
            if (!Enabled) return;
            _monsterEntityIds.Add(entityId);
            _monsterComponentIds.Add(behaviorId); _monsterComponentIds.Add(skillId); _monsterComponentIds.Add(manipulatorId); _monsterComponentIds.Add(modifierId);
            _active = true;
            Debug.LogError($"[MC] Registered eid={entityId} cids=[{behaviorId},{skillId},{manipulatorId},{modifierId}]");
        }

        public static void OnRawTCP(byte[] data)
        {
            if (!Enabled) return;
            if (!_active || data == null || data.Length < 1 || data[0] == 0x02) return;
            _totalTCP++;
            IncrementCount(_tcpTypes, $"0x{data[0]:X2}");
            ScanForMonsterIds(data, $"TCP-0x{data[0]:X2}");
        }

        public static void OnChannel(byte channel, byte messageType, byte[] data)
        {
            if (!Enabled) return;
            if (!_active || data == null) return;
            _totalChannel++;
            IncrementCount(_channelTypes, $"ch{channel}/0x{messageType:X2}");
            ScanForMonsterIds(data, $"ch{channel}/0x{messageType:X2}");
            if (channel == 7 && messageType == 0x07 && data.Length > 0) ParseStream(data);
            if (channel == 7 && (messageType == 0x34 || messageType == 0x35) && data.Length >= 3)
            {
                ushort componentId = (ushort)(data[0] | (data[1] << 8));
                byte subMessage = data[2];
                IncrementCount(_componentSubMessages, $"cid={componentId}/0x{subMessage:X2}");
                if (Verbose && _monsterComponentIds.Contains(componentId))
                    _monsterMessages.Add($"CH-COMP cid={componentId} sub=0x{subMessage:X2} len={data.Length} hex={Hex(data)}");
            }
        }

        public static void OnUDP(byte[] data)
        {
            if (!Enabled) return;
            if (!_active || data == null || data.Length < 1) return;
            _totalUDP++;
            IncrementCount(_udpTypes, $"0x{data[0]:X2}");
            ScanForMonsterIds(data, $"UDP-0x{data[0]:X2}");
            if (data[0] == 0x07 && data.Length > 1)
            {
                byte[] innerData = new byte[data.Length - 1];
                Array.Copy(data, 1, innerData, 0, innerData.Length);
                ParseStream(innerData);
            }
            if ((data[0] == 0x34 || data[0] == 0x35) && data.Length >= 4)
            {
                ushort componentId = (ushort)(data[1] | (data[2] << 8));
                byte subMessage = data[3];
                IncrementCount(_componentSubMessages, $"UDP-cid={componentId}/0x{subMessage:X2}");
                if (Verbose && _monsterComponentIds.Contains(componentId))
                    _monsterMessages.Add($"UDP-COMP cid={componentId} sub=0x{subMessage:X2} len={data.Length} hex={Hex(data)}");
            }
        }

        private static void ParseStream(byte[] data)
        {
            int position = 0;
            while (position < data.Length)
            {
                byte subMessage = data[position];
                if (subMessage == 0x06 || subMessage == 0x46) break;
                IncrementCount(_streamSubMessages, $"0x{subMessage:X2}");
                if ((subMessage == 0x34 || subMessage == 0x35) && position + 3 < data.Length)
                {
                    ushort componentId = (ushort)(data[position + 1] | (data[position + 2] << 8));
                    byte componentSubMessage = data[position + 3];
                    IncrementCount(_componentSubMessages, $"s-cid={componentId}/0x{componentSubMessage:X2}");
                    if (Verbose && _monsterComponentIds.Contains(componentId))
                        _monsterMessages.Add($"S-COMP cid={componentId} sub=0x{componentSubMessage:X2} @{position} hex={Hex(data, position, 20)}");
                }
                if (subMessage == 0x36 && position + 3 < data.Length)
                {
                    ushort entityId = (ushort)(data[position + 1] | (data[position + 2] << 8));
                    if (Verbose && _monsterEntityIds.Contains(entityId))
                        _monsterMessages.Add($"S-ENTITY-SYNCH eid={entityId} @{position} hex={Hex(data, position, 10)}");
                }
                if (position + 2 < data.Length)
                {
                    ushort candidateId = (ushort)(data[position + 1] | (data[position + 2] << 8));
                    if (Verbose && _monsterComponentIds.Contains(candidateId))
                        _monsterMessages.Add($"S-0x{subMessage:X2} MCID={candidateId} @{position + 1} hex={Hex(data, position, 20)}");
                    if (Verbose && _monsterEntityIds.Contains(candidateId))
                        _monsterMessages.Add($"S-0x{subMessage:X2} MEID={candidateId} @{position + 1} hex={Hex(data, position, 20)}");
                }
                position++;
            }
        }

        private static void ScanForMonsterIds(byte[] data, string source)
        {
            for (int dataOffset = 0; dataOffset < data.Length - 1; dataOffset++)
            {
                ushort candidateId = (ushort)(data[dataOffset] | (data[dataOffset + 1] << 8));
                if (_monsterComponentIds.Contains(candidateId))
                {
                    _monsterHits++;
                    if (Verbose)
                        _monsterMessages.Add($"CID={candidateId} in {source} @{dataOffset} hex={Hex(data, Math.Max(0, dataOffset - 2), 24)}");
                }
                if (_monsterEntityIds.Contains(candidateId))
                {
                    _monsterHits++;
                    if (Verbose)
                        _monsterMessages.Add($"EID={candidateId} in {source} @{dataOffset} hex={Hex(data, Math.Max(0, dataOffset - 2), 24)}");
                }
            }
        }

        public static void Report()
        {
            if (!Enabled) return;
            if (!_active) return;
            if (Time.time - _lastReport < 3f) return;
            _lastReport = Time.time;
            Debug.LogError("[MC] report");
            Debug.LogError($"[MC] tcp={_totalTCP} udp={_totalUDP} ch={_totalChannel} hits={_monsterHits}");
            Debug.LogError($"[MC] eids=[{string.Join(",", _monsterEntityIds)}] cids=[{string.Join(",", _monsterComponentIds)}]");
            if (_tcpTypes.Count > 0) Debug.LogError($"[MC] TCP: {Fmt(_tcpTypes)}");
            if (_udpTypes.Count > 0) Debug.LogError($"[MC] UDP: {Fmt(_udpTypes)}");
            if (_channelTypes.Count > 0) Debug.LogError($"[MC] CH: {Fmt(_channelTypes)}");
            if (_streamSubMessages.Count > 0) Debug.LogError($"[MC] STREAM: {Fmt(_streamSubMessages)}");
            if (_componentSubMessages.Count > 0) Debug.LogError($"[MC] COMP: {Fmt(_componentSubMessages)}");
            if (_monsterMessages.Count > 0)
            {
                Debug.LogError($"[MC] MONSTER ({_monsterMessages.Count})");
                foreach (var monsterMessage in _monsterMessages.Skip(Math.Max(0, _monsterMessages.Count - 20)))
                    Debug.LogError($"[MC] {monsterMessage}");
            }
            else if (_totalTCP + _totalUDP > 0)
                Debug.LogError("[MC] monsterMessages=0 reason=client-channel-or-format");
            Debug.LogError("[MC] report");
        }

        private static string Fmt(Dictionary<string, int> counts)
        {
            return string.Join(" | ", counts.OrderByDescending(entry => entry.Value).Select(entry => $"{entry.Key}={entry.Value}"));
        }

        private static string Hex(byte[] data, int start = 0, int length = 40)
        {
            start = Math.Max(0, Math.Min(start, data.Length));
            length = Math.Min(length, data.Length - start);
            return length <= 0 ? "" : BitConverter.ToString(data, start, length);
        }
    }
}
