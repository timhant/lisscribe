namespace Lisscribe.Core.Abstractions;

public interface IAutoStart
{
    bool IsEnabled { get; }

    void Enable(string executablePath);

    void Disable();
}
