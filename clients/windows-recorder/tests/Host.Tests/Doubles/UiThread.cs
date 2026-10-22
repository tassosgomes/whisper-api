using System.Collections.Concurrent;

namespace Whisper.Recorder.Host.Tests.Doubles;

/// <summary>
/// Thread de "interface" para os testes: uma fila única com um <see cref="SynchronizationContext"/> próprio, como o
/// dispatcher do WPF. Os ViewModels construídos nela capturam esse contexto e as atualizações viram <c>Post</c>.
/// </summary>
public sealed class UiThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    private readonly Thread thread;
    private readonly UiContext context;
    private long posted;

    public UiThread()
    {
        context = new UiContext(this);
        thread = new Thread(Loop) { IsBackground = true, Name = "Teste: thread da interface" };
        thread.Start();
    }

    /// <summary>Quantos itens já foram enfileirados (inclui os <c>Post</c> dos ViewModels).</summary>
    public long Posted => Interlocked.Read(ref posted);

    private void Loop()
    {
        SynchronizationContext.SetSynchronizationContext(context);
        foreach (var (callback, state) in queue.GetConsumingEnumerable())
            callback(state);
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try { source.SetResult(function()); }
            catch (Exception ex) { source.SetException(ex); }
        }, null);
        return source.Task;
    }

    /// <summary>Roda na thread da interface; as continuações do <c>await</c> voltam para ela.</summary>
    public Task InvokeAsync(Func<Task> function)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(async _ =>
        {
            try
            {
                await function();
                source.SetResult();
            }
            catch (Exception ex) { source.SetException(ex); }
        }, null);
        return source.Task;
    }

    public void Post(Action action) => context.Post(_ => action(), null);

    public void Dispose()
    {
        queue.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class UiContext(UiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref owner.posted);
            try { owner.queue.Add((d, state)); }
            catch (InvalidOperationException) { }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == owner.thread)
                d(state);
            else
            {
                using var done = new ManualResetEventSlim();
                Post(_ =>
                {
                    d(state);
                    done.Set();
                }, null);
                done.Wait();
            }
        }

        public override SynchronizationContext CreateCopy() => new UiContext(owner);
    }
}
