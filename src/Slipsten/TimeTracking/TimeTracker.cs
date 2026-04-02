using Microsoft.EntityFrameworkCore;
using Slipsten.Data;

namespace Slipsten.TimeTracking;

public class TimeTracker : IDisposable
{
    private readonly object _syncLock = new();
    private readonly System.Timers.Timer _flushTimer;
    private RunningEntryState? _running;
    private bool _isDisposed;

    public event EventHandler? StateChanged;

    public TimeTracker()
    {
        CloseOrphanedEntries();

        _flushTimer = new System.Timers.Timer(30_000);
        _flushTimer.Elapsed += (_, _) =>
        {
            try { Flush(); }
            catch (Exception ex) { CrashLogger.Log("TimeTracker.Flush", ex); }
        };
        _flushTimer.Start();
    }

    public bool IsRunning
    {
        get
        {
            lock (_syncLock)
                return _running != null;
        }
    }

    public WorkType? CurrentType
    {
        get
        {
            lock (_syncLock)
                return _running?.Type;
        }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { CrashLogger.Log("TimeTracker.StateChanged", ex); }
    }

    /// <summary>Closes any entries with null EndTime left from a crash or unclean shutdown.</summary>
    private static void CloseOrphanedEntries()
    {
        using var db = CreateContext();

        var orphaned = db.TimeEntries.Where(e => e.EndTime == null).ToList();
        foreach (var entry in orphaned)
            entry.EndTime = entry.StartTime;

        if (orphaned.Count > 0)
            db.SaveChanges();
    }

    public void Start(WorkType type)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();

            if (_running != null)
                StopInternal(string.Empty, DateTime.Now);

            using var db = CreateContext();
            var startedAt = DateTime.Now;
            var entry = new TimeEntry { StartTime = startedAt, Type = type };
            db.TimeEntries.Add(entry);
            db.SaveChanges();

            _running = new RunningEntryState(entry.Id, startedAt, null, type);
        }

        RaiseStateChanged();
    }

    public void Stop(string description)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();

            if (_running == null)
                return;

            StopInternal(description, DateTime.Now);
        }

        RaiseStateChanged();
    }

    /// <summary>Called when idle detected — ends open entry at idle-start time.</summary>
    public TimeEntry? PauseAt(DateTime idleStartedAt)
    {
        TimeEntry? paused = null;

        lock (_syncLock)
        {
            ThrowIfDisposed();

            if (_running == null)
                return null;

            using var db = CreateContext();
            var entry = db.TimeEntries.SingleOrDefault(e => e.Id == _running.Id);
            if (entry != null)
            {
                entry.EndTime = idleStartedAt;
                db.SaveChanges();
            }

            paused = new TimeEntry
            {
                Id = _running.Id,
                StartTime = _running.StartTime,
                EndTime = idleStartedAt,
                Type = _running.Type
            };
            _running = null;
        }

        RaiseStateChanged();
        return paused;
    }

    /// <summary>Re-opens the last entry so the idle gap is kept in the same slice.</summary>
    public void ResumeLastEntry()
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();

            if (_running != null)
                return;

            using var db = CreateContext();
            var last = db.TimeEntries
                .OrderByDescending(e => e.Id)
                .FirstOrDefault();

            if (last != null)
            {
                last.EndTime = null;
                db.SaveChanges();
                _running = new RunningEntryState(last.Id, last.StartTime, null, last.Type);
            }
            else
            {
                var startedAt = DateTime.Now;
                var entry = new TimeEntry { StartTime = startedAt, Type = WorkType.Ordinary };
                db.TimeEntries.Add(entry);
                db.SaveChanges();
                _running = new RunningEntryState(entry.Id, startedAt, null, entry.Type);
            }
        }

        RaiseStateChanged();
    }

    /// <summary>Writes the current time as EndTime so no work is lost on crash.</summary>
    private void Flush()
    {
        lock (_syncLock)
        {
            if (_isDisposed || _running == null)
                return;

            using var db = CreateContext();
            var entry = db.TimeEntries.SingleOrDefault(e => e.Id == _running.Id);
            if (entry == null)
            {
                _running = null;
                return;
            }

            var flushedAt = DateTime.Now;
            entry.EndTime = flushedAt;
            db.SaveChanges();
            _running = _running with { LastPersistedEndTime = flushedAt };
        }
    }

    /// <summary>Today's total across all entries.</summary>
    public TimeSpan TodayTotal()
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();

            using var db = CreateContext();
            var today = DateTime.Today;
            var entries = db.TimeEntries
                .AsNoTracking()
                .Where(e => e.StartTime >= today && e.EndTime != null)
                .ToList();

            var total = entries.Aggregate(TimeSpan.Zero, (acc, e) => acc + (e.EndTime!.Value - e.StartTime));

            if (_running != null)
            {
                total += _running.LastPersistedEndTime.HasValue
                    ? DateTime.Now - _running.LastPersistedEndTime.Value
                    : DateTime.Now - _running.StartTime;
            }

            return total;
        }
    }

    /// <summary>Returns entries grouped by day for the given month.</summary>
    public List<DailySummary> GetMonthlySummary(int year, int month)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();

            using var db = CreateContext();
            var start = new DateTime(year, month, 1);
            var end = start.AddMonths(1);

            var entries = db.TimeEntries
                .AsNoTracking()
                .Where(e => e.StartTime >= start && e.StartTime < end && e.EndTime != null)
                .ToList();

            return entries
                .GroupBy(e => e.StartTime.Date)
                .Select(g => new DailySummary
                {
                    Date = g.Key,
                    Ordinary = g.Where(e => e.Type == WorkType.Ordinary)
                                 .Aggregate(TimeSpan.Zero, (a, e) => a + (e.EndTime!.Value - e.StartTime)),
                    DoublePay = g.Where(e => e.Type == WorkType.DoublePay)
                                  .Aggregate(TimeSpan.Zero, (a, e) => a + (e.EndTime!.Value - e.StartTime))
                })
                .OrderBy(d => d.Date)
                .ToList();
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _flushTimer.Stop();
            _flushTimer.Dispose();
            _running = null;
        }
    }

    private void StopInternal(string description, DateTime stoppedAt)
    {
        if (_running == null)
            return;

        using var db = CreateContext();
        var entry = db.TimeEntries.SingleOrDefault(e => e.Id == _running.Id);
        if (entry != null)
        {
            entry.EndTime = stoppedAt;
            entry.Description = description;
            db.SaveChanges();
        }

        _running = null;
    }

    private static SlipstenContext CreateContext() => new();

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(TimeTracker));
    }

    private sealed record RunningEntryState(int Id, DateTime StartTime, DateTime? LastPersistedEndTime, WorkType Type);
}

public class DailySummary
{
    public DateTime Date { get; init; }
    public TimeSpan Ordinary { get; init; }
    public TimeSpan DoublePay { get; init; }
    public TimeSpan Total => Ordinary + DoublePay;

    /// <summary>Total rounded up to the next 30-minute increment.</summary>
    public TimeSpan TotalRounded
    {
        get
        {
            var minutes = (int)Math.Ceiling(Total.TotalMinutes / 30.0) * 30;
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
