using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Casco.Api.Domain;
using Casco.Api.Features.Ai;
using Casco.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Agent;

/// <param name="Part">Part of a big build that is being written (1 for normal requests).</param>
public record TaskStreamEvent(string Type, string? Text = null, int? Attempt = null, string? Status = null, int? Part = null);

/// <summary>Live model output of one agent task, fanned out to any number of browser subscribers.</summary>
public class TaskStream(Guid userId) : IAiStream
{
    private const int MaxChars = 400_000;
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _thought = new();
    private readonly List<Channel<TaskStreamEvent>> _subscribers = [];

    public Guid UserId { get; } = userId;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public int Attempt { get; private set; }
    public int Part { get; private set; } = 1;
    public string? FinalStatus { get; private set; }

    public void BeginAttempt(int attempt, int part = 1)
    {
        lock (_gate)
        {
            Attempt = attempt;
            Part = part;
            _text.Clear();
            _thought.Clear();
            Broadcast(new TaskStreamEvent("attempt", Attempt: attempt, Part: part));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _text.Clear();
            _thought.Clear();
            Broadcast(new TaskStreamEvent("reset"));
        }
    }

    public void Thought(string text)
    {
        lock (_gate)
        {
            if (_thought.Length + text.Length > MaxChars) return;
            _thought.Append(text);
            Broadcast(new TaskStreamEvent("thinking", text));
        }
    }

    public void Append(string text)
    {
        lock (_gate)
        {
            if (_text.Length + text.Length > MaxChars) return;
            _text.Append(text);
            Broadcast(new TaskStreamEvent("delta", text));
        }
    }

    public void Complete(string status)
    {
        lock (_gate)
        {
            FinalStatus = status;
            Broadcast(new TaskStreamEvent("done", Status: status));
            foreach (var s in _subscribers) s.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    /// <summary>Current text plus a channel for everything that follows (null when the task already finished).</summary>
    public (TaskStreamEvent Snapshot, string Thought, Channel<TaskStreamEvent>? Channel) Subscribe()
    {
        lock (_gate)
        {
            var snapshot = new TaskStreamEvent("snapshot", _text.ToString(), Attempt, FinalStatus, Part);
            var thought = _thought.ToString();
            if (FinalStatus is not null) return (snapshot, thought, null);
            var channel = Channel.CreateUnbounded<TaskStreamEvent>(new UnboundedChannelOptions { SingleReader = true });
            _subscribers.Add(channel);
            return (snapshot, thought, channel);
        }
    }

    public void Unsubscribe(Channel<TaskStreamEvent> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
    }

    private void Broadcast(TaskStreamEvent e)
    {
        foreach (var s in _subscribers) s.Writer.TryWrite(e);
    }
}

/// <summary>In-process registry of live task streams (single API server, so no external pub/sub is needed).</summary>
public class TaskStreamHub
{
    private readonly ConcurrentDictionary<Guid, TaskStream> _streams = new();

    public TaskStream GetOrCreate(Guid taskId, Guid userId)
    {
        foreach (var (id, s) in _streams)
            if (s.CreatedAt < DateTime.UtcNow.AddHours(-1)) _streams.TryRemove(id, out _);
        return _streams.GetOrAdd(taskId, _ => new TaskStream(userId));
    }

    public TaskStream? Get(Guid taskId) => _streams.GetValueOrDefault(taskId);

    public void Complete(Guid taskId, string status)
    {
        if (!_streams.TryGetValue(taskId, out var stream)) return;
        stream.Complete(status);
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => _streams.TryRemove(taskId, out TaskStream? _), TaskScheduler.Default);
    }
}

public static class TaskStreamEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void MapTaskStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tasks/{id:guid}/stream", async (Guid id, HttpContext http, AppDbContext db, TaskStreamHub hub) =>
        {
            var ct = http.RequestAborted;
            var userId = http.User.UserId();
            var status = await db.AgentTasks.Where(t => t.Id == id && t.UserId == userId).Select(t => t.Status).FirstOrDefaultAsync(ct)
                         ?? throw ApiException.NotFound("المهمة غير موجودة");

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache, no-transform";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            async Task Send(TaskStreamEvent e)
            {
                await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(e, Json) + "\n\n", ct);
            }

            var stream = hub.Get(id);
            if (stream is null && status is TaskStatuses.Succeeded or TaskStatuses.Failed)
            {
                await Send(new TaskStreamEvent("done", Status: status));
                return;
            }
            stream ??= hub.GetOrCreate(id, userId);

            var (snapshot, thought, channel) = stream.Subscribe();
            await Send(snapshot);
            if (thought.Length > 0) await Send(new TaskStreamEvent("thinking", thought));
            await http.Response.Body.FlushAsync(ct);
            if (channel is null) return;

            try
            {
                var pending = new StringBuilder();
                var thoughts = new StringBuilder();
                while (!ct.IsCancellationRequested)
                {
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        wait.CancelAfter(TimeSpan.FromSeconds(15));
                        try
                        {
                            if (!await channel.Reader.WaitToReadAsync(wait.Token)) break;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Keep proxies (Cloudflare, Caddy) from closing an idle connection.
                            await http.Response.WriteAsync(": ping\n\n", ct);
                            await http.Response.Body.FlushAsync(ct);
                            continue;
                        }
                    }

                    // Coalesce token-sized deltas into one message per flush.
                    while (channel.Reader.TryRead(out var e))
                    {
                        if (e.Type == "thinking") { thoughts.Append(e.Text); continue; }
                        if (e.Type == "delta") { pending.Append(e.Text); continue; }
                        if (thoughts.Length > 0) { await Send(new TaskStreamEvent("thinking", thoughts.ToString())); thoughts.Clear(); }
                        if (pending.Length > 0) { await Send(new TaskStreamEvent("delta", pending.ToString())); pending.Clear(); }
                        await Send(e);
                    }
                    if (thoughts.Length > 0) { await Send(new TaskStreamEvent("thinking", thoughts.ToString())); thoughts.Clear(); }
                    if (pending.Length > 0) { await Send(new TaskStreamEvent("delta", pending.ToString())); pending.Clear(); }
                    await http.Response.Body.FlushAsync(ct);
                    await Task.Delay(60, ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { stream.Unsubscribe(channel); }
        }).RequireAuthorization();
    }
}
