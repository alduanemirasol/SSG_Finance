using System.Collections.Concurrent;

namespace MyMvcApp.Services;

public class SseService
{
    private readonly ConcurrentDictionary<string, HttpResponse> _clients = new();

    public string Subscribe(HttpResponse response)
    {
        var id = Guid.NewGuid().ToString("N");
        _clients[id] = response;
        return id;
    }

    public void Unsubscribe(string id) => _clients.TryRemove(id, out _);

    public async Task BroadcastAsync(string eventType)
    {
        var message = $"event: {eventType}\ndata: {{}}\n\n";
        var dead = new List<string>();

        foreach (var (id, response) in _clients)
        {
            try
            {
                await response.WriteAsync(message);
                await response.Body.FlushAsync();
            }
            catch
            {
                dead.Add(id);
            }
        }

        foreach (var id in dead)
            _clients.TryRemove(id, out _);
    }
}
