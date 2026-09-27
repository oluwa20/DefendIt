using System.Text.Json;
using DefendIt.Models;
using Microsoft.JSInterop;

namespace DefendIt.Services;

/// <summary>Saves sessions in the browser's localStorage. The server never stores them.</summary>
public class SessionStore(IJSRuntime js)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<bool> SaveAsync(DefenseSession s) =>
        await js.InvokeAsync<bool>("defendit.saveSession", s.Id, JsonSerializer.Serialize(s, Web));

    public async Task<DefenseSession?> GetAsync(string id)
    {
        var json = await js.InvokeAsync<string?>("defendit.getSession", id);
        return Parse(json);
    }

    public async Task<List<DefenseSession>> ListAsync()
    {
        var all = await js.InvokeAsync<string[]>("defendit.listSessions");
        return all.Select(Parse).OfType<DefenseSession>().OrderByDescending(s => s.CreatedUtc).ToList();
    }

    public async Task DeleteAsync(string id) => await js.InvokeAsync<bool>("defendit.deleteSession", id);
    public async Task DeleteAllAsync() => await js.InvokeAsync<bool>("defendit.deleteAll");

    private static DefenseSession? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<DefenseSession>(json, Web); }
        catch (JsonException) { return null; }
    }
}
