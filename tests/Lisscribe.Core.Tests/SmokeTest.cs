using Xunit;

namespace Lisscribe.Core.Tests;

public class SmokeTest
{
    [Fact]
    public void CoreAssemblyIsReferencable()
    {
        var asm = typeof(Lisscribe.Core.AssemblyMarker).Assembly;
        Assert.Equal("Lisscribe.Core", asm.GetName().Name);
    }
}
