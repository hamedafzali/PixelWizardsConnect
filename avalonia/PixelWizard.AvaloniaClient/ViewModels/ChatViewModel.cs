using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using PixelWizard.Core.Models;

namespace PixelWizard.AvaloniaClient.ViewModels;

/// <summary>
/// Where chat and clipboard currently go: the connected viewer session or the approved
/// host session. <see cref="IsHost"/> labels this side's own messages.
/// </summary>
public sealed record ChatLink(bool IsHost, Func<string, Task> SendChat, Func<string, Task> SendClipboard);

/// <summary>
/// In-session chat (messages, input, panel visibility, unread count) and clipboard send,
/// shared by both roles. Nothing is sent while <c>activeLink</c> returns null.
/// </summary>
public sealed class ChatViewModel : ReactiveObject
{
    private readonly Func<ChatLink?>        _activeLink;
    private readonly Func<Task<string?>>    _readClipboard;

    public ChatViewModel(Func<ChatLink?> activeLink, Func<Task<string?>> readClipboard)
    {
        _activeLink    = activeLink;
        _readClipboard = readClipboard;

        ToggleCommand        = ReactiveCommand.Create(() => { Visible = !Visible; });
        SendCommand          = ReactiveCommand.Create(Send);
        SendClipboardCommand = ReactiveCommand.CreateFromTask(SendClipboardAsync);
    }

    public ObservableCollection<ChatEntry> Messages { get; } = new();

    private bool _visible;
    public bool Visible
    {
        get => _visible;
        set
        {
            this.RaiseAndSetIfChanged(ref _visible, value);
            if (value) UnreadCount = 0;
        }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _unreadCount, value);
            this.RaisePropertyChanged(nameof(HasUnread));
        }
    }
    public bool HasUnread => _unreadCount > 0;

    private string _input = "";
    public string Input { get => _input; set => this.RaiseAndSetIfChanged(ref _input, value); }

    public ReactiveCommand<Unit, Unit> ToggleCommand        { get; }
    public ReactiveCommand<Unit, Unit> SendCommand          { get; }
    public ReactiveCommand<Unit, Unit> SendClipboardCommand { get; }

    public void Send()
    {
        string text = Input.Trim();
        if (string.IsNullOrEmpty(text)) return;
        Input = "";

        var link = _activeLink();
        Messages.Add(new ChatEntry(DateTime.Now, link?.IsHost == true, text));
        if (link != null) _ = link.SendChat(text);
    }

    public void Receive(bool isFromHost, string text)
    {
        Messages.Add(new ChatEntry(DateTime.Now, isFromHost, text));
        if (!Visible) UnreadCount++;
    }

    public async Task SendClipboardAsync()
    {
        string? text = await _readClipboard();
        if (string.IsNullOrEmpty(text)) return;

        var link = _activeLink();
        if (link != null) await link.SendClipboard(text);
    }

    /// <summary>Session ended: clears the log and closes the panel.</summary>
    public void Reset()
    {
        Messages.Clear();
        Visible     = false;
        UnreadCount = 0;
    }
}
