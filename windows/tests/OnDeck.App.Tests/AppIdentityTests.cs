namespace OnDeck.App.Tests;

public class AppIdentityTests
{
    [Fact]
    public void TheAssemblyIsNamedOnDeck()
    {
        // An unpackaged toast takes its header from the exe. Nothing else in the suite can see
        // that header, so this assertion stands in for it: rename the assembly and every toast
        // silently starts announcing itself as something else.
        // PORT_PLAN.md Decision 4: display name onDeck, onDeck.exe.
        var name = typeof(OnDeck.App.App).Assembly.GetName().Name;

        Assert.Equal("onDeck", name);
    }
}
