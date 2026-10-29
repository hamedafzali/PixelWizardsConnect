using System.Reactive.Linq;
using PixelWizard.AvaloniaClient.ViewModels;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

public class ChatViewModelTests
{
    private sealed class FakeLink
    {
        public readonly List<string> Chat = new();
        public readonly List<string> Clipboard = new();
        public ChatLink Link(bool isHost) => new(isHost,
            t => { Chat.Add(t); return Task.CompletedTask; },
            t => { Clipboard.Add(t); return Task.CompletedTask; });
    }

    private static ChatViewModel Vm(Func<ChatLink?> link, string? clipboard = null) =>
        new(link, () => Task.FromResult(clipboard));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Send_TrimsAddsAndSends_LabelledByRole(bool isHost)
    {
        var fake = new FakeLink();
        var vm = Vm(() => fake.Link(isHost));
        vm.Input = "  hello  ";

        vm.Send();

        Assert.Equal(new[] { "hello" }, fake.Chat);
        var entry = Assert.Single(vm.Messages);
        Assert.Equal("hello", entry.Text);
        Assert.Equal(isHost, entry.IsHost);
        Assert.Equal("", vm.Input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Send_Blank_DoesNothing(string input)
    {
        var fake = new FakeLink();
        var vm = Vm(() => fake.Link(true));
        vm.Input = input;

        vm.Send();

        Assert.Empty(fake.Chat);
        Assert.Empty(vm.Messages);
        Assert.Equal(input, vm.Input);
    }

    [Fact]
    public void Send_NoActiveLink_SendsNothing_ButKeepsTheLine()
    {
        var vm = Vm(() => null);
        vm.Input = "hi";

        vm.Send();

        var entry = Assert.Single(vm.Messages);
        Assert.False(entry.IsHost);
    }

    [Fact]
    public void Receive_WhileHidden_CountsUnread_OpeningClearsIt()
    {
        var vm = Vm(() => null);
        vm.Receive(isFromHost: true, "a");
        vm.Receive(isFromHost: true, "b");

        Assert.Equal(2, vm.UnreadCount);
        Assert.True(vm.HasUnread);

        vm.Visible = true;

        Assert.Equal(0, vm.UnreadCount);
        Assert.False(vm.HasUnread);
        Assert.Equal(new[] { "a", "b" }, vm.Messages.Select(m => m.Text));
    }

    [Fact]
    public void Receive_WhileVisible_DoesNotCountUnread()
    {
        var vm = Vm(() => null);
        vm.Visible = true;

        vm.Receive(isFromHost: false, "x");

        Assert.Equal(0, vm.UnreadCount);
        Assert.False(Assert.Single(vm.Messages).IsHost);
    }

    [Fact]
    public async Task ToggleCommand_FlipsVisibility()
    {
        var vm = Vm(() => null);
        await vm.ToggleCommand.Execute();
        Assert.True(vm.Visible);
        await vm.ToggleCommand.Execute();
        Assert.False(vm.Visible);
    }

    [Fact]
    public async Task SendCommand_Sends()
    {
        var fake = new FakeLink();
        var vm = Vm(() => fake.Link(false));
        vm.Input = "via command";

        await vm.SendCommand.Execute();

        Assert.Equal(new[] { "via command" }, fake.Chat);
    }

    [Fact]
    public async Task SendClipboard_SendsClipboardText()
    {
        var fake = new FakeLink();
        var vm = Vm(() => fake.Link(true), clipboard: "copied");

        await vm.SendClipboardCommand.Execute();

        Assert.Equal(new[] { "copied" }, fake.Clipboard);
        Assert.Empty(fake.Chat);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SendClipboard_EmptyClipboard_SendsNothing(string? clipboard)
    {
        var fake = new FakeLink();
        var vm = Vm(() => fake.Link(true), clipboard);

        await vm.SendClipboardAsync();

        Assert.Empty(fake.Clipboard);
    }

    [Fact]
    public void Reset_ClearsLogHidesPanelAndUnread()
    {
        var vm = Vm(() => null);
        vm.Receive(true, "a");
        vm.Visible = true;
        vm.Receive(true, "b");

        vm.Reset();

        Assert.Empty(vm.Messages);
        Assert.False(vm.Visible);
        Assert.Equal(0, vm.UnreadCount);
    }
}
