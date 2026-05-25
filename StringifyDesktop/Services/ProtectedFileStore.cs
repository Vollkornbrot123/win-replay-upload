using System.Security.Cryptography;
using System.Text.Json;
using StringifyDesktop.Models;

namespace StringifyDesktop.Services;

public sealed class ProtectedFileStore : IProtectedFileStore
{
    private readonly AppPaths paths;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProtectedFileStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public async Task<OAuthSession?> LoadSessionAsync()
    {
        if (!File.Exists(paths.SessionPath))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(paths.SessionPath);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<OAuthSession>(plain, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveSessionAsync(OAuthSession? session)
    {
        if (session is null)
        {
            if (File.Exists(paths.SessionPath))
            {
                File.Delete(paths.SessionPath);
            }

            return;
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(paths.SessionPath, encrypted);
    }

    public async Task<PendingAuthFlow?> LoadPendingFlowAsync()
    {
        if (!File.Exists(paths.PendingFlowPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(paths.PendingFlowPath);
            return await JsonSerializer.DeserializeAsync<PendingAuthFlow>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SavePendingFlowAsync(PendingAuthFlow? flow)
    {
        if (flow is null)
        {
            if (File.Exists(paths.PendingFlowPath))
            {
                File.Delete(paths.PendingFlowPath);
            }

            return;
        }

        await using var stream = File.Create(paths.PendingFlowPath);
        await JsonSerializer.SerializeAsync(stream, flow, JsonOptions);
    }
}
