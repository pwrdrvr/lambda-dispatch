using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PwrDrvr.LambdaDispatch.Router;


/// <summary>
/// A TaskScheduler that maintains thread affinity for tasks and prioritizes thread utilization
/// to minimize context switching overhead in high-throughput, low-latency scenarios.
/// </summary>
public class AffinityTaskScheduler : TaskScheduler, IDisposable
{
  // Thread-local storage to track the worker associated with the current thread
  private static readonly ThreadLocal<AffinityWorker> _currentWorker = new ThreadLocal<AffinityWorker>();

  // Collection of worker threads
  private readonly AffinityWorker[] _workers;

  // Number of worker threads
  private readonly int _workerCount;

  // Indicates whether the scheduler is currently running
  private bool _isRunning = true;

  // Tracks tasks that don't have an affinity yet (not created by one of our threads)
  private readonly ConcurrentQueue<Task> _unaffinitizedTasks = new ConcurrentQueue<Task>();

  // Event used to signal workers when new unaffinitized work is available
  private readonly ManualResetEventSlim _workAvailable = new ManualResetEventSlim(false);

  /// <summary>
  /// Initializes a new instance of the AffinityTaskScheduler.
  /// </summary>
  /// <param name="maxDegreeOfParallelism">The maximum number of worker threads to use.</param>
  public AffinityTaskScheduler(int maxDegreeOfParallelism = 0)
  {
    // If maxDegreeOfParallelism is not specified or invalid, use Environment.ProcessorCount
    _workerCount = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
    _workers = new AffinityWorker[_workerCount];

    // Initialize and start the worker threads
    for (int i = 0; i < _workerCount; i++)
    {
      var worker = new AffinityWorker(this, i);
      _workers[i] = worker;
      worker.Start();
    }
  }

  /// <summary>
  /// Gets the maximum concurrency level supported by this scheduler.
  /// </summary>
  public override int MaximumConcurrencyLevel => _workerCount;

  /// <summary>
  /// Queues a task to the scheduler.
  /// </summary>
  /// <param name="task">The task to be queued.</param>
  protected override void QueueTask(Task task)
  {
    // Try to get the current worker associated with this thread
    AffinityWorker currentWorker = _currentWorker.Value;

    if (currentWorker != null)
    {
      // If we're on one of our worker threads, queue the task to that worker
      currentWorker.EnqueueTask(task);
    }
    else
    {
      // If we're not on one of our worker threads, queue to the unaffinitized queue
      _unaffinitizedTasks.Enqueue(task);
      _workAvailable.Set();
    }
  }

  /// <summary>
  /// Determines whether a task can be executed inline on the current thread.
  /// </summary>
  protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
  {
    // Only execute inline if we're on one of our worker threads
    if (_currentWorker.Value != null)
    {
      return TryExecuteTask(task);
    }

    return false;
  }

  /// <summary>
  /// Gets all the tasks currently scheduled to this scheduler.
  /// </summary>
  /// <returns>An enumerable of all scheduled tasks.</returns>
  protected override IEnumerable<Task> GetScheduledTasks()
  {
    // Collect tasks from the unaffinitized queue
    foreach (var task in _unaffinitizedTasks)
    {
      yield return task;
    }

    // Collect tasks from all workers
    foreach (var worker in _workers)
    {
      foreach (var task in worker.GetScheduledTasks())
      {
        yield return task;
      }
    }
  }

  /// <summary>
  /// Tries to find and execute an unaffinitized task.
  /// </summary>
  /// <returns>True if a task was found and executed; otherwise, false.</returns>
  internal bool TryExecuteUnaffinitizedTask()
  {
    if (_unaffinitizedTasks.TryDequeue(out Task task))
    {
      TryExecuteTask(task);
      return true;
    }

    if (_unaffinitizedTasks.IsEmpty)
    {
      _workAvailable.Reset();
    }

    return false;
  }

  /// <summary>
  /// Waits for work to be available.
  /// </summary>
  /// <param name="timeout">The amount of time to wait.</param>
  /// <returns>True if work became available; otherwise, false.</returns>
  internal bool WaitForWork(TimeSpan timeout)
  {
    return _workAvailable.Wait(timeout);
  }

