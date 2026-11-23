using Matchmaking.Contracts;
using Matchmaking.Stats.Data;
using MassTransit;

namespace Matchmaking.Stats.Consumers;

/// <summary>Açılan maçları gün bazında sayar.</summary>
public class MatchCreatedConsumer(StatsDbContext db) : IConsumer<MatchCreated>
{
    public async Task Consume(ConsumeContext<MatchCreated> context)
    {
        var dayKey = DateOnly.FromDateTime(context.Message.CreatedAt);
        var day = await db.Daily.FindAsync([dayKey], context.CancellationToken);
        if (day is null)
        {
            day = new DailyActivity { Day = dayKey };
            db.Daily.Add(day);
        }

        day.MatchesCreated++;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
