namespace tot_lib
{
    public class ContextWorker<T> : Worker<ContextWorker<T>> where T : class
    {
        protected T _context;

        public ContextWorker(T context, CancellationToken cancellationToken) : base(cancellationToken)
        {
            _context = context;
        }

        public async Task BeginInvoke(Action<T> action)
        {
            await BeginInvoke(action, CancellationToken.None);
        }

        public async Task BeginInvoke(Action<T> action, CancellationToken token)
        {
            await Task.Factory.StartNew(() => action(_context), token, TaskCreationOptions.None, this).WaitAsync(token);
        }

        public Task<R> BeginInvoke<R>(Func<T, R> func)
        {
            return BeginInvoke(func, CancellationToken.None);
        }

        public Task<R> BeginInvoke<R>(Func<T, R> func, CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(() => func(_context), cancellationToken, TaskCreationOptions.None, this);
        }

        public void Invoke(Action<T> action)
        {
            Invoke(action, CancellationToken.None);
        }

        public void Invoke(Action<T> action, CancellationToken token)
        {
            BeginInvoke(action, token).Wait();
        }

        public R Invoke<R>(Func<T, R> func, CancellationToken token)
        {
            var task = BeginInvoke(func, token);
            task.Wait(token);
            return task.Result;
        }

        public R Invoke<R>(Func<T, R> func)
        {
            return Invoke<R>(func, CancellationToken.None);
        }

        protected virtual Task Initialize(T context)
        {
            return Task.CompletedTask;
        }

        protected override async void ThreadLoop()
        {
            await Initialize(_context);
            base.ThreadLoop();
        }
    }
}