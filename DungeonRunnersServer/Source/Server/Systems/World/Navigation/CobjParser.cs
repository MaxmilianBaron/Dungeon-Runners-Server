using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DungeonRunners.Utilities
{
    public static class CobjParser
    {
        public const int HeaderSize = 27;
        public const string ExpectedClassName = "HybridCollisionObject";

        public static CobjData Parse(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length < HeaderSize) throw new InvalidDataException(
                $"cobj too short: {bytes.Length} bytes, need at least {HeaderSize}");

            var reader = new LEReader(bytes);

            byte tag = reader.ReadByte();

            byte[] nameBytes = reader.ReadBytes(ExpectedClassName.Length);
            string name = Encoding.ASCII.GetString(nameBytes);
            if (name != ExpectedClassName) throw new InvalidDataException(
                $"cobj className = '{name}', expected '{ExpectedClassName}' (tag was 0x{tag:X2})");

            byte terminator = reader.ReadByte();
            if (terminator != 0x00) throw new InvalidDataException(
                $"cobj terminator = 0x{terminator:X2}, expected 0x00");

            uint dfcHash = reader.ReadUInt32();

            int cellSize1 = reader.ReadInt32();
            int originX1 = reader.ReadInt32();
            int originY1 = reader.ReadInt32();
            int width1 = reader.ReadInt32();
            int height1 = reader.ReadInt32();

            ValidateGridDimensions(width1, height1, "sub-shape 1");

            int cellCount1 = width1 * height1;
            ushort[] heightmap = new ushort[cellCount1];
            for (int cellIndex = 0; cellIndex < cellCount1; cellIndex++)
            {
                heightmap[cellIndex] = reader.ReadUInt16();
            }

            int cellSize2 = reader.ReadInt32();
            int originX2 = reader.ReadInt32();
            int originY2 = reader.ReadInt32();
            int originZ2 = reader.ReadInt32();
            int width2 = reader.ReadInt32();
            int height2 = reader.ReadInt32();
            int depth2 = reader.ReadInt32();

            ValidateGridDimensions(width2, height2, "sub-shape 2");

            int cellCount2 = width2 * height2;
            CobjBBoxCell[] cells = new CobjBBoxCell[cellCount2];
            for (int cellIndex = 0; cellIndex < cellCount2; cellIndex++)
            {
                ushort count = reader.ReadUInt16();
                var bboxes = new CobjBBox[count];
                for (int boxIndex = 0; boxIndex < count; boxIndex++)
                {
                    short zLow = (short)reader.ReadUInt16();
                    short zHigh = (short)reader.ReadUInt16();
                    bboxes[boxIndex] = new CobjBBox(zLow, zHigh);
                }
                cells[cellIndex] = new CobjBBoxCell(bboxes);
            }

            return new CobjData(
                dfcHash,
                cellSize1, originX1, originY1, width1, height1, heightmap,
                cellSize2, originX2, originY2, originZ2, width2, height2, depth2, cells,
                bytesConsumed: reader.Position);
        }

        private static void ValidateGridDimensions(int width, int height, string label)
        {
            const int MaxDim = 1024;
            if (width < 0 || height < 0 || width > MaxDim || height > MaxDim)
            {
                throw new InvalidDataException(
                    $"cobj {label} grid {width}x{height} out of range [0,{MaxDim}]");
            }
        }
    }

    public sealed class CobjData
    {
        public uint DFCHash { get; }

        public int CellSize1 { get; }
        public int OriginX1 { get; }
        public int OriginY1 { get; }
        public int Width1 { get; }
        public int Height1 { get; }
        public ushort[] Heightmap { get; }

        public int CellSize2 { get; }
        public int OriginX2 { get; }
        public int OriginY2 { get; }
        public int OriginZ2 { get; }
        public int Width2 { get; }
        public int Height2 { get; }
        public int Depth2 { get; }
        public CobjBBoxCell[] Cells { get; }

        public int BytesConsumed { get; }

        public CobjData(
            uint dfcHash,
            int cellSize1, int originX1, int originY1, int width1, int height1, ushort[] heightmap,
            int cellSize2, int originX2, int originY2, int originZ2, int width2, int height2, int depth2,
            CobjBBoxCell[] cells, int bytesConsumed)
        {
            DFCHash = dfcHash;
            CellSize1 = cellSize1;
            OriginX1 = originX1;
            OriginY1 = originY1;
            Width1 = width1;
            Height1 = height1;
            Heightmap = heightmap;
            CellSize2 = cellSize2;
            OriginX2 = originX2;
            OriginY2 = originY2;
            OriginZ2 = originZ2;
            Width2 = width2;
            Height2 = height2;
            Depth2 = depth2;
            Cells = cells;
            BytesConsumed = bytesConsumed;
        }

        public ushort GetHeight(int cx, int cy)
        {
            if (cx < 0 || cx >= Width1 || cy < 0 || cy >= Height1) return 0;
            return Heightmap[cy * Width1 + cx];
        }

        public CobjBBoxCell GetCell(int cx, int cy)
        {
            if (cx < 0 || cx >= Width2 || cy < 0 || cy >= Height2)
                return CobjBBoxCell.Empty;
            return Cells[cy * Width2 + cx];
        }
    }

    public readonly struct CobjBBox
    {
        public readonly short ZLow;
        public readonly short ZHigh;
        public CobjBBox(short zLow, short zHigh) { ZLow = zLow; ZHigh = zHigh; }
    }

    public sealed class CobjBBoxCell
    {
        public static readonly CobjBBoxCell Empty = new CobjBBoxCell(Array.Empty<CobjBBox>());
        public CobjBBox[] BBoxes { get; }
        public CobjBBoxCell(CobjBBox[] bboxes) { BBoxes = bboxes ?? Array.Empty<CobjBBox>(); }
    }
}
