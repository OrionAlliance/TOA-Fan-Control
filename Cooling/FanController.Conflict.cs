// Foreign fan writer detection and the conflict tick rate.
using System.Timers;
using FanControlApp.Infrastructure;
using Timer = System.Timers.Timer;

namespace FanControlApp.Cooling;

public sealed partial class FanController
{
    /// <summary>A persistent duty mismatch means a foreign writer: tick 4x until quiet for 30s.</summary>
    private void DetectForeignWriter(List<FanChannel> controlled)
    {
        if (!_engaged) return; // nothing of ours on the chip yet

        bool foreign = controlled.Any(f =>
            f.Percent is { } p && Math.Abs(p - _currentPercent) > ForeignWriteTolerance);

        if (foreign)
        {
            _lastForeignWriteMs = Environment.TickCount64;
            _foreignTicks++;

            if (!_conflict && _foreignTicks >= ForeignTicksToConfirm)
            {
                _conflict = true;
                SetTickRate(ConflictTickMs);
                DebugLog.Write(
                    "!! Another program is writing fan speeds over ours (chip readback " +
                    "disagrees with our command). Holding control at 4x re-assert rate. " +
                    "RGB suites (SignalRGB, iCUE, ...) often enable fan control in updates.");
            }
        }
        else
        {
            _foreignTicks = 0;

            if (_conflict && Environment.TickCount64 - _lastForeignWriteMs > ConflictClearMs)
            {
                _conflict = false;
                SetTickRate(TickMs);
                DebugLog.Write("Foreign fan writes stopped - back to the normal tick rate.");
            }
        }
    }

    // Stop/Start dodges the assign-Interval-inside-Elapsed Timers.Timer quirk; no-op once disposed.
    private void SetTickRate(double ms)
    {
        if (System.Threading.Volatile.Read(ref _disposedFlag) != 0) return;
        _timer.Stop();
        _timer.Interval = ms;
        _timer.Start();
    }
}
