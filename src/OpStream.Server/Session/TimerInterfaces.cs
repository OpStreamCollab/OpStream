using System.Threading;

namespace OpStream.Server.Session;

/// <summary>
/// Simple timer factory to allow unit testing and abstraction.
/// </summary>
public interface ITimerFactory
{
    System.Threading.ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period);
}

public class DefaultTimerFactory : ITimerFactory
{
    public System.Threading.ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // En .NET 8+, el scheduler de base tiene una factoría
        // Pero para simplificar y ser compatible con la interfaz esperada:
        return new TimerWrapper(callback, state, dueTime, period);
    }
}

internal class TimerWrapper : System.Threading.ITimer
{
    private readonly Timer _timer;

    public TimerWrapper(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _timer = new Timer(callback, state, dueTime, period);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);

    public void Dispose() => _timer.Dispose();

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
