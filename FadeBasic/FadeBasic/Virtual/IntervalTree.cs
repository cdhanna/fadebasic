using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FadeBasic.Virtual
{
    [DebuggerDisplay("tree({min}-{max}) [{rangesSortedByStart.Count}]")]
    public class IntervalTree
    {
        public IntervalTree left, right;
        public List<DebugMap> rangesSortedByStart, rangesSortedByStop;
        public int min, max, center;
        

        public static IntervalTree From(List<DebugMap> maps)
        {
            var firstIndex = maps[0].range.startToken.insIndex;
            var lastIndex = maps[maps.Count - 1].range.stopToken.insIndex;
            var center = (lastIndex + firstIndex) / 2;

            var left = new List<DebugMap>();
            var right = new List<DebugMap>();
            var mid = new List<DebugMap>();
            foreach (var map in maps)
            {
                if (map.range.startToken.insIndex < center && map.range.stopToken.insIndex < center)
                {
                    left.Add(map);
                } else if (map.range.startToken.insIndex > center && map.range.stopToken.insIndex > center)
                {
                    right.Add(map);
                }
                else
                {
                    mid.Add(map);
                }
            }

            var midStartSorted = mid.ToList();
            midStartSorted.Sort((a, b) => a.range.startToken.insIndex.CompareTo(b.range.startToken.insIndex));
            var midStopSorted = mid.ToList();
            midStopSorted.Sort((a, b) => a.range.stopToken.insIndex.CompareTo(b.range.stopToken.insIndex));

            return new IntervalTree
            {
                min = firstIndex,
                max = lastIndex,
                left = left.Count > 0 ? From(left) : null,
                right = right.Count > 0 ? From(right) : null,
                center = center,
                rangesSortedByStart = midStartSorted,
                rangesSortedByStop = midStopSorted
            };
        }

        public bool TryFind(int index, out DebugMap location)
        {
            location = default;

            var leftish = index < center;
            if (leftish)
            {
                for (var i = 0; i < rangesSortedByStart.Count; i++)
                {
                    if (rangesSortedByStart[i].range.startToken.insIndex <= index)
                    {
                        // just take the first one and be done with it.
                        // TODO: maybe this will produce bad results?
                        location = rangesSortedByStart[i];
                        return true;
                    }
                }

                return left?.TryFind(index, out location) ?? false;
            }
            else
            {
                for (var i = 0; i < rangesSortedByStop.Count; i++)
                {
                    if (rangesSortedByStop[i].range.stopToken.insIndex >= index)
                    {
                        location = rangesSortedByStop[i];
                        return true;
                    }
                }

                return right?.TryFind(index, out location) ?? false;
            }
        }
    }

    public struct SourceLocation
    {
        public int lineNumber, charNumber;
    }
}