using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class LoginNavigationMachineTests
{
    [Fact]
    public async Task SecondCaller_JoinsExistingLogin()
    {
        var machine = new LoginNavigationMachine();
        var first = machine.BeginOrJoin();
        var generation = machine.Generation;
        var second = machine.BeginOrJoin();

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(generation, machine.Generation);

        machine.Complete(true);
        Assert.True(await first.Task);
        Assert.True(await second.Task);
    }

    [Fact]
    public async Task Cancel_CompletesEveryWaiter()
    {
        var machine = new LoginNavigationMachine();
        var first = machine.BeginOrJoin();
        var second = machine.BeginOrJoin();
        machine.Cancel();
        Assert.False(await first.Task);
        Assert.False(await second.Task);
        Assert.False(machine.IsActive);
    }

    [Fact]
    public void OldGeneration_CannotCompleteNewerLogin()
    {
        var machine = new LoginNavigationMachine();
        machine.BeginOrJoin();
        var oldGeneration = machine.Generation;
        machine.Cancel();
        machine.BeginOrJoin();

        Assert.False(machine.AcceptsProbe(oldGeneration, "https://chatgpt.com/", true));
        Assert.True(machine.AcceptsProbe(machine.Generation, "https://chatgpt.com/", true));
    }

    [Fact]
    public void LeavingChatGpt_StopsProbe()
    {
        var machine = new LoginNavigationMachine();
        machine.BeginOrJoin();
        Assert.False(machine.ShouldStopProbe("https://chatgpt.com/"));
        Assert.True(machine.ShouldStopProbe("https://accounts.google.com/o/oauth2/auth"));
        Assert.Equal(LoginNavigationAction.StayOnExternalAuth, machine.Observe("https://accounts.google.com/o/oauth2/auth", true));
        Assert.False(machine.MayNavigateAwayToProbe());
    }
}
