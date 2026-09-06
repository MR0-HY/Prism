using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Ports;

/// <summary>Read-only model interpretation. Its reply is tentative data, not a proposal or an input capability.</summary>
public interface ITaskIntentProvider
{
    Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct);
}
