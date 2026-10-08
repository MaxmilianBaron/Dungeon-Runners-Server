using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonRunners.Utilities
{
    internal sealed class NativeCurveTable
    {
        private readonly (int LevelF32, int ValueF32)[] _points;

        internal NativeCurveTable(IEnumerable<(int LevelF32, int ValueF32)> points)
        {
            _points = points.ToArray();
            Sort(_points, 0, _points.Length, _points.Length);
        }

        internal int EvalFixed32(int levelF32)
        {
            if (_points.Length == 0) return 0;
            if (levelF32 < _points[0].LevelF32) levelF32 = _points[0].LevelF32;
            else if (levelF32 > _points[_points.Length - 1].LevelF32) levelF32 = _points[_points.Length - 1].LevelF32;

            int first = 0;
            int count = _points.Length;
            while (count > 0)
            {
                int half = count / 2;
                int middle = first + half;
                if (_points[middle].LevelF32 < levelF32)
                {
                    first = middle + 1;
                    count -= half + 1;
                }
                else count = half;
            }
            if (first == _points.Length) return 0;
            if (_points[first].LevelF32 > levelF32 && first > 0) first--;
            var lower = _points[first];
            if (first + 1 == _points.Length) return lower.ValueF32;
            var upper = _points[first + 1];
            return Fixed32Math.InterpolateCurveValue(lower.LevelF32, lower.ValueF32, upper.LevelF32, upper.ValueF32, levelF32);
        }

        private static void Sort((int LevelF32, int ValueF32)[] points, int first, int end, int ideal)
        {
            while (end - first > 32 && ideal > 0)
            {
                var middle = Partition(points, first, end);
                ideal = ideal / 2 + ideal / 4;
                if (middle.First - first < end - middle.End)
                {
                    Sort(points, first, middle.First, ideal);
                    first = middle.End;
                }
                else
                {
                    Sort(points, middle.End, end, ideal);
                    end = middle.First;
                }
            }
            if (end - first > 32)
            {
                int count = end - first;
                for (int hole = count / 2; hole > 0;)
                {
                    hole--;
                    AdjustHeap(points, first, hole, count, points[first + hole]);
                }
                while (count > 1)
                {
                    var value = points[first + --count];
                    points[first + count] = points[first];
                    AdjustHeap(points, first, 0, count, value);
                }
            }
            else
            {
                for (int next = first + 1; next < end; next++)
                {
                    var value = points[next];
                    int hole = next;
                    while (hole > first && value.LevelF32 < points[hole - 1].LevelF32)
                    {
                        points[hole] = points[hole - 1];
                        hole--;
                    }
                    points[hole] = value;
                }
            }
        }

        private static (int First, int End) Partition((int LevelF32, int ValueF32)[] points, int first, int end)
        {
            int middle = first + (end - first) / 2;
            Median(points, first, middle, end - 1);
            int pivotFirst = middle;
            int pivotEnd = middle + 1;
            while (pivotFirst > first && points[pivotFirst - 1].LevelF32 == points[pivotFirst].LevelF32) pivotFirst--;
            while (pivotEnd < end && points[pivotEnd].LevelF32 == points[pivotFirst].LevelF32) pivotEnd++;
            int greaterFirst = pivotEnd;
            int greaterEnd = pivotFirst;
            while (true)
            {
                for (; greaterFirst < end; greaterFirst++)
                {
                    if (points[pivotFirst].LevelF32 < points[greaterFirst].LevelF32) continue;
                    if (points[greaterFirst].LevelF32 < points[pivotFirst].LevelF32) break;
                    Swap(points, pivotEnd++, greaterFirst);
                }
                for (; greaterEnd > first; greaterEnd--)
                {
                    if (points[greaterEnd - 1].LevelF32 < points[pivotFirst].LevelF32) continue;
                    if (points[pivotFirst].LevelF32 < points[greaterEnd - 1].LevelF32) break;
                    Swap(points, --pivotFirst, greaterEnd - 1);
                }
                if (greaterEnd == first && greaterFirst == end) return (pivotFirst, pivotEnd);
                if (greaterEnd == first)
                {
                    if (pivotEnd != greaterFirst) Swap(points, pivotFirst, pivotEnd);
                    pivotEnd++;
                    Swap(points, pivotFirst++, greaterFirst++);
                }
                else if (greaterFirst == end)
                {
                    Swap(points, --greaterEnd, --pivotFirst);
                    Swap(points, pivotFirst, --pivotEnd);
                }
                else Swap(points, greaterFirst++, --greaterEnd);
            }
        }

        private static void Median((int LevelF32, int ValueF32)[] points, int first, int middle, int last)
        {
            if (last - first > 40)
            {
                int step = (last - first + 1) / 8;
                MedianThree(points, first, first + step, first + 2 * step);
                MedianThree(points, middle - step, middle, middle + step);
                MedianThree(points, last - 2 * step, last - step, last);
                MedianThree(points, first + step, middle, last - step);
            }
            else MedianThree(points, first, middle, last);
        }

        private static void MedianThree((int LevelF32, int ValueF32)[] points, int first, int middle, int last)
        {
            if (points[middle].LevelF32 < points[first].LevelF32) Swap(points, middle, first);
            if (points[last].LevelF32 < points[middle].LevelF32) Swap(points, last, middle);
            if (points[middle].LevelF32 < points[first].LevelF32) Swap(points, middle, first);
        }

        private static void AdjustHeap((int LevelF32, int ValueF32)[] points, int first, int hole, int count, (int LevelF32, int ValueF32) value)
        {
            int top = hole;
            int child = 2 * hole + 2;
            while (child < count)
            {
                if (points[first + child].LevelF32 < points[first + child - 1].LevelF32) child--;
                points[first + hole] = points[first + child];
                hole = child;
                child = 2 * hole + 2;
            }
            if (child == count)
            {
                points[first + hole] = points[first + child - 1];
                hole = child - 1;
            }
            while (hole > top)
            {
                int parent = (hole - 1) / 2;
                if (points[first + parent].LevelF32 >= value.LevelF32) break;
                points[first + hole] = points[first + parent];
                hole = parent;
            }
            points[first + hole] = value;
        }

        private static void Swap((int LevelF32, int ValueF32)[] points, int first, int second)
        {
            if (first == second) return;
            var value = points[first];
            points[first] = points[second];
            points[second] = value;
        }
    }
}
