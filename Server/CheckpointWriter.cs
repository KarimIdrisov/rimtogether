namespace RimTogether;

public sealed class CheckpointWriter : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly Action<Checkpoint> write;
    private readonly Action<string> log;
    private readonly Task worker;
    private Checkpoint? pending;
    private Exception? lastError;
    private bool stopping;

    public CheckpointWriter(Action<Checkpoint> write, Action<string> log)
    {
        this.write = write; this.log = log;
        worker = Task.Run(Work);
    }
    public void Submit(Checkpoint checkpoint)
    {
        lock (gate)
        {
            if (stopping) throw new InvalidOperationException("Checkpoint writer is stopping");
            pending = checkpoint; // Latest pending checkpoint wins; at most one write plus one pending snapshot.
        }
        signal.Set();
    }
    private void Work()
    {
        while (true)
        {
            Checkpoint? next; bool done;
            lock (gate) { next = pending; pending = null; done = stopping && next == null; }
            if (done) return;
            if (next == null) { signal.WaitOne(); continue; }
            try { write(next); lastError = null; }
            catch (Exception error) { lastError = error; log("CHECKPOINT ERROR: " + error.Message); }
        }
    }
    public void Complete()
    {
        lock (gate) stopping = true;
        signal.Set(); worker.GetAwaiter().GetResult();
        if (lastError != null) throw new IOException("Последняя запись checkpoint не завершилась. Предыдущие файлы сохранены.", lastError);
    }
    public void Dispose() { try { Complete(); } finally { signal.Dispose(); } }
}
