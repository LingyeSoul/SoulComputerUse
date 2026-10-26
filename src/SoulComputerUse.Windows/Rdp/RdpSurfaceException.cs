using System;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>A failure reported by the remote desktop COM surface, with the HRESULT kept for diagnosis.</summary>
public sealed class RdpSurfaceException : Exception
{
    private const int HResultBase = unchecked((int)0x80004005);

    /// <summary>Creates a failure for one COM member call.</summary>
    public RdpSurfaceException(string operation, string parameters, int hResult, string message)
        : base($"{message} (operation: {operation}; parameters: {parameters}; HRESULT: 0x{hResult:X8})", null)
    {
        Operation = operation;
        Parameters = parameters;
        ComHResult = hResult;
        HResult = HResultBase;
    }

    /// <summary>The COM member that failed.</summary>
    public string Operation { get; }

    /// <summary>The parameters that were passed, without any credential material.</summary>
    public string Parameters { get; }

    /// <summary>The raw HRESULT returned by COM.</summary>
    public int ComHResult { get; }
}