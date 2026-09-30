using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record DiskReconnectAcknowledgement(string RunId, string CaseId, string Instance, string VolumeId);

public static class DiskRemovalHandshake
{
    public const string CaseId = "disk-orderly-eject-reconnect";
    public static void Validate(DiskReconnectAcknowledgement acknowledgement, string runId, DiskTarget target, string caseId = CaseId)
    {
        if (acknowledgement.RunId != runId || acknowledgement.CaseId != caseId ||
            !string.Equals(acknowledgement.Instance, target.Instance, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acknowledgement.VolumeId, target.VolumeId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Reconnect acknowledgement has a stale run/case or a different disk/volume identity.");
    }
}
