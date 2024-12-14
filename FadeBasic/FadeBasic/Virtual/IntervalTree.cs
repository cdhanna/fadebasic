using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FadeBasic.Virtual
{
    public class IntervalTreeNode
    {
        public IntervalTree tree;
        public DebugMap map;
    }
    
    [DebuggerDisplay("tree({min}-{max}) [{rangesSortedByStart.Count}]")]
    public class IntervalTree
    {
        public IntervalTree left, right;
        public List<IntervalTreeNode> rangesSortedByStart, rangesSortedByStop;
        public int min, max, center;
        

        public static IntervalTree From(List<DebugMap> maps)
        {
            if (maps == null || maps.Count == 0)
            {
                return null;
            }
            
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

            var midNodes = mid.Select(x =>
            {
                return new IntervalTreeNode
                {
                    map = x,
                    tree = From(x.innerMaps)
                };
            }).ToList();
            
            var midStartSorted = midNodes.ToList();
            midStartSorted.Sort((a, b) => a.map.range.startToken.insIndex.CompareTo(b.map.range.startToken.insIndex));
            var midStopSorted = midNodes.ToList();
            midStopSorted.Sort((a, b) => a.map.range.stopToken.insIndex.CompareTo(b.map.range.stopToken.insIndex));

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
                    if (rangesSortedByStart[i].map.range.startToken.insIndex <= index)
                    {
                        // just take the first one and be done with it.
                        // TODO: maybe this will produce bad results?
                        if (rangesSortedByStart[i].tree != null)
                        {
                            return rangesSortedByStart[i].tree.TryFind(index, out location);
                        }
                        else
                        {
                            location = rangesSortedByStart[i].map;
                            return true;
                        }
                    }
                }

                return left?.TryFind(index, out location) ?? false;
            }
            else
            {
                for (var i = 0; i < rangesSortedByStop.Count; i++)
                {
                    if (rangesSortedByStop[i].map.range.stopToken.insIndex >= index)
                    {
                        if (rangesSortedByStop[i].tree != null)
                        {
                            return rangesSortedByStop[i].tree.TryFind(index, out location);
                        }
                        else
                        {
                            location = rangesSortedByStop[i].map;
                            return true;

                        }
                        // location = rangesSortedByStop[i];
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