using System;
using System.Net.Sockets;

namespace PixelWizard.AvaloniaClient.Services;

/// <summary>Maps low-level exceptions to clear, actionable user messages.</summary>
public static class FriendlyError
{
    public static string Describe(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "No host is accepting connections at that address. Make sure the host has started sharing.",
            SocketError.TimedOut          => "The connection timed out. Check the address and that both machines are on the same network.",
            SocketError.HostNotFound      => "That address could not be found. Check the IP address or router address.",
            SocketError.HostUnreachable   => "The host could not be reached. Check your network connection.",
            SocketError.NetworkUnreachable=> "The network is unreachable. Check your connection.",
            _ => "Could not connect to the host. Check the address and try again."
        },
        TimeoutException        => "The operation timed out. Please try again.",
        UriFormatException      => "That address is not valid.",
        FormatException         => "That address or code is not in a valid format.",
        System.Net.Http.HttpRequestException => "Could not reach the router server. Check the router address.",
        OperationCanceledException => "The operation was cancelled.",
        _ => "Something went wrong. Please check your settings and try again."
    };
}
