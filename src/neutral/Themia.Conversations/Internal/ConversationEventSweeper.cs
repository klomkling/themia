using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Conversations.Internal;

/// <summary>
/// Marks <c>conversation_events.delivered_at</c> once every recipient in an event's payload is covered for its <c>Seq</c>: the recipient is inactive, or the message is
/// redacted, or <c>max(notified_seq, last_read_seq) &gt;= Seq</c>. Left, erased and redacted recipients therefore never keep an event undelivered or a conversation rescanned.
/// </summary>
internal sealed class ConversationEventSweeper(IConversationDialect dialect, ILogger logger)
{
    private const int EventsPerConversation = 500;

    public async Task SweepAsync(DbConnection connection, DateTimeOffset now, DateTimeOffset cutoff, int conversations, CancellationToken cancellationToken)
    {
        var candidates = (await connection.QueryAsync<Candidate>(new CommandDefinition(
            "SELECT tenant_id AS TenantId, conversation_id AS ConversationId FROM conversation_events " +
            "WHERE kind = 0 AND delivered_at IS NULL AND created_at <= @Cutoff GROUP BY tenant_id, conversation_id ORDER BY MIN(created_at) " +
            $"{dialect.LimitClause};",
            new { Cutoff = cutoff.UtcDateTime, Limit = conversations },
            cancellationToken: cancellationToken))).ToList();

        var marked = 0;
        foreach (var candidate in candidates)
        {
            marked += await SweepConversationAsync(connection, candidate, now, cancellationToken);
        }

        logger.LogDebug("Conversation event sweep checked {Conversations} conversations and marked {Events} events delivered", candidates.Count, marked);
    }

    private async Task<int> SweepConversationAsync(DbConnection connection, Candidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var parameters = new { candidate.TenantId, candidate.ConversationId };
        var events = (await connection.QueryAsync<EventRow>(new CommandDefinition(
            "SELECT id AS Id, payload AS Payload FROM conversation_events WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND kind = 0 AND delivered_at IS NULL ORDER BY created_at " +
            $"{dialect.LimitClause};",
            new { candidate.TenantId, candidate.ConversationId, Limit = EventsPerConversation },
            cancellationToken: cancellationToken))).ToList();
        if (events.Count == 0)
        {
            return 0;
        }

        var coverage = (await connection.QueryAsync<Coverage>(new CommandDefinition(
            "SELECT p.id AS ParticipantId, CASE WHEN p.left_at IS NULL THEN 1 ELSE 0 END AS Active, " +
            "CASE WHEN n.notified_seq IS NOT NULL AND n.notified_seq > p.last_read_seq THEN n.notified_seq ELSE p.last_read_seq END AS Reached " +
            "FROM conversation_participants p LEFT JOIN conversation_notify_cursors n ON n.tenant_id = p.tenant_id AND n.conversation_id = p.conversation_id AND n.participant_id = p.id " +
            "WHERE p.tenant_id = @TenantId AND p.conversation_id = @ConversationId;",
            parameters,
            cancellationToken: cancellationToken))).ToDictionary(c => c.ParticipantId);
        var redacted = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT seq FROM conversation_messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND redacted_at IS NOT NULL;",
            parameters,
            cancellationToken: cancellationToken))).ToHashSet();

        var covered = events.Where(e => IsCovered(e, coverage, redacted)).Select(e => e.Id).ToList();
        if (covered.Count == 0)
        {
            return 0;
        }

        // One parameter per id: a list parameter is expanded by Dapper on some drivers and sent as an array on others.
        var ids = new DynamicParameters(new { Now = now.UtcDateTime, candidate.TenantId, candidate.ConversationId });
        for (var i = 0; i < covered.Count; i++)
        {
            ids.Add($"Id{i}", covered[i]);
        }

        var names = string.Join(", ", Enumerable.Range(0, covered.Count).Select(i => $"@Id{i}"));
        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE conversation_events SET delivered_at = @Now WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND delivered_at IS NULL AND id IN ({names});",
            ids,
            cancellationToken: cancellationToken));
        return covered.Count;
    }

    private static bool IsCovered(EventRow row, Dictionary<Guid, Coverage> coverage, HashSet<long> redacted)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(row.Payload);
        }
        catch (JsonException)
        {
            return true; // Not a message event the drain can act on: nothing to notify, so it must not be rescanned forever.
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("seq", out var seqElement) || !seqElement.TryGetInt64(out var seq)
                || !root.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            if (redacted.Contains(seq))
            {
                return true;
            }

            foreach (var recipient in recipients.EnumerateArray())
            {
                // A recipient with no participant row any more (purged) is inactive, so it is covered.
                if (recipient.TryGetGuid(out var id) && coverage.TryGetValue(id, out var state) && state.Active != 0 && state.Reached < seq)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class Candidate
    {
        public string TenantId { get; init; } = string.Empty;

        public Guid ConversationId { get; init; }
    }

    private sealed class EventRow
    {
        public Guid Id { get; init; }

        public string Payload { get; init; } = string.Empty;
    }

    private sealed class Coverage
    {
        public Guid ParticipantId { get; init; }

        public int Active { get; init; }

        public long Reached { get; init; }
    }
}
