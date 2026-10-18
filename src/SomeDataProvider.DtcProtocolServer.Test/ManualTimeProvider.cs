// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>A monotonic test clock. Advance runs due callbacks in time order, outside the clock's lock.</summary>
sealed class ManualTimeProvider : TimeProvider
{
	readonly object _lock = new();
	readonly List<ManualTimer> _timers = new();
	long _ticks;

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public int ActiveTimers
	{
		get
		{
			lock (_lock) return _timers.Count(t => t.Due != long.MaxValue);
		}
	}

	public override long GetTimestamp()
	{
		lock (_lock) return _ticks;
	}

	public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		var timer = new ManualTimer(this, callback, state);
		lock (_lock)
		{
			_timers.Add(timer);
			timer.Change(dueTime, period);
		}
		return timer;
	}

	public void Advance(TimeSpan time)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
		long target;
		lock (_lock) target = checked(_ticks + time.Ticks);
		while (true)
		{
			ManualTimer timer;
			lock (_lock)
			{
				var next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
				if (next == null)
				{
					_ticks = target;
					return;
				}
				timer = next;
				_ticks = timer.Due;
				timer.Due = timer.Period > TimeSpan.Zero ? checked(_ticks + timer.Period.Ticks) : long.MaxValue;
			}
			timer.Callback(timer.State);
		}
	}

	sealed class ManualTimer : ITimer
	{
		readonly ManualTimeProvider _clock;
		bool _disposed;

		public ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state)
		{
			_clock = clock;
			Callback = callback;
			State = state;
		}

		public TimerCallback Callback { get; }

		public object? State { get; }

		public long Due { get; set; } = long.MaxValue;

		public TimeSpan Period { get; private set; }

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			lock (_clock._lock)
			{
				if (_disposed) return false;
				Period = period;
				Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_clock._ticks + dueTime.Ticks);
				return true;
			}
		}

		public void Dispose()
		{
			lock (_clock._lock)
			{
				_disposed = true;
				Due = long.MaxValue;
				_clock._timers.Remove(this);
			}
		}

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
