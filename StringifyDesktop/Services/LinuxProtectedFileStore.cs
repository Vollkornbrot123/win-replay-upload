using System.Text.Json;
using KeySharp;
using StringifyDesktop.Models;

namespace StringifyDesktop.Services;

public sealed class LinuxProtectedFileStore : IProtectedFileStore
{
    private const string AppId = "com.stringify.desktop.app";
    private const string ServiceName = "StringifyDesktop";
    private const string SessionUser = "oauth-session";
    private const string PendingFlowUser = "pending-auth-flow";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<OAuthSession?> LoadSessionAsync()
        => LoadAsync<OAuthSession>(SessionUser);

    public Task SaveSessionAsync(OAuthSession? session)
        => SaveAsync(SessionUser, session);

    public Task<PendingAuthFlow?> LoadPendingFlowAsync()
        => LoadAsync<PendingAuthFlow>(PendingFlowUser);

    public Task SavePendingFlowAsync(PendingAuthFlow? flow)
        => SaveAsync(PendingFlowUser, flow);

    private Task<T?> LoadAsync<T>(string user)
    {
        try
        {
            var payload = Keyring.GetPassword(AppId, ServiceName, user);

            if (string.IsNullOrWhiteSpace(payload))
                return Task.FromResult<T?>(default);

            var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            return Task.FromResult(result);
        }
        catch (KeyringException)
        {
            return Task.FromResult<T?>(default);
        }
        catch (JsonException)
        {
            return Task.FromResult<T?>(default);
        }
    }

    private Task SaveAsync<T>(string user, T? value)
    {
        try
        {
            var payload = JsonSerializer.Serialize(value, JsonOptions);
            Keyring.SetPassword(AppId, ServiceName, user, payload);
            return Task.CompletedTask;
        }
        catch (KeyringException)
        {
            return Task.CompletedTask;
        }
    }
}