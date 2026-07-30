using Lisscribe.Core.Abstractions;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakeSessionMonitor : ISessionMonitor
{
    public event EventHandler? SessionLocked;
    public event EventHandler? SystemSuspending;
    public event EventHandler? ForegroundWindowChanged;

    public bool IsStarted { get; private set; }

    public void Start() => IsStarted = true;

    public void RaiseSessionLocked() => SessionLocked?.Invoke(this, EventArgs.Empty);
    public void RaiseSystemSuspending() => SystemSuspending?.Invoke(this, EventArgs.Empty);
    public void RaiseForegroundWindowChanged() => ForegroundWindowChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() { }
}
