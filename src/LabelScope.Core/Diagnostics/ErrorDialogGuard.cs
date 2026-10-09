namespace LabelScope.Core.Diagnostics;

/// <summary>
/// Decides when the window-thread error handler should stop showing "still running" dialogs: after several errors
/// in a short time the program is in a loop and must close with a readable message, and while one error dialog is
/// open no second one may be stacked on top of it.
/// </summary>
public sealed class ErrorDialogGuard
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _clock;
    private readonly Queue<DateTime> _recent = new();
    private readonly object _lock = new();
    private bool _dialogOpen;

    /// <summary>Creates a guard.</summary>
    /// <param name="limit">Number of errors inside <paramref name="window"/> that counts as a loop.</param>
    /// <param name="window">Length of the observation window (default 30 seconds).</param>
    /// <param name="clock">Time source, replaceable so tests do not have to wait; defaults to UTC now.</param>
    public ErrorDialogGuard(int limit = 3, TimeSpan? window = null, Func<DateTime>? clock = null)
    {
        _limit = limit;
        _window = window ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Records one error.</summary>
    /// <returns>True when the limit is reached within the window: the program should close.</returns>
    public bool RecordAndCheckLoop()
    {
        lock (_lock)
        {
            var now = _clock();
            _recent.Enqueue(now);
            while (_recent.Count > 0 && now - _recent.Peek() > _window) _recent.Dequeue();
            return _recent.Count >= _limit;
        }
    }

    /// <summary>Tries to claim the right to show an error dialog.</summary>
    /// <returns>False when another error dialog is already open; the caller then only writes the log.</returns>
    public bool TryEnterDialog()
    {
        lock (_lock)
        {
            if (_dialogOpen) return false;
            _dialogOpen = true;
            return true;
        }
    }

    /// <summary>Releases the claim made by <see cref="TryEnterDialog"/> after the dialog was closed.</summary>
    public void ExitDialog()
    {
        lock (_lock) _dialogOpen = false;
    }
}
