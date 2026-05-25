using StringifyDesktop.Models;

namespace StringifyDesktop.Services;

public interface IProtectedFileStore
{
    Task<OAuthSession?> LoadSessionAsync();

    Task SaveSessionAsync(OAuthSession? session);

    Task<PendingAuthFlow?> LoadPendingFlowAsync();

    Task SavePendingFlowAsync(PendingAuthFlow? flow);
}
