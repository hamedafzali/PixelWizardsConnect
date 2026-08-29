using System.Collections.Generic;
using PixelWizard.Core.Protocol;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Exhaustive classification of every MessageType into MessagePlane. Media is exactly the
/// frame-carrying types (droppable, becomes a WebRTC track in Phase 4); everything else is
/// Control (reliable/ordered). New MessageType values default to Control here unless added
/// to the Media set explicitly -- a silent miscategorization would route media traffic onto
/// the reliable path (harmless but defeats the point) rather than the reverse.
/// </summary>
public class MessagePlaneTests
{
    private static readonly HashSet<MessageType> MediaTypes = new()
    {
        MessageType.ScreenDelta,
        MessageType.FullScreen,
        MessageType.StreamFrame
    };

    public static IEnumerable<object[]> AllMessageTypes()
    {
        foreach (MessageType type in System.Enum.GetValues(typeof(MessageType)))
            yield return new object[] { type };
    }

    [Theory]
    [MemberData(nameof(AllMessageTypes))]
    public void EveryMessageType_ClassifiesAsExpectedPlane(MessageType type)
    {
        var expected = MediaTypes.Contains(type) ? MessagePlane.Media : MessagePlane.Control;
        Assert.Equal(expected, type.GetPlane());
    }

    [Fact]
    public void GetPlane_IsPure_SameTypeAlwaysSamePlane()
    {
        Assert.Equal(MessageType.ScreenDelta.GetPlane(), MessageType.ScreenDelta.GetPlane());
        Assert.Equal(MessageType.Handshake.GetPlane(), MessageType.Handshake.GetPlane());
    }
}
