using System.Collections.Concurrent;

namespace InfoPanel.Preview.Video;

/// <summary>
/// The ordered output queue of a video stream and its bound: one count of queued items plus reservations, at most
/// <see cref="Capacity"/>. A frame reserves its slot before it is submitted, so its encoder's callback always finds room
/// and never waits, and an encoded frame is never dropped for lack of room: when the queue is full the stream stops
/// submitting, and raw frames drop before encoding instead.
/// </summary>
internal sealed class OutputSlots<T>(int capacity)
{
    private readonly ConcurrentQueue<T> _queue = new();
    private int _occupied;

    public int Capacity { get; } = capacity;

    /// <summary>Queued items plus outstanding reservations.</summary>
    public int Occupied => Volatile.Read(ref _occupied);

    /// <summary>Items queued and not yet taken.</summary>
    public int Queued => _queue.Count;

    /// <summary>A reservation of one slot, or null when all are taken.</summary>
    public Reservation? TryReserve()
    {
        while (true)
        {
            var occupied = Volatile.Read(ref _occupied);
            if (occupied >= Capacity)
                return null;
            if (Interlocked.CompareExchange(ref _occupied, occupied + 1, occupied) == occupied)
                return new Reservation(this);
        }
    }

    /// <summary>The oldest queued item, giving its slot back.</summary>
    public bool TryTake(out T item)
    {
        if (!_queue.TryDequeue(out item!))
            return false;
        Interlocked.Decrement(ref _occupied);
        return true;
    }

    /// <summary>
    /// One reserved slot, owned: <see cref="Commit"/> moves it into the queue with its item; disposing it without a
    /// commit gives the slot back. Every path that queues nothing disposes it (in a <c>finally</c>).
    /// </summary>
    public sealed class Reservation(OutputSlots<T> slots) : IDisposable
    {
        private int _done;

        /// <summary>Queues the item in this slot; false if the reservation was already committed or given back.</summary>
        public bool Commit(T item)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
                return false;
            slots._queue.Enqueue(item);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref slots._occupied);
        }
    }
}