  /// <summary>
  /// Releases all resources used by the scheduler.
  /// </summary>
  public void Dispose()
  {
    _isRunning = false;
    _workAvailable.Set(); // Wake up any waiting workers

    // Wait for all workers to finish
    foreach (var worker in _workers)
    {
      worker.Join();
    }

    _workAvailable.Dispose();
  }

  /// <summary>
  /// Represents a worker thread for the AffinityTaskScheduler.
  /// </summary>
  private class AffinityWorker
  {
    // The parent scheduler this worker belongs to
    private readonly AffinityTaskScheduler _scheduler;

    // Queue of tasks with affinity to this worker
    private readonly ConcurrentQueue<Task> _tasks = new ConcurrentQueue<Task>();

    // Event to signal when new work is available for this worker
    private readonly ManualResetEventSlim _workAvailable = new ManualResetEventSlim(false);

    // The thread associated with this worker
    private Thread _thread;

    // Priority index (lower indices get priority for unaffinitized work)
    private readonly int _priorityIndex;

    // Indicates whether the worker is currently processing tasks
    private bool _isProcessing;

    /// <summary>
    /// Initializes a new instance of the Worker class.
    /// </summary>
    /// <param name="scheduler">The parent scheduler.</param>
    /// <param name="priorityIndex">The priority index of this worker.</param>
    public AffinityWorker(AffinityTaskScheduler scheduler, int priorityIndex)
    {
      _scheduler = scheduler;
      _priorityIndex = priorityIndex;
    }

    /// <summary>
    /// Starts the worker thread.
    /// </summary>
    public void Start()
    {
      _thread = new Thread(WorkerThreadProc)
      {
        IsBackground = true,
        Name = $"AffinityTaskScheduler Worker {_priorityIndex}"
      };
      _thread.Start();
    }

    /// <summary>
    /// Waits for the worker thread to complete.
    /// </summary>
    public void Join()
    {
      _thread?.Join();
    }

    /// <summary>
    /// Enqueues a task to this worker.
    /// </summary>
    /// <param name="task">The task to enqueue.</param>
    public void EnqueueTask(Task task)
    {
      _tasks.Enqueue(task);
      _workAvailable.Set();
    }

    /// <summary>
    /// Gets the tasks scheduled to this worker.
    /// </summary>
    /// <returns>An enumerable of scheduled tasks.</returns>
    public IEnumerable<Task> GetScheduledTasks()
    {
      return _tasks.ToArray();
    }

    /// <summary>
    /// The main worker thread procedure.
    /// </summary>
    private void WorkerThreadProc()
    {
      // Associate this worker with the current thread
      _currentWorker.Value = this;

      try
      {
        while (_scheduler._isRunning)
        {
          // First, try to process tasks with affinity to this worker
          _isProcessing = true;
          bool processedTask = false;

          while (_tasks.TryDequeue(out Task task))
          {
            _scheduler.TryExecuteTask(task);
            processedTask = true;
          }

          if (_tasks.IsEmpty)
          {
            _workAvailable.Reset();
          }

          // If no affinity tasks, look for unaffinitized work based on priority
          if (!processedTask)
          {
            _isProcessing = false;

            // Try to get unaffinitized work (only if we're the highest priority idle worker)
            bool gotUnaffinitizedWork = false;

            // Check if we're the highest priority idle worker
            bool isHighestPriorityIdle = true;
            for (int i = 0; i < _priorityIndex; i++)
            {
              if (!_scheduler._workers[i]._isProcessing)
              {
                isHighestPriorityIdle = false;
                break;
              }
            }

            if (isHighestPriorityIdle)
            {
              gotUnaffinitizedWork = _scheduler.TryExecuteUnaffinitizedTask();
            }

            // If we didn't get any work, wait for new work
            if (!gotUnaffinitizedWork)
            {
              WaitForWork();
            }
          }
        }
      }
      finally
      {
        // Dissociate this worker from the thread
        _currentWorker.Value = null;
      }
    }

    /// <summary>
    /// Waits for work to become available.
    /// </summary>
    private void WaitForWork()
    {
      // Wait on both our own work event and the scheduler's unaffinitized work event
      WaitHandle[] waitHandles = { _workAvailable.WaitHandle, _scheduler._workAvailable.WaitHandle };
      WaitHandle.WaitAny(waitHandles, 100); // Use a timeout to periodically check for shutdown
    }
  }
}