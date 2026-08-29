using System;

namespace PixelWizard.Transport
{
    /// <summary>
    /// A <c>MessageReceived</c> subscriber has thrown on <see cref="ConsecutiveFailures"/>
    /// messages in a row. One handler failure is reported via <c>HandlerError</c> and the loop
    /// continues, because a bug in dispatch/rendering/injection doesn't mean the connection is
    /// bad. But a handler failing on every single frame is a stuck session wearing a working
    /// connection as a disguise -- continuing to receive and re-fail forever just burns CPU and
    /// floods <c>HandlerError</c>, so past this threshold it's escalated to a transport-level
    /// <c>Error</c> and the connection is closed like any other unrecoverable failure.
    /// </summary>
    public sealed class RepeatedHandlerFailureException : Exception
    {
        public int ConsecutiveFailures { get; }

        public RepeatedHandlerFailureException(int consecutiveFailures, Exception lastFailure, string message)
            : base(message, lastFailure)
        {
            ConsecutiveFailures = consecutiveFailures;
        }
    }
}
