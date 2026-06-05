using System.Collections.Generic;

namespace PeakRoutePlanner.Planning;

internal sealed class PriorityQueue<T>
{
    private readonly List<Entry> entries = [];

    internal int Count => entries.Count;

    internal void Clear()
    {
        entries.Clear();
    }

    internal void Enqueue(T item, float priority)
    {
        entries.Add(new Entry(item, priority));
        BubbleUp(entries.Count - 1);
    }

    internal T Dequeue()
    {
        T item = entries[0].Item;
        Entry last = entries[entries.Count - 1];
        entries.RemoveAt(entries.Count - 1);
        if (entries.Count > 0)
        {
            entries[0] = last;
            BubbleDown(0);
        }

        return item;
    }

    private void BubbleUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (entries[parent].Priority <= entries[index].Priority)
            {
                break;
            }

            Swap(parent, index);
            index = parent;
        }
    }

    private void BubbleDown(int index)
    {
        while (true)
        {
            int left = index * 2 + 1;
            int right = left + 1;
            int smallest = index;
            if (left < entries.Count && entries[left].Priority < entries[smallest].Priority)
            {
                smallest = left;
            }

            if (right < entries.Count && entries[right].Priority < entries[smallest].Priority)
            {
                smallest = right;
            }

            if (smallest == index)
            {
                break;
            }

            Swap(index, smallest);
            index = smallest;
        }
    }

    private void Swap(int left, int right)
    {
        (entries[left], entries[right]) = (entries[right], entries[left]);
    }

    private readonly struct Entry
    {
        internal Entry(T item, float priority)
        {
            Item = item;
            Priority = priority;
        }

        internal T Item { get; }

        internal float Priority { get; }
    }
}
