#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property OutputType=WinExe
#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

sealed class RenderWindow : Form
{
    private const int OwnDeviceContext = 0x0020;
    private const int ClipChildren = 0x02000000;
    private const int ClipSiblings = 0x04000000;
    private const uint WindowOpenGlDoubleBuffer = 0x00000004 | 0x00000020 | 0x00000001;
    private const uint GenericFormat = 0x00000040;
    private const uint GenericAccelerated = 0x00001000;
    private const uint ColorBufferBit = 0x00004000;
    private const ushort PixelFormatDescriptorSize = 40;
    private nint deviceContext;
    private nint renderContext;

    [STAThread]
    private static int Main()
    {
        using var window = new RenderWindow
        {
            Text = "Test FPS · synthetic workload",
            ClientSize = new(640, 360),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen
        };
        if (!window.InitializeGraphics()) return 1;

        int exitCode = 0;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 17 };
        timer.Tick += (_, _) =>
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(180))
            {
                window.Close();
                return;
            }

            if (window.WindowState == FormWindowState.Minimized) return;
            if (window.Render((float)elapsed.Elapsed.TotalSeconds)) return;
            exitCode = 2;
            window.Close();
        };
        timer.Start();
        Application.Run(window);
        return exitCode;
    }

    private RenderWindow() => SetStyle(ControlStyles.Opaque, true);

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= OwnDeviceContext;
            parameters.Style |= ClipChildren | ClipSiblings;
            return parameters;
        }
    }

    private bool InitializeGraphics()
    {
        if (Marshal.SizeOf<PixelFormatDescriptor>() != PixelFormatDescriptorSize
            || Marshal.OffsetOf<PixelFormatDescriptor>(nameof(PixelFormatDescriptor.LayerMask)) != 28)
            return false;

        deviceContext = GetDC(Handle);
        if (deviceContext == 0) return false;

        var requested = new PixelFormatDescriptor
        {
            Size = PixelFormatDescriptorSize,
            Version = 1,
            Flags = WindowOpenGlDoubleBuffer,
            ColorBits = 24
        };
        int format = ChoosePixelFormat(deviceContext, in requested);
        if (format == 0 || DescribePixelFormat(deviceContext, format, PixelFormatDescriptorSize, out var selected) == 0)
            return false;
        if ((selected.Flags & WindowOpenGlDoubleBuffer) != WindowOpenGlDoubleBuffer
            || (selected.Flags & (GenericFormat | GenericAccelerated)) == GenericFormat)
            return false;
        if (SetPixelFormat(deviceContext, format, in selected) == 0) return false;

        renderContext = wglCreateContext(deviceContext);
        return renderContext != 0 && wglMakeCurrent(deviceContext, renderContext) != 0;
    }

    private bool Render(float seconds)
    {
        if (renderContext == 0) return false;

        glClearColor(0.09f, 0.24f, 0.4f + 0.2f * MathF.Sin(seconds * 2), 1f);
        glClear(ColorBufferBit);
        return SwapBuffers(deviceContext) != 0;
    }

    protected override void DestroyHandle()
    {
        if (renderContext != 0)
        {
            wglMakeCurrent(0, 0);
            wglDeleteContext(renderContext);
            renderContext = 0;
        }
        // Windows frees the private CS_OWNDC device context with its window.
        deviceContext = 0;
        base.DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort Size, Version;
        public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift;
        public byte AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits;
        public byte DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDC(nint window);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int ChoosePixelFormat(nint dc, in PixelFormatDescriptor descriptor);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DescribePixelFormat(nint dc, int format, uint size, out PixelFormatDescriptor descriptor);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int SetPixelFormat(nint dc, int format, in PixelFormatDescriptor descriptor);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int SwapBuffers(nint dc);

    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern int wglMakeCurrent(nint dc, nint context);
    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern int wglDeleteContext(nint context);
    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern void glClearColor(float red, float green, float blue, float alpha);
    [DllImport("opengl32.dll", ExactSpelling = true)]
    private static extern void glClear(uint mask);
}
