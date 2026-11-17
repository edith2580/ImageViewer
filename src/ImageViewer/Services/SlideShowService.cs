using Avalonia.Threading;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>
/// 슬라이드쇼 타이머와 다음 이미지 선택 규칙.
/// - 순차: 마지막 이후 반복 옵션이면 처음으로, 아니면 종료
/// - 랜덤: 섞은 순서(셔플 백)로 재생해 직전 이미지가 연속으로 나오지 않게 한다.
///   반복 옵션이 꺼져 있으면 모든 이미지를 한 번씩 보여 준 뒤 종료한다.
/// - 수동 이동 시 타이머를 다시 시작한다 (정책: 수동 이동 후 타이머 재시작).
/// </summary>
public sealed class SlideShowService : IDisposable
{
    private readonly Random _random;
    private readonly Queue<int> _bag = new();
    private DispatcherTimer? _timer;
    private int _bagCount = -1;
    private bool _bagCompletedOnce;
    private int _intervalSeconds = SlideShowSettings.DefaultIntervalSeconds;
    private SlideShowMode _mode = SlideShowMode.Sequential;

    public SlideShowService()
        : this(Random.Shared)
    {
    }

    public SlideShowService(Random random)
    {
        _random = random;
    }

    public event EventHandler? Tick;

    public bool IsPlaying { get; private set; }

    public bool Repeat { get; set; } = true;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            _intervalSeconds = SlideShowSettings.ClampInterval(value);
            if (_timer is not null)
            {
                _timer.Interval = TimeSpan.FromSeconds(_intervalSeconds);
            }
        }
    }

    public SlideShowMode Mode
    {
        get => _mode;
        set
        {
            if (_mode != value)
            {
                _mode = value;
                ResetOrder();
            }
        }
    }

    public void Start()
    {
        ResetOrder();
        EnsureTimer();
        _timer!.Stop();
        _timer.Start();
        IsPlaying = true;
    }

    public void Stop()
    {
        _timer?.Stop();
        IsPlaying = false;
    }

    /// <summary>재생 중이면 간격 타이머를 처음부터 다시 센다.</summary>
    public void RestartTimer()
    {
        if (IsPlaying && _timer is not null)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    /// <summary>목록이나 재생 방식이 바뀌었을 때 랜덤 순서를 초기화한다.</summary>
    public void ResetOrder()
    {
        _bag.Clear();
        _bagCount = -1;
        _bagCompletedOnce = false;
    }

    /// <summary>다음에 보여 줄 이미지 위치를 꺼낸다 (랜덤 순서에서 소비). 재생을 끝내야 하면 null.</summary>
    public int? GetNextIndex(int currentIndex, int count)
    {
        var next = PeekNextIndex(currentIndex, count);
        if (next is not null && Mode == SlideShowMode.Random && count > 1)
        {
            _bag.Dequeue();
        }

        return next;
    }

    /// <summary>
    /// 다음 타이머에 보여 줄 이미지 위치를 소비하지 않고 미리 본다 (미리 읽기용).
    /// 랜덤 재생이면 섞은 순서가 아직 없을 때 여기서 만들어 두므로, 이후 <see cref="GetNextIndex"/> 와 같은 결과를 준다.
    /// </summary>
    public int? PeekNextIndex(int currentIndex, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        if (count == 1)
        {
            return Repeat ? 0 : null;
        }

        if (Mode == SlideShowMode.Sequential)
        {
            var next = currentIndex + 1;
            if (next < count)
            {
                return next;
            }

            return Repeat ? 0 : null;
        }

        if (_bagCount != count)
        {
            _bag.Clear();
            _bagCount = count;
            _bagCompletedOnce = false;
        }

        while (true)
        {
            if (_bag.Count == 0)
            {
                if (_bagCompletedOnce && !Repeat)
                {
                    return null;
                }

                FillBag(currentIndex, count, firstRound: !_bagCompletedOnce);
                _bagCompletedOnce = true;
            }

            // 현재 이미지(수동으로 이동한 경우 등)이거나 목록 밖이면 건너뛴다. 꺼낼 때도 똑같이 건너뛰므로 버려도 된다.
            var candidate = _bag.Peek();
            if (candidate != currentIndex && candidate < count)
            {
                return candidate;
            }

            _bag.Dequeue();
            if (_bag.Count == 0 && !Repeat)
            {
                return null;
            }
        }
    }

    private void FillBag(int currentIndex, int count, bool firstRound)
    {
        var order = Enumerable.Range(0, count).Where(i => i != currentIndex).ToArray();
        _random.Shuffle(order);
        foreach (var i in order)
        {
            _bag.Enqueue(i);
        }

        // 반복 재생 시 다음 라운드에서는 현재 이미지도 포함하되 맨 앞에는 오지 않도록 뒤에 넣는다.
        if (!firstRound && currentIndex >= 0 && currentIndex < count)
        {
            _bag.Enqueue(currentIndex);
        }
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_intervalSeconds),
        };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Stop();
        _timer = null;
    }
}
