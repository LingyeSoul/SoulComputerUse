using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// The seam between input planning and the real user32 call. Tests use it to inspect the constructed
/// native events without injecting anything into the desktop.
/// </summary>
public interface ISendInput
{
    /// <summary>Sends one batch of native events and returns how many the system accepted.</summary>
    uint Send(IReadOnlyList<INPUT> inputs);
}

/// <summary>The production adapter over user32 SendInput.</summary>
public sealed class SendInputAdapter : ISendInput
{
    /// <summary>The size of the native INPUT structure on x64.</summary>
    public const int NativeInputSize = 40;

    /// <inheritdoc />
    public uint Send(IReadOnlyList<INPUT> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return 0;
        }

        int inputSize = Marshal.SizeOf<INPUT>();
        if (inputSize != NativeInputSize)
        {
            throw new InvalidOperationException(
                $"The native INPUT structure must be {NativeInputSize} bytes on x64, but it is {inputSize} bytes.");
        }

        INPUT[] buffer = new INPUT[inputs.Count];
        for (int index = 0; index < inputs.Count; index++)
        {
            buffer[index] = inputs[index];
        }

        return SendInputNative.SendInput((uint)buffer.Length, buffer, inputSize);
    }
}