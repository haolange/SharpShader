using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpShader.HLSLCrossCompiler.Internal;

internal sealed class NativeWideStringMarshaller : IDisposable
{
    private readonly List<nint> _allocations = new List<nint>();
    private bool _disposed;

    public nint SourceName { get; private set; }

    public nint EntryPoint { get; private set; }

    public nint Profile { get; private set; }

    public nint Arguments { get; private set; }

    public uint ArgumentCount { get; private set; }

    private NativeWideStringMarshaller()
    {
    }

    public static NativeWideStringMarshaller Create(
        string sourceName,
        string? entryPoint,
        string profile,
        IReadOnlyList<string> arguments)
    {
        NativeWideStringMarshaller marshaller = new NativeWideStringMarshaller();
        try
        {
            marshaller.SourceName = marshaller.AllocWide(sourceName);
            marshaller.EntryPoint = marshaller.AllocWide(entryPoint);
            marshaller.Profile = marshaller.AllocWide(profile);
            marshaller.Arguments = marshaller.AllocArgv(arguments, out uint argumentCount);
            marshaller.ArgumentCount = argumentCount;
            return marshaller;
        }
        catch
        {
            marshaller.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        for (int i = _allocations.Count - 1; i >= 0; i--)
        {
            nint allocation = _allocations[i];
            if (allocation != 0)
            {
                Marshal.FreeHGlobal(allocation);
            }
        }

        _allocations.Clear();
        SourceName = 0;
        EntryPoint = 0;
        Profile = 0;
        Arguments = 0;
        ArgumentCount = 0;
        _disposed = true;
    }

    private nint AllocArgv(IReadOnlyList<string> arguments, out uint argumentCount)
    {
        argumentCount = (uint)arguments.Count;
        if (argumentCount == 0)
        {
            return 0;
        }

        nint argv = Marshal.AllocHGlobal(IntPtr.Size * (int)argumentCount);
        _allocations.Add(argv);

        for (int i = 0; i < argumentCount; i++)
        {
            string argument = arguments[i] ?? string.Empty;
            nint argPtr = AllocWide(argument);
            Marshal.WriteIntPtr(argv, i * IntPtr.Size, argPtr);
        }

        return argv;
    }

    private nint AllocWide(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        return OperatingSystem.IsWindows()
            ? AllocUtf16(value)
            : AllocUtf32(value);
    }

    private nint AllocUtf16(string value)
    {
        int unitCount = value.Length + 1;
        nint memory = Marshal.AllocHGlobal(unitCount * sizeof(char));
        _allocations.Add(memory);

        unsafe
        {
            Span<char> destination = new Span<char>((void*)memory, unitCount);
            value.AsSpan().CopyTo(destination);
            destination[value.Length] = '\0';
        }

        return memory;
    }

    private nint AllocUtf32(string value)
    {
        List<int> codePoints = new List<int>(value.Length + 1);
        foreach (Rune rune in value.EnumerateRunes())
        {
            codePoints.Add(rune.Value);
        }

        codePoints.Add(0);

        nint memory = Marshal.AllocHGlobal(codePoints.Count * sizeof(int));
        _allocations.Add(memory);
        Marshal.Copy(codePoints.ToArray(), 0, memory, codePoints.Count);
        return memory;
    }
}
