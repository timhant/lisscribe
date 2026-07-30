namespace Lisscribe.Core.Session;

public enum SessionState
{
    Idle,
    Recording,
    Recognizing,
    Failed,
    Paused,
}

public sealed class SessionStatusEventArgs(SessionState state, string? message) : EventArgs
{
    public SessionState State { get; } = state;
    public string? Message { get; } = message;
}
