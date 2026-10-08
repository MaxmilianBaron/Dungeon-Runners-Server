using System;
using System.Collections.Generic;
using System.IO;

namespace DungeonRunners.Core
{
    public sealed class HybridCollisionObject
    {
        public string Name { get; private set; }
        public int WalkCellSize { get; private set; }
        public int WalkOriginX { get; private set; }
        public int WalkOriginY { get; private set; }
        public int WalkGridX { get; private set; }
        public int WalkGridY { get; private set; }
        public int BlockCellSize { get; private set; }
        public int BlockOriginX { get; private set; }
        public int BlockOriginY { get; private set; }
        public int BlockOriginZ { get; private set; }
        public int BlockGridX { get; private set; }
        public int BlockGridY { get; private set; }
        public int BlockGridZ { get; private set; }
        public int NonEmptyBuckets { get; private set; }
        public int RangeCount { get; private set; }
        public int BodyOffset { get; private set; }

        private short[] _walkHeights = Array.Empty<short>();
        private List<VerticalRange>[] _blockBuckets = Array.Empty<List<VerticalRange>>();
        private bool _hasBlockExtents;
        private int _blockMinFixedX;
        private int _blockMinFixedY;
        private int _blockMinFixedZ;
        private int _blockMaxFixedX;
        private int _blockMaxFixedY;
        private int _blockMaxFixedZ;

        public bool HasHeightSurface => _walkHeights.Length != 0 && WalkGridX >= 2 && WalkGridY >= 2 && WalkCellSize > 0;

        private struct VerticalRange
        {
            public short LowZ;
            public short HighZ;
        }

        public static bool TryLoadFromAuthoredData(string collisionObjectName, out HybridCollisionObject cobj, out string source)
        {
            cobj = null;
            source = null;
            if (string.IsNullOrWhiteSpace(collisionObjectName))
                return false;

            string fileName = collisionObjectName.Trim();
            if (DungeonRunners.Data.AuthoredSnapshotCatalog.Instance.TryLoadBinaryPayload(4, fileName, out byte[] authoredBytes, out int entryIndex))
            {
                if (TryParse(fileName, authoredBytes, out cobj, out string authoredError))
                {
                    source = $"authored.db:type4:entry={entryIndex}";
                    return true;
                }
                DungeonRunners.Engine.Debug.LogError($"[COBJ] parse-failed name='{fileName}' source='authored.db:type4:entry={entryIndex}' error='{authoredError}' sourceFunction=HybridCollisionObject::readObject");
                return false;
            }
            return false;
        }

