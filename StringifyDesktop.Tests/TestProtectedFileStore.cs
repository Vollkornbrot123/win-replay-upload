using StringifyDesktop.Models;
using StringifyDesktop.Services;

namespace StringifyDesktop.Tests;

internal sealed class TestProtectedFileStore : IProtectedFileStore
{
    private OAuthSession? session;
    private PendingAuthFlow? pendingFlow;

    public Task<OAuthSession?> LoadSessionAsync()
    {
        return Task.FromResult(session);
    }

    public Task SaveSessionAsync(OAuthSession? value)
    {
        session = value;
        return Task.CompletedTask;
    }

    public Task<PendingAuthFlow?> LoadPendingFlowAsync()
    {
        return Task.FromResult(pendingFlow);
    }

    public Task SavePendingFlowAsync(PendingAuthFlow? flow)
    {
        pendingFlow = flow;
        return Task.CompletedTask;
    }
}

