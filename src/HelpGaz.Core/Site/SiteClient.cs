using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HelpGaz.Core.Diagnostics;

namespace HelpGaz.Core.Site;

public sealed record SiteTemplate(int Id, string DisplayName, string FileName);
public sealed record Contract(int Id, string Number, string Address, string Customer, string Company, string Date = "");
public sealed record LoginResult(string Token, string Email);

public sealed class SiteAuthException(string message) : Exception(message);
public sealed class SiteException(string message) : Exception(message);

/// <summary>Клиент API сайта. Запросы идут строго по одному: сервер слабый, а скан может быть большим.</summary>
public sealed class SiteClient : IDisposable
{
    /// <summary>Адрес сайта можно подменить переменной окружения для проверки на тестовом сервере.</summary>
    public static string DefaultBaseUrl =>
        Environment.GetEnvironmentVariable("HELPGAZ_SITE") ?? "https://xn--b1afkbeyaaikdhdeg.xn--p1ai";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SiteClient(string? baseUrl = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl ?? DefaultBaseUrl), Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpGaz");
    }

    public string? Token
    {
        get => _http.DefaultRequestHeaders.Authorization?.Parameter;
        set => _http.DefaultRequestHeaders.Authorization = value is null ? null : new AuthenticationHeaderValue("Bearer", value);
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            using var resp = await Logged("POST /api/auth/login", () => _http.PostAsJsonAsync("/api/auth/login", new { email, password }, ct));
            AppLog.Info($"POST /api/auth/login → {(int)resp.StatusCode} за {watch.ElapsedMilliseconds} мс");
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SiteAuthException(await ReadMessage(resp, "Неверный email или пароль."));
            if (!resp.IsSuccessStatusCode)
                throw new SiteException($"Сайт ответил ошибкой {(int)resp.StatusCode} при входе.");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.GetProperty("token").GetString() ?? throw new SiteException("Сайт не вернул токен.");
            Token = token;
            return new LoginResult(token, email);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SiteTemplate>> GetTemplatesAsync(CancellationToken ct = default) =>
        ParseTemplates(await GetBytesAsync("/api/templates", ct));

    public async Task<IReadOnlyList<SiteTemplate>> GetCustomItdTemplatesAsync(CancellationToken ct = default) =>
        ParseTemplates(await GetBytesAsync("/api/templates/itd", ct));

    public Task<byte[]> DownloadTemplateAsync(int templateId, CancellationToken ct = default) =>
        GetBytesAsync($"/api/templates/{templateId}/download", ct);

    public Task<byte[]> GenerateDocumentAsync(string urlPrefix, int contractId, CancellationToken ct = default) =>
        GetBytesAsync($"{urlPrefix}/{contractId}", ct);

    public async Task<IReadOnlyList<Contract>> GetContractsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await GetBytesAsync("/contracts", ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            AppLog.Error($"/contracts: ожидался массив, пришло {doc.RootElement.ValueKind}");
            throw new SiteException("Сайт вернул список договоров в неожиданном виде.");
        }

        var list = new List<Contract>();
        var skipped = 0;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (!int.TryParse(Text(c, "id"), out var id)) { skipped++; continue; }
            list.Add(new Contract(id, Text(c, "contractNumber"), Text(c, "objectName"), Text(c, "fio"), Text(c, "namecompany"), Text(c, "date")));
        }

        var first = doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default;
        var idKind = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var idValue) ? idValue.ValueKind.ToString() : "нет";
        AppLog.Info(list.Count == 0
            ? $"Договоров с сайта: 0 (элементов в ответе {doc.RootElement.GetArrayLength()}, без id {skipped})"
            : $"Договоров с сайта: {list.Count}, id от {list.Min(c => c.Id)} до {list.Max(c => c.Id)}; пропущено без id: {skipped}; тип id: {idKind}");
        return list;
    }

    private static IReadOnlyList<SiteTemplate> ParseTemplates(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SiteTemplate>();
        foreach (var t in doc.RootElement.EnumerateArray())
            if (int.TryParse(Text(t, "id"), out var id))
                list.Add(new SiteTemplate(id, Text(t, "displayName"), Text(t, "fileName")));
        return list;
    }

    private static string Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    private async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            using var resp = await Logged("GET " + path, () => _http.GetAsync(path, ct));
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                AppLog.Warn($"GET {path} → 401 за {watch.ElapsedMilliseconds} мс");
                throw new SiteAuthException("Сессия на сайте истекла, войдите заново.");
            }
            if (!resp.IsSuccessStatusCode)
            {
                var message = await ReadMessage(resp, $"Сайт ответил ошибкой {(int)resp.StatusCode}.");
                AppLog.Warn($"GET {path} → {(int)resp.StatusCode} за {watch.ElapsedMilliseconds} мс: {message}");
                throw new SiteException(message);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            AppLog.Info($"GET {path} → {(int)resp.StatusCode}, {bytes.Length} байт за {watch.ElapsedMilliseconds} мс");
            return bytes;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Сетевые сбои и таймауты попадают в лог до того, как уйдут выше.</summary>
    private static async Task<HttpResponseMessage> Logged(string request, Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            AppLog.Error($"{request}: нет ответа", e);
            throw;
        }
    }

    private static async Task<string> ReadMessage(HttpResponseMessage resp, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } s)
                return s;
        }
        catch (JsonException) { }
        return fallback;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
