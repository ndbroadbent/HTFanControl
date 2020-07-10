﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Timers
{
    internal interface IPositionTimer
    {
        bool Update(TimeSpan currentPosition);
        bool Stop();
        ValueTask<bool> DisposeAsync(bool stop);
    }

    internal class PositionTimer<T> : IPositionTimer
    {
        private readonly TimeSpan[] _positions;
        private readonly T[] _values;
        private readonly T _defaultValue;

        private const long ElapsedPositionToleranceTicks = 250000;
        private const long CurrentPositionToleranceTicks = ElapsedPositionToleranceTicks * 2;
        private const long SkipWindowTicks = CurrentPositionToleranceTicks * 10;
        private const long MinAdjustedIntervalTicks = 600000000;
        private const double AdjustmentFraction = 0.8;

        private Action<T> _action;
        private Timer _timer;
        private TimeSpan _startPosition;
        private Stopwatch _stopwatch;
        private int _index;
        private TimeSpan _nextPosition;
        private T _lastValue;
        private bool _invoking;
        private TaskCompletionSource<bool> _disposed;

        public PositionTimer(IEnumerable<(TimeSpan position, T value)> values, Action<T> action,
            T defaultValue = default)
        {
            var orderedValues = values.OrderBy(v => v.position).ToList();
            var count = orderedValues.Count;
            _positions = new TimeSpan[count];
            _values = new T[count];
            TimeSpan lastPosition = default;
            var lastValue = defaultValue;
            var distinct = 0;
            for (var i = 0; i < count; i++)
            {
                var (position, value) = orderedValues[i];
                if (position == lastPosition && distinct > 0)
                {
                    lastValue = distinct > 1 ? _values[distinct - 2] : defaultValue;
                    if (EqualityComparer<T>.Default.Equals(value, lastValue))
                    {
                        lastPosition = TimeSpan.MinValue;
                        distinct--;
                    }
                    else
                    {
                        _values[distinct - 1] = value;
                        lastValue = value;
                    }
                }
                else if (!EqualityComparer<T>.Default.Equals(value, lastValue))
                {
                    _positions[distinct] = position;
                    _values[distinct] = value;
                    lastPosition = position;
                    lastValue = value;
                    distinct++;
                }
            }

            Array.Resize(ref _positions, distinct);
            Array.Resize(ref _values, distinct);

            _action = action ?? throw new ArgumentNullException(nameof(action));
            _defaultValue = defaultValue;

            InvokeTimerCallback();
        }

        public bool Update(TimeSpan currentPosition)
        {
            lock (_positions)
            {
                if (_timer != null)
                {
                    var timerPosition = GetCurrentPosition();
                    var deltaTicks = Subtract(timerPosition, currentPosition);
                    if (deltaTicks < 0)
                    {
                        deltaTicks = unchecked(-deltaTicks);
                        if (deltaTicks < 0)
                            deltaTicks = long.MaxValue;
                    }

                    if (deltaTicks <= CurrentPositionToleranceTicks)
                        return true;

                    _stopwatch.Restart();

                    if (currentPosition < timerPosition && deltaTicks <= SkipWindowTicks &&
                        Subtract(currentPosition, _startPosition) > SkipWindowTicks)
                    {
                        if (!_invoking && currentPosition < _nextPosition)
                            Change(currentPosition);
                    }
                    else
                    {
                        UpdateStateAndChangeOrInvoke(currentPosition);
                    }
                }
                else if (_action != null && _disposed == null)
                {
                    if (_positions.Length > 0)
                    {
                        _stopwatch = Stopwatch.StartNew();
                        _timer = new Timer(TimerCallback);

                        UpdateStateAndChangeOrInvoke(currentPosition);
                    }
                }
                else
                {
                    return false;
                }

                _startPosition = currentPosition;
            }

            return true;
        }

        public bool Stop()
        {
            lock (_positions)
            {
                if (_timer == null)
                    return _action != null && _disposed == null;

                _timer.Dispose();
                _timer = null;
                _stopwatch = null;

                if (!_invoking && !EqualityComparer<T>.Default.Equals(_lastValue, _defaultValue))
                    InvokeTimerCallback();
            }

            return true;
        }

        public ValueTask<bool> DisposeAsync(bool stop)
        {
            lock (_positions)
            {
                if (_action == null || _disposed != null)
                    return new ValueTask<bool>(false);

                if (stop)
                {
                    _timer?.Dispose();
                    _timer = null;

                    if (_invoking)
                    {
                        _disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    else if (EqualityComparer<T>.Default.Equals(_lastValue, _defaultValue))
                    {
                        _action = null;
                        return new ValueTask<bool>(true);
                    }
                    else
                    {
                        InvokeTimerCallback();

                        _disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                else
                {
                    _action = null;
                    _timer?.Dispose();
                    _timer = null;

                    if (_invoking)
                    {
                        _disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    else
                    {
                        return new ValueTask<bool>(true);
                    }
                }
            }

            return new ValueTask<bool>(_disposed.Task);
        }

        private void TimerCallback(object state)
        {
            if (state != null)
            {
                if (state != _timer)
                    return;

                lock (_positions)
                {
                    if (_timer != state || _invoking)
                        return;

                    var currentPosition = GetCurrentPosition();

                    TimeSpan elapsedPosition;
                    if (currentPosition < _nextPosition)
                    {
                        if (Subtract(_nextPosition, currentPosition) > ElapsedPositionToleranceTicks)
                        {
                            Change(currentPosition);
                            return;
                        }

                        elapsedPosition = _nextPosition;
                    }
                    else
                    {
                        elapsedPosition = currentPosition;
                    }

                    var currentValue = UpdateIndexAndGetValue(elapsedPosition);
                    UpdateNextPosition(elapsedPosition);

                    if (EqualityComparer<T>.Default.Equals(_lastValue, currentValue))
                    {
                        if (elapsedPosition < _nextPosition)
                            Change(currentPosition);

                        return;
                    }

                    _lastValue = currentValue;
                    _invoking = true;
                }
            }
            else
            {
                _lastValue = _defaultValue;
            }

            _action?.Invoke(_lastValue);

            lock (_positions)
            {
                _invoking = false;

                if (_timer != null)
                {
                    var currentPosition = GetCurrentPosition();
                    if (currentPosition < _nextPosition)
                    {
                        Change(currentPosition);
                    }
                    else
                    {
                        UpdateStateAndChangeOrInvoke(currentPosition);
                    }
                }
                else if (!EqualityComparer<T>.Default.Equals(_lastValue, _defaultValue) && _action != null)
                {
                    InvokeTimerCallback();
                }
                else
                {
                    _disposed?.SetResult(true);
                }
            }
        }

        private TimeSpan GetCurrentPosition() => new TimeSpan(Add(_startPosition, _stopwatch.Elapsed));

        private void UpdateStateAndChangeOrInvoke(TimeSpan currentPosition)
        {
            if (_invoking)
            {
                _nextPosition = TimeSpan.MinValue;
            }
            else if (EqualityComparer<T>.Default.Equals(_lastValue, UpdateIndexAndGetValue(currentPosition)))
            {
                UpdateNextPosition(currentPosition);

                if (currentPosition < _nextPosition)
                    Change(currentPosition);
            }
            else
            {
                InvokeTimerCallback();
            }
        }

        private void Change(TimeSpan currentPosition)
        {
            _timer.Change(Math.Min((long) GetInterval(currentPosition, _nextPosition).TotalMilliseconds, 4294967294),
                Timeout.Infinite);
        }

        private void InvokeTimerCallback()
        {
            if (_timer == null)
            {
                _invoking = true;
                ThreadPool.QueueUserWorkItem(TimerCallback, null);
            }
            else
            {
                _nextPosition = TimeSpan.MinValue;
                _timer.Change(0, Timeout.Infinite);
            }
        }

        private T UpdateIndexAndGetValue(TimeSpan currentPosition)
        {
            if (currentPosition >= _positions[_index])
            {
                if (_index == _positions.Length - 1 || currentPosition < _positions[_index + 1])
                    return _values[_index];

                _index++;

                if (_index != _positions.Length - 1 && currentPosition >= _positions[_index + 1])
                    _index = BinarySearch(_positions, _index + 1, _positions.Length - 1, currentPosition);
            }
            else if (currentPosition < _positions[0])
            {
                _index = 0;
                return _defaultValue;
            }
            else
            {
                _index = BinarySearch(_positions, 1, _index - 1, currentPosition);
            }

            return _values[_index];
        }

        private void UpdateNextPosition(TimeSpan currentPosition)
        {
            if (_index == 0 && currentPosition < _positions[0])
            {
                _nextPosition = _positions[0];
            }
            else if (_index == _positions.Length - 1)
            {
                _nextPosition = _positions[_index];
            }
            else
            {
                _nextPosition = _positions[_index + 1];
            }
        }

        private static int BinarySearch(TimeSpan[] array, int lowIndex, int highIndex, TimeSpan value)
        {
            while (lowIndex <= highIndex)
            {
                var i = lowIndex + ((highIndex - lowIndex) >> 1);
                var order = array[i].CompareTo(value);

                if (order == 0)
                    return i;

                if (order < 0)
                {
                    lowIndex = i + 1;
                }
                else
                {
                    highIndex = i - 1;
                }
            }

            return highIndex;
        }

        private static TimeSpan GetInterval(TimeSpan startPosition, TimeSpan endPosition)
        {
            var intervalTicks = Subtract(endPosition, startPosition);

            if (intervalTicks <= MinAdjustedIntervalTicks)
                return new TimeSpan(intervalTicks);

            var adjustedIntervalTicks = (long) Math.Ceiling(intervalTicks * AdjustmentFraction);
            return adjustedIntervalTicks < MinAdjustedIntervalTicks
                ? new TimeSpan(intervalTicks)
                : new TimeSpan(adjustedIntervalTicks);
        }

        private static long Add(TimeSpan t1, TimeSpan t2)
        {
            var t1Ticks = t1.Ticks;
            var t2Ticks = t2.Ticks;
            var resultTicks = unchecked(t1Ticks + t2Ticks);

            if (t1Ticks >> 63 == t2Ticks >> 63 && t1Ticks >> 63 != resultTicks >> 63)
                return long.MaxValue;

            return resultTicks;
        }

        private static long Subtract(TimeSpan t1, TimeSpan t2)
        {
            var t1Ticks = t1.Ticks;
            var t2Ticks = t2.Ticks;
            var resultTicks = unchecked(t1Ticks - t2Ticks);

            if (t1Ticks >> 63 != t2Ticks >> 63 && t1Ticks >> 63 != resultTicks >> 63)
                return long.MaxValue;

            return resultTicks;
        }
    }
}