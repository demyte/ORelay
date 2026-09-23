namespace ORelay.Configuration;

/// <summary>The last settings snapshot successfully applied by the running relay.</summary>
internal sealed class RelayConfigurationState(RelaySettings initial)
{
    private RelaySettings _current = initial ?? throw new ArgumentNullException(nameof(initial));

    public RelaySettings Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public void Publish(RelaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _current, settings);
        Changed?.Invoke();
    }
}
