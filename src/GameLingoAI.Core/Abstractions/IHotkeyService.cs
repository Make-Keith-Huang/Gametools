using GameLingoAI.Core.Configuration;

namespace GameLingoAI.Core.Abstractions;

public sealed record HotkeyApplyResult(bool IsSuccess, string Message);

/// <summary>Owned application shortcuts; call on the owning UI thread. No general keyboard stream is exposed.</summary>
public interface IHotkeyService : IDisposable
{
    event EventHandler<HotkeyAction>? Triggered;

    HotkeyApplyResult Apply(HotkeyOptions options);

    HotkeyApplyResult Clear();
}
