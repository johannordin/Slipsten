using System.Runtime.InteropServices;
using Slipsten.Data;

namespace Slipsten.TimeTracking;

public class IdleDetector : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public event EventHandler? WentIdle;
    public event EventHandler? Returned;

    private readonly System.Timers.Timer _timer;
    private bool _isIdle;
    private int _thresholdSeconds;

    public IdleDetector(int thresholdSeconds)
    {
        _thresholdSeconds = thresholdSeconds;
        _timer = new System.Timers.Timer(5_000); // poll every 5 s
        _timer.Elapsed += (_, _) =>
        {
            try { Check(); }
            catch (Exception ex) { CrashLogger.Log("IdleDetector.Check", ex); }
        };
    }

    public void UpdateThreshold(int seconds) => _thresholdSeconds = seconds;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        GetLastInputInfo(ref info);
        return TimeSpan.FromMilliseconds(Environment.TickCount - (int)info.dwTime);
    }

    private void Check()
    {
        var idle = GetIdleTime() >= TimeSpan.FromSeconds(_thresholdSeconds);

        if (idle && !_isIdle)
        {
            _isIdle = true;
            try { WentIdle?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { CrashLogger.Log("IdleDetector.WentIdle", ex); }
        }
        else if (!idle && _isIdle)
        {
            _isIdle = false;
            try { Returned?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { CrashLogger.Log("IdleDetector.Returned", ex); }
        }
    }

    public void Dispose() => _timer.Dispose();
}
