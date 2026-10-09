using System.Runtime.InteropServices;

namespace Fiv.Scheduling;

/// <summary>
/// One piece of queued work. Its cancel flag lives in native memory so that native code (the
/// JPEG decoder) can poll it while it runs.
/// </summary>
public sealed unsafe class WorkItem
{
    internal const int Queued = 0, Running = 1, Finished = 2, Removed = 3;

    readonly int* _cancel = (int*)NativeMemory.AllocZeroed(sizeof(int));
    internal Action<WorkItem> Action;
    internal int State;
    internal long Sequence;

    internal WorkItem(Action<WorkItem> action, int priority)
    {
        Action = action;
        Priority = priority;
    }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; internal set; }

    /// <summary>Non-zero once cancelled. Valid for as long as this object is reachable.</summary>
    public int* CancelFlag => _cancel;

    public bool IsCancelled => Volatile.Read(ref *_cancel) != 0;

    internal void SetCancelled() => Volatile.Write(ref *_cancel, 1);

    ~WorkItem() => NativeMemory.Free(_cancel);
}

/// <summary>
/// A fixed number of worker threads that always take the queued item with the lowest priority
/// value (the oldest among equals). Priorities can be changed and items cancelled while queued;
/// a running item is told through its cancel flag. Threads start when first needed.
/// </summary>
public sealed class WorkScheduler(int maxWorkers, string name, ThreadPriority threadPriority = ThreadPriority.Normal)
{
    readonly object _gate = new();
    readonly List<WorkItem> _queue = [];
    int _workers, _idle;
    long _sequence;

    public int MaxWorkers { get; } = Math.Max(1, maxWorkers);

    public WorkItem Enqueue(int priority, Action<WorkItem> action)
    {
        var item = new WorkItem(action, priority);
        lock (_gate)
        {
            item.Sequence = _sequence++;
            _queue.Add(item);
            if (_idle > 0)
            {
                Monitor.Pulse(_gate);
            }
            else if (_workers < MaxWorkers)
            {
                _workers++;
                new Thread(Work) { IsBackground = true, Name = $"{name} {_workers}", Priority = threadPriority }.Start();
            }
        }
        return item;
    }

    public void SetPriority(WorkItem item, int priority)
    {
        lock (_gate) item.Priority = priority;
    }

    /// <summary>
    /// Cancels an item. Returns true if it was still queued and will therefore never run; false if
    /// it is running (it will notice its flag) or already finished.
    /// </summary>
    public bool Cancel(WorkItem item)
    {
        item.SetCancelled();
        lock (_gate)
        {
            if (item.State != WorkItem.Queued) return false;
            item.State = WorkItem.Removed;
            _queue.Remove(item);
            return true;
        }
    }

    void Work()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                while (_queue.Count == 0)
                {
                    _idle++;
                    Monitor.Wait(_gate);
                    _idle--;
                }
                int best = 0;
                for (int i = 1; i < _queue.Count; i++)
                {
                    WorkItem a = _queue[i], b = _queue[best];
                    if (a.Priority < b.Priority || (a.Priority == b.Priority && a.Sequence < b.Sequence)) best = i;
                }
                item = _queue[best];
                _queue.RemoveAt(best);
                item.State = WorkItem.Running;
            }
            try
            {
                item.Action(item);
            }
            catch (Exception)
            {
                // Work items report their own failures; one that throws must not take the worker down.
            }
            lock (_gate) item.State = WorkItem.Finished;
        }
    }
}
