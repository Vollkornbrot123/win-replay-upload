using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StringifyDesktop.Models;

namespace StringifyDesktop.Services;

public sealed class AuthService : IAccessTokenSource
{
    private const int CodeMaxAgeMinutes = 10;
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);
    private readonly AppConfiguration configuration;
    private readonly IProtectedFileStore protectedFileStore;
    private readonly SystemClock clock;
    private readonly HttpClient httpClient;
    private OAuthSession? currentSession;

    public AuthService(
        AppConfiguration configuration,
        IProtectedFileStore protectedFileStore,
        SystemClock clock,
        HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        this.protectedFileStore = protectedFileStore;
        this.clock = clock;
        this.httpClient = httpClient ?? new HttpClient();
    }

    public event EventHandler? SessionChanged;

    public event EventHandler? CallbackStateChanged;

    public OAuthSession? Session => currentSession;

    public bool IsProcessingCallback { get; private set; }

    public string? CallbackMessage { get; private set; }

    public string? CallbackError { get; private set; }

    public async Task InitializeAsync()
    {
        currentSession = await protectedFileStore.LoadSessionAsync();
        if (currentSession is not null && NeedsRefresh(currentSession))
        {
            currentSession = await TryRefreshAsync(currentSession);
            await protectedFileStore.SaveSessionAsync(currentSession);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartBrowserSignInAsync()
    {
        var flow = new PendingAuthFlow(
            State: RandomBase64Url(24),
            Nonce: RandomBase64Url(24),
            CodeVerifier: RandomBase64Url(32),
            CreatedAt: clock.UtcNow);

        await protectedFileStore.SavePendingFlowAsync(flow);

        var authorizeUrl = await BuildAuthorizeUrlAsync(flow);
        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        CallbackMessage = "Waiting for browser sign-in to complete...";
        CallbackError = null;
        CallbackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task HandleLaunchArgumentsAsync(string[] args)
    {
        foreach (var arg in args)
        {
            if (!Uri.TryCreate(arg, UriKind.Absolute, out var uri))
            {
                continue;
            }

            await HandleCallbackUriAsync(uri);
        }
    }

    public async Task HandleCallbackUriAsync(Uri uri)
    {
        var expected = new Uri(configuration.OAuthCallbackUri);
        if (!string.Equals(uri.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, expected.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IsProcessingCallback = true;
        CallbackMessage = "Completing browser sign-in...";
        CallbackError = null;
        CallbackStateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var session = await ExchangeCallbackAsync(uri);
            currentSession = session;
            await protectedFileStore.SaveSessionAsync(currentSession);
            CallbackMessage = null;
            CallbackError = null;
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            CallbackError = error.Message;
        }
        finally
        {
            IsProcessingCallback = false;
            CallbackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        if (currentSession is null)
        {
            return null;
        }

        if (!NeedsRefresh(currentSession))
        {
            return currentSession.AccessToken;
        }

        currentSession = await TryRefreshAsync(currentSession);
        await protectedFileStore.SaveSessionAsync(currentSession);
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return currentSession?.AccessToken;
    }

    public async Task SignOutAsync()
    {
        currentSession = null;
        CallbackMessage = null;
        CallbackError = null;
        await protectedFileStore.SavePendingFlowAsync(null);
        await protectedFileStore.SaveSessionAsync(null);
        SessionChanged?.Invoke(this, EventArgs.Empty);
        CallbackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> BuildAuthorizeUrlAsync(PendingAuthFlow flow)
    {
        var challenge = await CreateCodeChallengeAsync(flow.CodeVerifier);
        var issuer = configuration.OAuthIssuer.TrimEnd('/');
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = configuration.OAuthClientId,
            ["redirect_uri"] = configuration.OAuthCallbackUri,
            ["scope"] = configuration.OAuthScopes,
            ["state"] = flow.State,
            ["nonce"] = flow.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        var query = string.Join("&", parameters.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
        return $"{issuer}/oauth/authorize?{query}";
    }

    private async Task<OAuthSession> ExchangeCallbackAsync(Uri callbackUri)
    {
        var pending = await protectedFileStore.LoadPendingFlowAsync();
        if (pending is null)
        {
            throw new InvalidOperationException("No pending sign-in request was found on this device.");
        }

        if (clock.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(CodeMaxAgeMinutes))
        {
            await protectedFileStore.SavePendingFlowAsync(null);
            throw new InvalidOperationException("The sign-in callback expired. Please try again.");
        }

        var query = ParseQuery(callbackUri.Query);
        if (query.TryGetValue("error", out var oauthError) && !string.IsNullOrWhiteSpace(oauthError))
        {
            throw new InvalidOperationException(query.GetValueOrDefault("error_description") ?? oauthError);
        }

        var code = query.GetValueOrDefault("code");
        var state = query.GetValueOrDefault("state");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new InvalidOperationException("The callback did not include an authorization code.");
        }

        if (!string.Equals(state, pending.State, StringComparison.Ordinal))
        {
            await protectedFileStore.SavePendingFlowAsync(null);
            throw new InvalidOperationException("The sign-in callback state did not match this device.");
        }

        var token = await RequestTokenAsync(new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = configuration.OAuthClientId,
            ["redirect_uri"] = configuration.OAuthCallbackUri,
            ["code"] = code,
            ["code_verifier"] = pending.CodeVerifier
        });

        var user = await TryFetchUserInfoAsync(token.AccessToken)
            ?? ParseUserFromJwt(token.IdToken)
            ?? throw new InvalidOperationException("Authentication succeeded, but user information was unavailable.");

        await protectedFileStore.SavePendingFlowAsync(null);
        return BuildSession(token, user, currentSession);
    }

    private async Task<OAuthSession?> TryRefreshAsync(OAuthSession previous)
    {
        if (string.IsNullOrWhiteSpace(previous.RefreshToken))
        {
            return null;
        }

        try
        {
            var token = await RequestTokenAsync(new Dictionary<string, string?>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = configuration.OAuthClientId,
                ["refresh_token"] = previous.RefreshToken
            });

            return BuildSession(token, previous.User, previous);
        }
        catch
        {
            return null;
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(IReadOnlyDictionary<string, string?> values)
    {
        var issuer = configuration.OAuthIssuer.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{issuer}/oauth/token");
        request.Content = new FormUrlEncodedContent(values
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!));

        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token exchange failed: {await ReadErrorAsync(response)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<TokenResponse>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            ?? throw new InvalidOperationException("Token exchange returned an empty payload.");
    }

    private async Task<DesktopAuthUser?> TryFetchUserInfoAsync(string accessToken)
    {
        var issuer = configuration.OAuthIssuer.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{issuer}/oauth/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<UserInfoResponse>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return payload is null || string.IsNullOrWhiteSpace(payload.Sub)
            ? null
            : new DesktopAuthUser(
                payload.Sub,
                payload.Email,
                payload.EmailVerified,
                payload.Name,
                payload.PreferredUsername,
                payload.Picture);
    }

    private static OAuthSession BuildSession(TokenResponse token, DesktopAuthUser user, OAuthSession? previous)
    {
        return new OAuthSession(
            token.AccessToken,
            token.RefreshToken ?? previous?.RefreshToken,
            token.IdToken ?? previous?.IdToken,
            token.TokenType,
            token.Scope ?? previous?.Scope ?? "profile email",
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresIn, 1)),
            user);
    }

    private static bool NeedsRefresh(OAuthSession session)
    {
        return session.ExpiresAt <= DateTimeOffset.UtcNow.Add(RefreshSkew);
    }

    private static Dictionary<string, string?> ParseQuery(string query)
    {
        var normalized = query.StartsWith('?') ? query[1..] : query;
        return normalized
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static parts => Uri.UnescapeDataString(parts[0]),
                static parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null,
                StringComparer.Ordinal);
    }

    public static string RandomBase64Url(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return ToBase64Url(bytes);
    }

    public static async Task<string> CreateCodeChallengeAsync(string verifier)
    {
        await Task.Yield();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return ToBase64Url(digest);
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }

    private static DesktopAuthUser? ParseUserFromJwt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var jwt = JsonSerializer.Deserialize<JwtPayload>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return jwt is null || string.IsNullOrWhiteSpace(jwt.Sub)
                ? null
                : new DesktopAuthUser(jwt.Sub, jwt.Email, jwt.EmailVerified, jwt.Name, jwt.PreferredUsername, jwt.Picture);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var payload = await JsonSerializer.DeserializeAsync<ErrorPayload>(stream);
            return payload?.ErrorDescription ?? payload?.Message ?? payload?.Error ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch
        {
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = "Bearer";

        public string? Scope { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class UserInfoResponse
    {
        public string Sub { get; set; } = string.Empty;

        public string? Email { get; set; }

        [JsonPropertyName("email_verified")]
        public bool? EmailVerified { get; set; }

        public string? Name { get; set; }

        [JsonPropertyName("preferred_username")]
        public string? PreferredUsername { get; set; }

        public string? Picture { get; set; }
    }

    private sealed class JwtPayload
    {
        public string Sub { get; set; } = string.Empty;

        public string? Email { get; set; }

        [JsonPropertyName("email_verified")]
        public bool? EmailVerified { get; set; }

        public string? Name { get; set; }

        [JsonPropertyName("preferred_username")]
        public string? PreferredUsername { get; set; }

        public string? Picture { get; set; }
    }

    private sealed class ErrorPayload
    {
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }

        public string? Message { get; set; }
    }
}