        public static bool TryParse(string name, byte[] bytes, out HybridCollisionObject cobj, out string error)
        {
            cobj = null;
            error = null;
            if (bytes == null || bytes.Length < 0x1B + 40)
            {
                error = "too-small";
                return false;
            }

            try
            {
                if (!TryResolveReadObjectBodyOffset(bytes, out int bodyOffset, out error))
                    return false;

                var parsed = new HybridCollisionObject { Name = name };
                using var ms = new MemoryStream(bytes, false);
                ms.Position = bodyOffset;
                using var reader = new BinaryReader(ms);
                parsed.BodyOffset = bodyOffset;
                parsed.WalkCellSize = reader.ReadInt32();
                parsed.WalkOriginX = reader.ReadInt32();
                parsed.WalkOriginY = reader.ReadInt32();
                parsed.WalkGridX = reader.ReadInt32();
                parsed.WalkGridY = reader.ReadInt32();
                if (parsed.WalkGridX < 0 || parsed.WalkGridY < 0 || parsed.WalkGridX > 4096 || parsed.WalkGridY > 4096)
                {
                    error = "invalid-walk-grid";
                    return false;
                }

                int walkCount = parsed.WalkGridX * parsed.WalkGridY;
                parsed._walkHeights = new short[walkCount];
                for (int walkIndex = 0; walkIndex < walkCount; walkIndex++)
                    parsed._walkHeights[walkIndex] = reader.ReadInt16();

                parsed.BlockCellSize = reader.ReadInt32();
                parsed.BlockOriginX = reader.ReadInt32();
                parsed.BlockOriginY = reader.ReadInt32();
                parsed.BlockOriginZ = reader.ReadInt32();
                parsed.BlockGridX = reader.ReadInt32();
                parsed.BlockGridY = reader.ReadInt32();
                parsed.BlockGridZ = reader.ReadInt32();
                if (parsed.BlockGridX < 0 || parsed.BlockGridY < 0 || parsed.BlockGridZ < 0 ||
                    parsed.BlockGridX > 4096 || parsed.BlockGridY > 4096 || parsed.BlockGridZ > 4096)
                {
                    error = "invalid-block-grid";
                    return false;
                }

                int bucketCount = parsed.BlockGridX * parsed.BlockGridY;
                parsed._blockBuckets = new List<VerticalRange>[bucketCount];
                for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
                {
                    ushort count = reader.ReadUInt16();
                    if (count == 0)
                        continue;

                    var ranges = new List<VerticalRange>(count);
                    for (int rangeIndex = 0; rangeIndex < count; rangeIndex++)
                    {
                        var range = new VerticalRange
                        {
                            LowZ = reader.ReadInt16(),
                            HighZ = reader.ReadInt16()
                        };
                        ranges.Add(range);
                        parsed.RangeCount++;
                    }
                    ranges.Sort(CompareHybridRangeByBottomHeight);
                    parsed._blockBuckets[bucketIndex] = ranges;
                    parsed.NonEmptyBuckets++;
                }

                parsed._hasBlockExtents = parsed.RangeCount > 0;
                if (parsed._hasBlockExtents)
                {
                    parsed._blockMinFixedX = parsed.BlockOriginX << 8;
                    parsed._blockMinFixedY = parsed.BlockOriginY << 8;
                    parsed._blockMinFixedZ = parsed.BlockOriginZ << 8;
                    parsed._blockMaxFixedX = (parsed.BlockOriginX + (parsed.BlockGridX * parsed.BlockCellSize)) << 8;
                    parsed._blockMaxFixedY = (parsed.BlockOriginY + (parsed.BlockGridY * parsed.BlockCellSize)) << 8;
                    parsed._blockMaxFixedZ = (parsed.BlockOriginZ + (parsed.BlockGridZ * parsed.BlockCellSize)) << 8;
                }

                if (reader.BaseStream.Position != reader.BaseStream.Length)
                {
                    error = $"trailing-bytes pos={reader.BaseStream.Position} len={reader.BaseStream.Length}";
                    return false;
                }

                cobj = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryResolveReadObjectBodyOffset(byte[] bytes, out int bodyOffset, out string error)
        {
            bodyOffset = 0;
            error = null;
            const int wrapperClassStart = 1;
            const string expectedClass = "HybridCollisionObject";
            if (bytes.Length < 0x1B + 4)
            {
                error = "too-small-wrapper";
                return false;
            }

            int zero = Array.IndexOf(bytes, (byte)0, wrapperClassStart);
            if (zero < 0)
            {
                error = "missing-wrapper-terminator";
                return false;
            }

            string className = System.Text.Encoding.ASCII.GetString(bytes, wrapperClassStart, zero - wrapperClassStart);
            if (!string.Equals(className, expectedClass, StringComparison.Ordinal))
            {
                error = $"unexpected-wrapper-class '{className}'";
                return false;
            }

            bodyOffset = zero + 5;
            if (bodyOffset != 0x1B)
            {
                error = $"unexpected-body-offset 0x{bodyOffset:X}";
                return false;
            }

            return true;
        }

        public bool TestSegmentFixed(int startX, int startY, int startZ, int endX, int endY, int endZ, int radiusFixed, out int tHitFixed)
        {
            tHitFixed = 0;
            if (_blockBuckets.Length == 0 || BlockGridX <= 0 || BlockGridY <= 0 || BlockCellSize <= 0)
                return false;

            radiusFixed = Math.Max(0, radiusFixed);
            int boxMinX = startX - radiusFixed;
            int boxMinY = startY - radiusFixed;
            int boxMinZ = startZ - radiusFixed;
            int boxMaxX = startX + radiusFixed;
            int boxMaxY = startY + radiusFixed;
            int boxMaxZ = startZ + radiusFixed;
            if (TestBoundingBoxFixed(boxMinX, boxMinY, boxMinZ, boxMaxX, boxMaxY, boxMaxZ))
                return true;

            int dx = endX - startX;
            int dy = endY - startY;
            int dz = endZ - startZ;
            int sampleCount = NativeHybridSweepSampleCount(dx, dy, dz);
            int denominatorFixed = (sampleCount + 1) << 8;
            int stepX = (int)(((long)dx << 8) / denominatorFixed);
            int stepY = (int)(((long)dy << 8) / denominatorFixed);
            int stepZ = (int)(((long)dz << 8) / denominatorFixed);

            boxMinX += stepX;
            boxMinY += stepY;
            boxMinZ += stepZ;
            boxMaxX += stepX;
            boxMaxY += stepY;
            boxMaxZ += stepZ;

            int sampleIndex = 1;
            while (sampleIndex < sampleCount)
            {
                if (TestBoundingBoxFixed(boxMinX, boxMinY, boxMinZ, boxMaxX, boxMaxY, boxMaxZ))
                {
                    tHitFixed = NativeHybridSweepT(sampleIndex, sampleCount);
                    return true;
                }
                boxMinX += stepX;
                boxMinY += stepY;
                boxMinZ += stepZ;
                boxMaxX += stepX;
                boxMaxY += stepY;
                boxMaxZ += stepZ;
                sampleIndex++;
            }

            if (!TestBoundingBoxFixed(boxMinX, boxMinY, boxMinZ, boxMaxX, boxMaxY, boxMaxZ))
                return false;
            tHitFixed = NativeHybridSweepT(sampleIndex, sampleCount);
            return true;
        }

        private static int NativeHybridSweepSampleCount(int dx, int dy, int dz)
        {
            int maxAxis = NativeAbsInt32(dx);
            int absY = NativeAbsInt32(dy);
            if (absY > maxAxis) maxAxis = absY;
            int absZ = NativeAbsInt32(dz);
            if (absZ > maxAxis) maxAxis = absZ;
            return (maxAxis >> 8) / 3;
        }

        private static int NativeHybridSweepT(int sampleIndex, int sampleCount)
        {
            if (sampleIndex <= 0)
                return 0;
            int denominator = sampleCount + 1;
            int tFixed = denominator <= 0 ? 0x100 : (sampleIndex << 8) / denominator;
            return Clamp(tFixed, 0, 0x100);
        }

        public bool TestBoundingBoxFixed(int centerX, int centerY, int centerZ, int radiusFixed)
        {
            return TestBoundingBoxFixed(
                centerX - radiusFixed,
                centerY - radiusFixed,
                centerZ - radiusFixed,
                centerX + radiusFixed,
                centerY + radiusFixed,
                centerZ + radiusFixed);
        }

        public bool TestBoundingBoxFixed(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            if (_blockBuckets.Length == 0 || BlockCellSize <= 0)
                return false;
            if (!NativeBlockExtentsOverlap(minX, minY, minZ, maxX, maxY, maxZ))
                return false;

            int rawGx0 = NativeBlockGridIndexFromFixed(minX, BlockOriginX, BlockCellSize);
            int rawGy0 = NativeBlockGridIndexFromFixed(minY, BlockOriginY, BlockCellSize);
            int rawGx1 = NativeBlockGridIndexFromFixed(maxX, BlockOriginX, BlockCellSize);
            int rawGy1 = NativeBlockGridIndexFromFixed(maxY, BlockOriginY, BlockCellSize);
            if (rawGx1 < 0 || rawGy1 < 0 || rawGx0 >= BlockGridX || rawGy0 >= BlockGridY)
                return false;
            int gx0 = Clamp(rawGx0, 0, BlockGridX - 1);
            int gy0 = Clamp(rawGy0, 0, BlockGridY - 1);
            int gx1 = Clamp(rawGx1, 0, BlockGridX - 1);
            int gy1 = Clamp(rawGy1, 0, BlockGridY - 1);

            for (int gy = gy0; gy <= gy1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    var ranges = _blockBuckets[gy * BlockGridX + gx];
                    if (ranges == null)
                        continue;
                    for (int rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
                    {
                        var range = ranges[rangeIndex];
                        int low = range.LowZ << 8;
                        int high = range.HighZ << 8;
                        if (low > maxZ)
                            break;
                        if (maxZ >= low && minZ <= high)
                            return true;
                    }
                }
            }

            return false;
        }

        private bool NativeBlockExtentsOverlap(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            if (!_hasBlockExtents)
                return false;
            return minX <= _blockMaxFixedX
                && minY <= _blockMaxFixedY
                && minZ <= _blockMaxFixedZ
                && _blockMinFixedX <= maxX
                && _blockMinFixedY <= maxY
                && _blockMinFixedZ <= maxZ;
        }

        private static int NativeBlockGridIndexFromFixed(int fixedCoord, int originWorld, int cellWorld)
        {
            if (cellWorld <= 0) return 0;
            return ((fixedCoord >> 8) - originWorld) / cellWorld;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int NativeAbsInt32(int value)
        {
            if (value == int.MinValue)
                return int.MaxValue;
            return value < 0 ? -value : value;
        }

        private static int CompareHybridRangeByBottomHeight(VerticalRange left, VerticalRange right)
        {
            int lowCompare = left.LowZ.CompareTo(right.LowZ);
            return lowCompare != 0 ? lowCompare : left.HighZ.CompareTo(right.HighZ);
        }

        public bool GetHeight(int xFixed8, int yFixed8, out int heightFixed8)
        {
            heightFixed8 = 0;
            int gridW = WalkGridX;
            int gridH = WalkGridY;
            int cellSize = WalkCellSize;
            if (_walkHeights.Length == 0 || gridW < 2 || gridH < 2 || cellSize <= 0)
                return false;

            int x = xFixed8 - WalkOriginX * 0x100;
            int y = yFixed8 - WalkOriginY * 0x100;
            if (x < 0) x += 0x100;
            if (y < 0) y += 0x100;
            if (cellSize * (gridW - 1) * 0x100 <= x) x -= 0x100;
            if ((gridH - 1) * cellSize * 0x100 <= y) y -= 0x100;
            if ((x >> 8) < 0 || (y >> 8) < 0)
                return false;

            int gx = (x >> 8) / cellSize;
            int gy = (y >> 8) / cellSize;
            if (gx >= gridW - 1 || gy >= gridH - 1)
                return false;

            int heightIndex = gridW * gy + gx;
            if (heightIndex < 0 || heightIndex + gridW + 1 >= _walkHeights.Length)
                return false;

            int sample1 = _walkHeights[heightIndex];
            int sample2 = _walkHeights[heightIndex + 1];
            int sample3 = _walkHeights[heightIndex + gridW];
            int sample4 = _walkHeights[heightIndex + gridW + 1];
            const int Sentinel = -0x7fff;
            if (sample1 == Sentinel || sample2 == Sentinel || sample3 == Sentinel || sample4 == Sentinel)
                return false;
            long sampleDelta12 = (long)sample1 - sample2;
            if (sampleDelta12 < 0)
                sampleDelta12 = -sampleDelta12;
            long sampleDelta13 = (long)sample1 - sample3;
            if (sampleDelta13 < 0)
                sampleDelta13 = -sampleDelta13;
            long sampleDelta14 = (long)sample1 - sample4;
            if (sampleDelta14 < 0)
                sampleDelta14 = -sampleDelta14;
            if (sampleDelta12 >= 0x33 || sampleDelta13 >= 0x33 || sampleDelta14 >= 0x33)
                return false;

            int cell8 = cellSize * 0x100;
            int fracX = ((x - ((gx * 0x100 * cell8) >> 8)) * 0x100) / cell8;
            int fracY = ((y - ((gy * 0x100 * cell8) >> 8)) * 0x100) / cell8;

            int topRow = ((sample3 * 0x100 * (0x100 - fracX)) >> 8) + ((sample4 * 0x100 * fracX) >> 8);
            int botRow = ((sample1 * 0x100 * (0x100 - fracX)) >> 8) + ((sample2 * 0x100 * fracX) >> 8);
            heightFixed8 = ((((topRow * fracY) >> 8) + (((0x100 - fracY) * botRow) >> 8)) * 0x100) / 0xa00;
            return true;
        }
    }
}
