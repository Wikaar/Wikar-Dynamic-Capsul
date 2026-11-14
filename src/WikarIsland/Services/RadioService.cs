using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace WikarIsland.Services;

/// <summary>Wi-Fi и Bluetooth через Windows.Devices.Radios (то же, что переключатели в центре уведомлений Windows).</summary>
public static class RadioService
{
    public static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            RadioAccessStatus access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return null;
            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }

    /// <summary>Переключить; возвращает новое состояние или null, если не вышло.</summary>
    public static async Task<bool?> ToggleAsync(RadioKind kind)
    {
        Radio? radio = await FindAsync(kind);
        if (radio == null) return null;
        RadioState next = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        try
        {
            RadioAccessStatus result = await radio.SetStateAsync(next);
            if (result != RadioAccessStatus.Allowed) return null;
            return next == RadioState.On;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }
}
