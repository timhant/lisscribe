using Lisscribe.Core.Abstractions;

namespace Lisscribe.Core.Tests.Fakes;

public sealed class FakeTextInjector : ITextInjector
{
    public List<(string Text, nint Window)> Injections { get; } = [];

    public InjectResult NextResult { get; set; } = InjectResult.Ok;

    public Task<InjectResult> InjectAsync(
        string text, nint targetWindow, CancellationToken cancellationToken)
    {
        Injections.Add((text, targetWindow));
        return Task.FromResult(NextResult);
    }

    public string? LastText => Injections.Count == 0 ? null : Injections[^1].Text;
}
