using System;
using System.Collections.Generic;
using PixelWizard.Core.Protocol;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Exhaustive classification of every MessageType into ViewerDispatchAction/HostDispatchAction.
/// This is the pure part of MainViewModel.OnViewerMessage/OnHostMessage's dispatch switches --
/// STATUS_REPORT.md's lowest-scored (1/5) extraction target -- pulled out so it is testable
/// without a socket or a UI dispatcher, per docs/PHASE2-PLAN.md's T1. Any new MessageType
/// defaults to Ignored on both sides unless added here explicitly, matching the same
/// fail-safe default MessagePlaneTests uses for MessagePlane.
/// </summary>
public class MessageDispatchTests
{
    private static readonly Dictionary<MessageType, ViewerDispatchAction> ExpectedViewerAction = new()
    {
        [MessageType.FullScreen] = ViewerDispatchAction.ApplyFullScreen,
        [MessageType.ScreenDelta] = ViewerDispatchAction.ApplyScreenDelta,
        [MessageType.HelloAck] = ViewerDispatchAction.HostHelloAck,
        [MessageType.HelloRejected] = ViewerDispatchAction.HostHelloRejected,
        [MessageType.HandshakeOk] = ViewerDispatchAction.HandshakeAcknowledged,
        [MessageType.HandshakeFailed] = ViewerDispatchAction.HandshakeRejected,
        [MessageType.Pong] = ViewerDispatchAction.LatencyPong,
        [MessageType.ClipboardText] = ViewerDispatchAction.Clipboard,
        [MessageType.ChatMessage] = ViewerDispatchAction.Chat,
    };

    private static readonly Dictionary<MessageType, HostDispatchAction> ExpectedHostAction = new()
    {
        [MessageType.MouseMove] = HostDispatchAction.MouseMove,
        [MessageType.MouseClick] = HostDispatchAction.MouseClick,
        [MessageType.MouseButtonDown] = HostDispatchAction.MouseButtonDown,
        [MessageType.MouseButtonUp] = HostDispatchAction.MouseButtonUp,
        [MessageType.KeyPress] = HostDispatchAction.KeyPress,
        [MessageType.KeyRelease] = HostDispatchAction.KeyRelease,
        [MessageType.Ping] = HostDispatchAction.PingReply,
        [MessageType.QualityPreset] = HostDispatchAction.QualityChanged,
        [MessageType.ClipboardText] = HostDispatchAction.Clipboard,
        [MessageType.ChatMessage] = HostDispatchAction.Chat,
    };

    public static IEnumerable<object[]> AllMessageTypes()
    {
        foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
            yield return new object[] { type };
    }

    [Theory]
    [MemberData(nameof(AllMessageTypes))]
    public void ClassifyForViewer_EveryMessageType_MatchesExpectedActionOrIgnored(MessageType type)
    {
        var expected = ExpectedViewerAction.TryGetValue(type, out var action)
            ? action
            : ViewerDispatchAction.Ignored;

        Assert.Equal(expected, MessageDispatch.ClassifyForViewer(type));
    }

    [Theory]
    [MemberData(nameof(AllMessageTypes))]
    public void ClassifyForHost_EveryMessageType_MatchesExpectedActionOrIgnored(MessageType type)
    {
        var expected = ExpectedHostAction.TryGetValue(type, out var action)
            ? action
            : HostDispatchAction.Ignored;

        Assert.Equal(expected, MessageDispatch.ClassifyForHost(type));
    }

    [Fact]
    public void ClassifyForViewer_IsPure_SameTypeAlwaysSameAction()
    {
        Assert.Equal(
            MessageDispatch.ClassifyForViewer(MessageType.ScreenDelta),
            MessageDispatch.ClassifyForViewer(MessageType.ScreenDelta));
    }

    [Fact]
    public void ClassifyForHost_IsPure_SameTypeAlwaysSameAction()
    {
        Assert.Equal(
            MessageDispatch.ClassifyForHost(MessageType.MouseMove),
            MessageDispatch.ClassifyForHost(MessageType.MouseMove));
    }
}
