namespace Updatr.Core.Errors;

/// <summary>What a decoded code means for the item that produced it.</summary>
public enum CodeOutcome
{
    /// <summary>The operation failed.</summary>
    Failure,

    /// <summary>Nothing to do - already installed or already current. Not a failure.</summary>
    AlreadyCurrent,

    /// <summary>The operation succeeded but needs (or started) a reboot to finish.</summary>
    SuccessRebootRequired,
}

/// <summary>A numeric installer / winget / HRESULT code decoded into something a person can act on.</summary>
/// <param name="Code">The code masked to 32 bits.</param>
/// <param name="Hint">Plain-English meaning, or null when the code is not one Updatr knows.</param>
/// <param name="Outcome">How the item that returned it should be counted.</param>
/// <param name="Transient">
/// True when the same action is likely to succeed later without changing anything (file in use,
/// another install running, network down). The queue may wait and retry these.
/// </param>
public sealed record ErrorCodeInfo(uint Code, string? Hint, CodeOutcome Outcome, bool Transient)
{
    /// <summary>The code as <c>0xXXXXXXXX</c>.</summary>
    public string Hex => $"0x{Code:X8}";

    /// <summary><c>0xXXXXXXXX (hint)</c>, or just the hex when the code is unknown.</summary>
    public override string ToString() => Hint is null ? Hex : $"{Hex} ({Hint})";
}
