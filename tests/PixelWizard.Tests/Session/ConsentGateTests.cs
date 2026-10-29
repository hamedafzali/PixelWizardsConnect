using System;
using System.Threading.Tasks;
using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// Consent must fail closed: only an explicit Allow lets a viewer in.
/// </summary>
public class ConsentGateTests
{
    [Fact]
    public async Task NoDecider_Denies() =>
        Assert.False(await ConsentGate.DecideAsync(null, "viewer"));

    [Fact]
    public async Task ExplicitDeny_Denies() =>
        Assert.False(await ConsentGate.DecideAsync(_ => Task.FromResult(false), "viewer"));

    [Fact]
    public async Task ExplicitAllow_Allows() =>
        Assert.True(await ConsentGate.DecideAsync(_ => Task.FromResult(true), "viewer"));

    [Fact]
    public async Task DeciderThrowsSynchronously_Denies() =>
        Assert.False(await ConsentGate.DecideAsync(_ => throw new InvalidOperationException("dialog failed"), "viewer"));

    [Fact]
    public async Task DeciderFaults_Denies() =>
        Assert.False(await ConsentGate.DecideAsync(
            _ => Task.FromException<bool>(new InvalidOperationException("dialog failed")), "viewer"));

    [Fact]
    public async Task DeciderCancelled_Denies() =>
        Assert.False(await ConsentGate.DecideAsync(_ => Task.FromCanceled<bool>(new System.Threading.CancellationToken(true)), "viewer"));

    [Fact]
    public async Task DeciderReturnsNullTask_Denies() =>
        Assert.False(await ConsentGate.DecideAsync(_ => null!, "viewer"));

    [Fact]
    public async Task PassesViewerToDecider()
    {
        string? seen = null;
        await ConsentGate.DecideAsync(v => { seen = v; return Task.FromResult(true); }, "incoming viewer");
        Assert.Equal("incoming viewer", seen);
    }
}
