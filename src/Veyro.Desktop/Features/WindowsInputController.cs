using System.Runtime.InteropServices;
using System.IO;
using Veyro.Protocol;

namespace Veyro.Desktop.Features;

public sealed class WindowsInputController
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint MouseWheel = 0x0800;
    private const uint MouseVirtualDesktop = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private bool penContact;

    public void Apply(RemoteInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch (input.InputCommand)
        {
            case RemoteInputCommand.MouseDelta:
                ValidateDelta(input.DeltaAxisX, input.DeltaAxisY, 10_000);
                InjectMouse(
                    checked((int)Math.Round(input.DeltaAxisX)),
                    checked((int)Math.Round(input.DeltaAxisY)),
                    0,
                    MouseMove);
                break;
            case RemoteInputCommand.SingleTap:
                Click(1);
                break;
            case RemoteInputCommand.DoubleTap:
                Click(2);
                break;
            case RemoteInputCommand.ScrollGesture:
                ValidateDelta(input.DeltaAxisX, input.DeltaAxisY, 100);
                InjectMouse(0, 0, unchecked((uint)(int)Math.Round(input.DeltaAxisY * 120)), MouseWheel);
                break;
            case RemoteInputCommand.KeyboardInput:
                InjectText(input.KeyboardChar);
                break;
            case RemoteInputCommand.StylusEvent:
                InjectStylus(input);
                break;
            default:
                throw new InvalidDataException("O comando de entrada remota não é suportado.");
        }
    }

    private static void Click(int count)
    {
        for (var index = 0; index < count; index++)
        {
            Send(
                new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = LeftDown } } },
                new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = LeftUp } } });
        }
    }

    private static void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 8 || text.Any(char.IsControl))
        {
            throw new InvalidDataException("A entrada de teclado contém texto inválido.");
        }

        foreach (var character in text)
        {
            Send(
                new Input
                {
                    Type = InputKeyboard,
                    Data = new InputUnion
                    {
                        Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyUnicode }
                    }
                },
                new Input
                {
                    Type = InputKeyboard,
                    Data = new InputUnion
                    {
                        Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyUnicode | KeyUp }
                    }
                });
        }
    }

    private static void ValidateDelta(float x, float y, float maximumAbsoluteValue)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            Math.Abs(x) > maximumAbsoluteValue || Math.Abs(y) > maximumAbsoluteValue)
        {
            throw new InvalidDataException("O deslocamento de entrada remota é inválido.");
        }
    }

    private void InjectStylus(RemoteInputEvent input)
    {
        if (!float.IsFinite(input.NormalizedX) || !float.IsFinite(input.NormalizedY) ||
            input.NormalizedX is < 0 or > 1 || input.NormalizedY is < 0 or > 1 ||
            !float.IsFinite(input.Pressure) || input.Pressure is < 0 or > 1)
        {
            throw new InvalidDataException("As coordenadas da caneta são inválidas.");
        }

        var normalizedX = checked((int)Math.Round(input.NormalizedX * 65535));
        var normalizedY = checked((int)Math.Round(input.NormalizedY * 65535));
        var flags = MouseMove | MouseAbsolute | MouseVirtualDesktop;
        switch (input.StylusAction)
        {
            case StylusAction.StylusDown:
                penContact = true;
                flags |= LeftDown;
                break;
            case StylusAction.StylusMove:
                break;
            case StylusAction.StylusUp:
            case StylusAction.StylusCancel:
                if (penContact)
                {
                    flags |= LeftUp;
                }

                penContact = false;
                break;
            default:
                throw new InvalidDataException("A ação de caneta é inválida.");
        }

        InjectMouse(normalizedX, normalizedY, 0, flags);
    }

    private static void InjectMouse(int x, int y, uint data, uint flags) =>
        Send(
            new Input
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags }
                }
            });

    private static void Send(params Input[] inputs)
    {
        if (SendInput(checked((uint)inputs.Length), inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            throw new InvalidOperationException("O Windows recusou a injeção de entrada remota.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}
