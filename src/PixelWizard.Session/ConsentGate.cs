using System;
using System.Threading.Tasks;

namespace PixelWizard.Session;

/// <summary>
/// The host's consent decision for one verified viewer. Fails closed: no decider wired up,
/// a decider that throws or faults, or anything other than an explicit <c>true</c> denies.
/// The decider itself (the Avalonia dialog) stays in the app; only this policy lives here,
/// where it can be tested without UI.
/// </summary>
public static class ConsentGate
{
    public static async Task<bool> DecideAsync(Func<string, Task<bool>>? decider, string viewer)
    {
        if (decider == null) return false;
        try
        {
            return await decider(viewer);
        }
        catch
        {
            return false;
        }
    }
}
