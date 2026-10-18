using System.Collections.Concurrent;

namespace tot_lib
{
    public class Worker<C> : TaskScheduler where C : Worker<C>
    {
        protected static Dictionary<Type, C> _instances = new Dictionary<Type, C>();
        protected readonly CancellationToken _cancellationToken;
        protected bool _isRunning;
        protected BlockingCollection<Task> _threadQueue = new BlockingCollection<Task>();

        public Worker(CancellationToken cancellationToken)
        {
            if (!_instances.ContainsKey(typeof(C)))
            {
                _cancellationToken = cancellationToken;
                _instances[typeof(C)] = (C)this;
            }
            else
                throw new InvalidOperationException("Can't start two Discord Workers");
        }

        public static C Instance
        {
            get
            {
                if (_instances.TryGetValue(typeof(C), out C? instance))
                    return instance;
                throw new InvalidOperationException("Instance not found");
            }
        }

        public Task BeginInvoke(Action action)
        {
            return BeginInvoke(action, CancellationToken.None);
        }

        public Task<R> BeginInvoke<R>(Func<R> func)
        {
            return BeginInvoke(func, CancellationToken.None);
        }

        public Task<R> BeginInvoke<R>(Func<R> func, CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(func, cancellationToken, TaskCreationOptions.None, this);
        }

        public Task BeginInvoke(Action action, CancellationToken token)
        {
            return Task.Factory.StartNew(action, token, TaskCreationOptions.None, this);
        }

        public void Invoke(Action action)
        {
            Invoke(action, CancellationToken.None);
        }

        public void Invoke(Action action, CancellationToken token)
        {
            BeginInvoke(action, token).Wait(token);
        }

        public R Invoke<R>(Func<R> func, CancellationToken token)
        {
            var task = BeginInvoke(func, token);
            task.Wait(token);
            return task.Result;
        }

        public R Invoke<R>(Func<R> func)
        {
            return Invoke<R>(func, CancellationToken.None);
        }

        public void Start()
        {
            new Thread(ThreadLoop) { Name = typeof(C).Name + " Thread" }.Start();
        }

        public void Stop()
        {
            _threadQueue.CompleteAdding();
            _instances.Remove(typeof(C));
        }

        protected override IEnumerable<Task>? GetScheduledTasks()
        {
            return null;
        }

        protected override void QueueTask(Task task)
        {
            try
            {
                _threadQueue.Add(task, _cancellationToken);
            }
            catch (OperationCanceledException) { }
        }

        protected virtual void ThreadLoop()
        {
            _isRunning = true;
            try
            {
                foreach (var task in _threadQueue.GetConsumingEnumerable(_cancellationToken))
                {
                    TryExecuteTask(task);
                }
            }
            catch (OperationCanceledException)
            { }
            finally
            {
                _isRunning = false;
            }
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            if (taskWasPreviouslyQueued) return false;
            return _isRunning && TryExecuteTask(task);
        }
    }
}