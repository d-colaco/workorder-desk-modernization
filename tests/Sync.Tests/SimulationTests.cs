namespace Sync.Tests;

/// <summary>The simulator's rules (src/Lab.Driver/Simulation.cs) against the rules the stored procedures and the planner
/// API enforce. No containers: it checks that `make simulate` cannot choose a step the system would refuse, except in the
/// close/edit race, which is meant to be refused sometimes.</summary>
public class SimulationTests
{
    /// <summary>Plays one order from creation to close, applying every legality rule of V003 and WorkOrderCommands.</summary>
    private static (int Steps, int Races) Play(int seed)
    {
        var rnd = new Random(seed);
        var qty = rnd.Next(4, 101) * 5;
        var o = new SimOrder { Qty = qty, FloorQty = qty, DueDate = DateTime.Today.AddDays(14) };
        var steps = 0; var races = 0;
        var clock = new DateTime(2026, 10, 17, 10, 0, 0);
        while (!o.Closed)
        {
            Assert.True(++steps < 400, $"seed {seed}: the order never closes");
            clock = clock.AddSeconds(rnd.Next(1, 20));
            var step = Script.Next(o, rnd);
            switch (step)
            {
                case Step.Release:
                    Assert.False(o.Released);                                   // 50012
                    Script.DidRelease(o, Enumerable.Range(1, rnd.Next(2, 6)).Select(i => i * 10).ToArray());
                    break;
                case Step.StartOp:
                    Assert.True(o.Released && o.Started == o.Finished && o.Started < o.Ops.Length);
                    var (si, start, hours) = Script.PlanStart(o, clock, rnd);
                    Assert.True(start == clock && hours > 0);
                    Script.DidStart(o, si, start, hours);
                    break;
                case Step.FinishOp:
                    Assert.True(o.Started == o.Finished + 1);                   // only an operation that was started
                    clock = clock.AddSeconds(rnd.Next(1, 40));
                    var (fi, fs, fe, fh, cost) = Script.PlanFinish(o, clock, rnd);
                    Assert.True(fe > fs && fe <= clock && fh > 0 && cost > 0);   // never ends before it starts or after "now"
                    Script.DidFinish(o);
                    break;
                case Step.Scrap:
                    var n = Script.ScrapAmount(o, rnd);
                    Assert.True(o.Released && n >= 1);                          // 50032
                    Assert.True(o.Scrapped + n <= o.FloorQty);                  // 50033, with margin for plan changes
                    Script.DidScrap(o, n);
                    break;
                case Step.PlanChange:
                    var q = Script.NewQty(o, rnd);
                    Assert.True(q > o.Scrapped && q >= 10);                     // "below what is already scrapped"
                    Assert.True(Script.NewDue(o, rnd) > DateTime.Today);
                    Script.DidPlan(o, q, Script.NewDue(o, rnd));
                    Assert.True(o.Scrapped < o.FloorQty);                       // later scrap still has room
                    break;
                case Step.Race:
                    races++;
                    goto case Step.Close;
                case Step.Close:
                    Assert.True(o.Released && o.Finished == o.Ops.Length);      // 50042, 50043
                    Script.DidClose(o);
                    break;
            }
        }
        return (steps, races);
    }

    [Fact]
    public void Every_step_chosen_is_one_the_procedures_and_the_api_accept()
    {
        for (var seed = 1; seed <= 300; seed++) Play(seed);
    }

    [Fact]
    public void Scrap_and_plan_changes_on_an_order_that_stays_open_never_break_the_quantity_rules()
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var rnd = new Random(seed);
            var o = new SimOrder { Qty = 20, FloorQty = 20, DueDate = DateTime.Today.AddDays(10) };
            Script.DidRelease(o, [10, 20]);
            for (var i = 0; i < 3000; i++)
            {
                if (rnd.NextDouble() < 0.5 && o.Scrapped < Script.ScrapCap(o))
                {
                    var n = Script.ScrapAmount(o, rnd);
                    Assert.True(n >= 1 && o.Scrapped + n <= o.Qty, $"seed {seed}: scrap {n} on top of {o.Scrapped} of {o.Qty}");
                    Script.DidScrap(o, n);
                }
                else
                {
                    var q = Script.NewQty(o, rnd);
                    Assert.True(q > o.Scrapped, $"seed {seed}: new quantity {q} is not above the {o.Scrapped} already scrapped");
                    Script.DidPlan(o, q, Script.NewDue(o, rnd));
                }
            }
        }
    }

    [Fact]
    public void A_fixed_share_of_closes_race_a_planner_edit()
    {
        var races = Enumerable.Range(1, 1000).Sum(s => Play(s).Races);
        Assert.InRange(races / 1000.0, Script.RaceShare - 0.05, Script.RaceShare + 0.05);
    }

    [Fact]
    public void A_closed_order_takes_no_more_steps()
    {
        var o = new SimOrder { Qty = 100, FloorQty = 100, DueDate = DateTime.Today.AddDays(5), Closed = true };
        Assert.Throws<InvalidOperationException>(() => Script.Next(o, new Random(1)));
    }

    [Fact]
    public void The_same_seed_plays_the_same_order()
    {
        Assert.Equal(Play(42), Play(42));
    }
}
