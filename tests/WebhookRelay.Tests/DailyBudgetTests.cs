using WebhookRelay.Api.Data;
using WebhookRelay.Api.Options;
using WebhookRelay.Api.Services;

namespace WebhookRelay.Tests;

public class DailyBudgetTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    private DailyBudget CreateBudget(RelayDbContext db, int limit)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RelayOptions { DailyCallBudget = limit });
        return new DailyBudget(db, options, _clock);
    }

    [Fact]
    public async Task TrySpend_AllowsCallsUpToTheLimit_ThenRefuses()
    {
        using RelayDbContext db = _database.CreateContext();
        DailyBudget budget = CreateBudget(db, limit: 3);

        Assert.True(await budget.TrySpendAsync(CancellationToken.None));
        Assert.True(await budget.TrySpendAsync(CancellationToken.None));
        Assert.True(await budget.TrySpendAsync(CancellationToken.None));
        Assert.False(await budget.TrySpendAsync(CancellationToken.None));

        Assert.Equal(3, await budget.GetUsedTodayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TrySpend_StartsFreshOnTheNextDay()
    {
        using RelayDbContext db = _database.CreateContext();
        DailyBudget budget = CreateBudget(db, limit: 1);

        Assert.True(await budget.TrySpendAsync(CancellationToken.None));
        Assert.False(await budget.TrySpendAsync(CancellationToken.None));

        _clock.Advance(TimeSpan.FromDays(1));

        Assert.True(await budget.TrySpendAsync(CancellationToken.None));
        Assert.Equal(1, await budget.GetUsedTodayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Count_SurvivesARestart()
    {
        // A new context and a new DailyBudget object stand in for the app restarting.
        // The count has to come back from the database.
        using (RelayDbContext firstRun = _database.CreateContext())
        {
            DailyBudget budget = CreateBudget(firstRun, limit: 2);
            Assert.True(await budget.TrySpendAsync(CancellationToken.None));
            Assert.True(await budget.TrySpendAsync(CancellationToken.None));
        }

        using RelayDbContext secondRun = _database.CreateContext();
        DailyBudget afterRestart = CreateBudget(secondRun, limit: 2);

        Assert.Equal(2, await afterRestart.GetUsedTodayAsync(CancellationToken.None));
        Assert.False(await afterRestart.TrySpendAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUsedToday_IsZero_BeforeAnyCalls()
    {
        using RelayDbContext db = _database.CreateContext();
        DailyBudget budget = CreateBudget(db, limit: 5);

        Assert.Equal(0, await budget.GetUsedTodayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TrySpend_RefusesEverything_WhenTheLimitIsZero()
    {
        using RelayDbContext db = _database.CreateContext();
        DailyBudget budget = CreateBudget(db, limit: 0);

        Assert.False(await budget.TrySpendAsync(CancellationToken.None));
    }
}
