// The rules of `simulate`, with no I/O so a test can check that every step it can choose is one the stored procedures
// and the planner API accept. Program.cs does the calls and reports back what succeeded.

public enum Step { Release, StartOp, FinishOp, Scrap, PlanChange, Close, Race }

public sealed class SimOrder
{
    public int LegacyId;
    public Guid NewId;
    public int Qty, FloorQty, Scrapped;   // FloorQty: the smallest quantity ever set; scrap is capped against it
    public DateTime DueDate;
    public int[] Ops = [];
    public double[] Hours = [];
    public DateTime[] StartedAt = [];
    public int Started, Finished;         // operations started / finished, always in sequence
    public bool Released, Closed;
}

public static class Script
{
    public const double RaceShare = 0.2;  // share of closes that race a planner edit

    public static int ScrapCap(SimOrder o) => Math.Max(1, o.FloorQty / 20);

    public static Step Next(SimOrder o, Random rnd)
    {
        if (o.Closed) throw new InvalidOperationException("closed orders take no more steps");
        if (!o.Released) return Step.Release;
        var choices = new List<(Step Step, double Weight)>();
        if (o.Finished < o.Ops.Length) choices.Add((o.Started == o.Finished ? Step.StartOp : Step.FinishOp, 5));
        if (o.Scrapped < ScrapCap(o)) choices.Add((Step.Scrap, 1.5));
        choices.Add((Step.PlanChange, 0.8));
        if (o.Finished == o.Ops.Length) choices.Add((Step.Close, 6));
        var pick = rnd.NextDouble() * choices.Sum(c => c.Weight);
        foreach (var (step, weight) in choices)
        {
            if ((pick -= weight) > 0) continue;
            return step == Step.Close && rnd.NextDouble() < RaceShare ? Step.Race : step;
        }
        return choices[^1].Step;
    }

    public static int ScrapAmount(SimOrder o, Random rnd) => Math.Max(1, Math.Min(rnd.Next(1, 4), ScrapCap(o) - o.Scrapped));

    public static int NewQty(SimOrder o, Random rnd) =>
        Math.Max(Math.Max(10, o.Scrapped + 1), (int)Math.Round(o.Qty * (0.85 + rnd.NextDouble() * 0.4)));

    public static DateTime NewDue(SimOrder o, Random rnd)
    {
        var due = o.DueDate.AddDays(rnd.Next(-3, 15));
        var earliest = DateTime.Today.AddDays(1);
        return due < earliest ? earliest : due;
    }

    // Times are the moments the events happen, taken from the database server's clock (the procedures stamp the order
    // itself with GETDATE()), so an operation never starts before its order or ends in the future. The hours are the
    // recorded work, a separate number: a demo runs in seconds, a real operation takes hours.
    public static (int Index, DateTime Start, double Hours) PlanStart(SimOrder o, DateTime serverNow, Random rnd) =>
        (o.Started, serverNow, Math.Round(0.5 + rnd.NextDouble() * 5.5, 2));

    public static (int Index, DateTime Start, DateTime End, double Hours, decimal Cost) PlanFinish(SimOrder o, DateTime serverNow, Random rnd)
    {
        var i = o.Finished;
        var end = serverNow > o.StartedAt[i] ? serverNow : o.StartedAt[i].AddSeconds(1);
        return (i, o.StartedAt[i], end, o.Hours[i], Math.Round((decimal)(o.Hours[i] * (18 + rnd.NextDouble() * 27)), 2));
    }

    public static void DidRelease(SimOrder o, int[] ops)
    {
        o.Released = true;
        o.Ops = ops;
        o.Hours = new double[ops.Length];
        o.StartedAt = new DateTime[ops.Length];
    }

    public static void DidStart(SimOrder o, int index, DateTime start, double hours)
    {
        o.StartedAt[index] = start;
        o.Hours[index] = hours;
        o.Started = index + 1;
    }

    public static void DidFinish(SimOrder o) => o.Finished++;
    public static void DidScrap(SimOrder o, int n) => o.Scrapped += n;
    public static void DidPlan(SimOrder o, int? qty, DateTime? due)
    {
        if (qty is { } q) { o.Qty = q; o.FloorQty = Math.Min(o.FloorQty, q); }
        if (due is { } d) o.DueDate = d;
    }
    public static void DidClose(SimOrder o) => o.Closed = true;
}
