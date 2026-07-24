using PalPeek.Core;

namespace PalPeek;

public sealed class HostStateStore
{
    private readonly object _gate = new();
    private HostStatus _value;

    public HostStateStore(PalPeekOptions options) =>
        _value = HostStatus.Offline(options.Nickname, "正在启动。");

    public event EventHandler<HostStatus>? Changed;

    public HostStatus Get()
    {
        lock (_gate) return _value;
    }

    public void Set(HostStatus value)
    {
        lock (_gate)
        {
            if (_value == value)
                return;
            _value = value;
        }
        Changed?.Invoke(this, value);
    }
}
