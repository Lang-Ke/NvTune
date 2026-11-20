namespace NvTune;

internal interface IFanBackend
{
    int TemperatureC(int gpuIndex);
    void SetPercent(int gpuIndex, int percent);
    void RestoreAuto(int gpuIndex);
}

/// <summary>One control path for the GUI and daemon. A failed sensor or write never becomes a
/// low-temperature sample, and one failing GPU never prevents the others from being updated.</summary>
internal sealed class FanController(IFanBackend backend, Action<string> log)
{
    private readonly Dictionary<int, string> _lastErrors = new();
    private readonly Dictionary<int, int> _requests = new();
    public int? RequestedPercent(int index) => _requests.TryGetValue(index, out int request) ? request : null;

    public void Apply(int index, FanState.FanSettings? settings)
    {
        try
        {
            if (settings is null || settings.ModeValue == FanState.Mode.Auto)
            {
                backend.RestoreAuto(index);
                _requests.Remove(index);
            }
            else
            {
                settings.Validate();
                int target = settings.ModeValue == FanState.Mode.Fixed
                    ? settings.FixedPercent!.Value
                    : settings.Curve!.FanPercentAt(backend.TemperatureC(index));
                backend.SetPercent(index, target);
                _requests[index] = target;
            }
            if (_lastErrors.Remove(index)) log($"GPU {index} 风扇控制已恢复。");
        }
        catch (Exception primary)
        {
            _requests.Remove(index);
            // Restore even after a partial multi-fan write. Try maximum cooling if the driver
            // refuses auto; report both failures instead of pretending the handback succeeded.
            string recovery;
            try { backend.RestoreAuto(index); recovery = "已恢复自动风扇"; }
            catch (Exception autoError)
            {
                try { backend.SetPercent(index, 100); recovery = $"自动控制恢复失败 ({autoError.Message})，已设置 100% 转速"; }
                catch (Exception emergencyError)
                {
                    recovery = $"自动控制及 100% 转速均失败 ({autoError.Message}; {emergencyError.Message})";
                }
            }
            string message = $"GPU {index} 风扇控制失败: {primary.Message}；{recovery}。将继续重试。";
            if (!_lastErrors.TryGetValue(index, out string? previous) || previous != message)
                log(message);
            _lastErrors[index] = message;
            throw new CliError(message);
        }
    }

    public bool Tick(IEnumerable<int> indexes, Func<int, FanState.FanSettings?> load)
    {
        bool ok = true;
        foreach (int index in indexes.Distinct().ToArray())
        {
            FanState.FanSettings? settings;
            try { settings = load(index); }
            catch (Exception ex)
            {
                try { Apply(index, null); } catch { }
                log($"GPU {index} 风扇配置不可用: {ex.Message}");
                ok = false;
                continue;
            }
            try { Apply(index, settings); }
            catch { ok = false; }
        }
        return ok;
    }

    public bool RestoreSaved(IEnumerable<int> indexes, Func<int, FanState.FanSettings?> loadSaved)
        => Tick(indexes, loadSaved);
}
