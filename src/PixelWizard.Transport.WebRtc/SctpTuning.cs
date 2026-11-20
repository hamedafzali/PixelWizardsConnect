using System.Reflection;
using SIPSorcery.Net;

namespace PixelWizard.Transport.WebRtc
{
    /// <summary>
    /// Works around the pacing of SIPSorcery's SCTP sender (verified against SIPSorcery 10.0.16).
    /// Stock behaviour: the congestion window starts at 4380 bytes and never grows on a clean link
    /// (CalculateCongestionWindow only grows it while cwnd &lt; outstanding bytes), at most 4 chunks leave per
    /// burst and bursts are 50 ms apart. That caps a loopback/LAN data channel at about 0.3 MB/s.
    /// Raising the window and shortening the burst period on both ends gives about 3.3 MB/s; either alone
    /// does not help.
    ///
    /// The fields are private/internal members of SctpDataSender, reached by reflection, so a SIPSorcery
    /// upgrade may rename them. Failure is never fatal: the transport works at stock speed and
    /// <see cref="TryApply"/> returns false (the tests assert true, so an upgrade breaks CI loudly).
    /// </summary>
    internal static class SctpTuning
    {
        internal const int BurstPeriodMilliseconds = 1;
        internal const uint CongestionWindowBytes = 200_000;
        internal const uint SlowStartThresholdBytes = 262_144;

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        internal static bool TryApply(RTCPeerConnection pc)
        {
            try
            {
                var association = pc.sctp?.RTCSctpAssociation;
                if (association == null) return false;
                var sender = typeof(SctpAssociation).GetField("_dataSender", Instance)?.GetValue(association);
                if (sender == null) return false;
                var t = sender.GetType();
                var burst = t.GetField("_burstPeriodMilliseconds", Instance);
                var cwnd = t.GetField("_congestionWindow", Instance);
                var ssthresh = t.GetField("_slowStartThreshold", Instance);
                if (burst?.FieldType != typeof(int) || cwnd?.FieldType != typeof(uint) || ssthresh?.FieldType != typeof(uint))
                    return false;
                burst.SetValue(sender, BurstPeriodMilliseconds);
                cwnd.SetValue(sender, CongestionWindowBytes);
                ssthresh.SetValue(sender, SlowStartThresholdBytes);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
