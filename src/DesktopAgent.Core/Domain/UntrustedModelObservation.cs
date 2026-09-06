namespace DesktopAgent.Core.Domain;

/// <summary>A model's short account of an earlier frame, never independent evidence or input authority.
/// Current is a reported observation; Next is only the model's proposed next stage, not a completed action.</summary>
public sealed record UntrustedModelObservation(Lease Lease, string FrameId, string Current, string Next)
{
    public const int MaximumEntries = 12;
    public const int PreservedInitialEntries = 6;
    public string Source => "model_current_next_untrusted";
    public bool Verified => false;
    public bool InputAuthority => false;
    public override string ToString() => $"UntrustedModelObservation({Lease}, frame={FrameId}, text omitted)";
}
