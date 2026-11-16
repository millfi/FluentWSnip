using WSnip.Windows;
using Xunit;

namespace WSnip.Rendering.Tests;

public sealed class InstanceIsolationTests
{
    [Fact]
    public void OriginalAndFluentInstancesCanBothClaimButDuplicateFluentCannot()
    {
        string suffix = ".IsolationTest." + Guid.NewGuid().ToString("N");
        using var original = new WindowsSingleInstanceService("LightStudio.WSnip" + suffix);
        using var fluent = new WindowsSingleInstanceService(WindowsPlatform.AppId + suffix);
        using var secondFluent = new WindowsSingleInstanceService(WindowsPlatform.AppId + suffix);

        Assert.True(original.TryClaim());
        Assert.True(fluent.TryClaim());
        Assert.False(secondFluent.TryClaim());
    }

    [Fact]
    public async Task FluentActivationGoesToFluentWithoutReachingOriginal()
    {
        string suffix = ".IsolationTest." + Guid.NewGuid().ToString("N");
        using var original = new WindowsSingleInstanceService("LightStudio.WSnip" + suffix);
        using var fluent = new WindowsSingleInstanceService(WindowsPlatform.AppId + suffix);
        using var secondFluent = new WindowsSingleInstanceService(WindowsPlatform.AppId + suffix);
        var originalReceived = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fluentReceived = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(original.TryClaim());
        Assert.True(fluent.TryClaim());
        original.ArgumentsReceived += (_, args) => originalReceived.TrySetResult(args);
        fluent.ArgumentsReceived += (_, args) => fluentReceived.TrySetResult(args);
        original.StartListening();
        fluent.StartListening();

        Assert.True(await secondFluent.ForwardAsync(["--snip", "rectangle"]));
        Assert.Equal(new[] { "--snip", "rectangle" }, await fluentReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(originalReceived.Task.IsCompleted);
    }
}
