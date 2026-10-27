using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Mullion.App.Interop;
using Mullion.App.Services;
using Mullion.Core;

namespace Mullion.App.UI;

/// <summary>
/// A small topmost frame showing a live DWM thumbnail of another window. Clicking the thumbnail brings
/// the original window to the front and closes this frame. The source window is left untouched.
/// </summary>
public partial class PipWindow : Window
{
    private const int CaptionDip = 22;
    private const int PaddingDip = 3;

    private nint _hwnd;
    private nint _thumbnail;

    internal PipWindow(nint source, string title)
    {
        Source = source;
        InitializeComponent();
        TitleText.Text = title;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateThumbnail();
        Closed += OnClosed;
    }

    public nint Source { get; }

    public nint Handle => _hwnd;

    public bool ThumbnailRegistered => _thumbnail != 0;

    /// <summary>HRESULT of DwmRegisterThumbnail (0 = S_OK).</summary>
    public int RegisterResult { get; private set; }

    /// <summary>Where the thumbnail was last placed, in physical client pixels.</summary>
    public PixelRect ThumbnailRect { get; private set; }

    /// <summary>Raised when the user asks for the original window (click on the thumbnail or the Open button).</summary>
    public event Action<PipWindow>? OpenRequested;

    /// <summary>Sizes the frame to a quarter of the work area (keeping the source aspect) and parks it bottom-right.</summary>
    public void PlaceInitial()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var sourceFrame = Win32WindowOps.GetFrameBounds(Source);
        var work = Win32WindowOps.GetWorkArea(Source);
        if (work.IsEmpty)
        {
            work = Win32WindowOps.GetWorkArea(_hwnd);
        }

        double scale = Win32WindowOps.GetDpi(Source) / 96.0;
        int caption = (int)Math.Round(CaptionDip * scale);
        var (width, height) = ThumbnailLayout.InitialSize(sourceFrame.Width, sourceFrame.Height, work);
        height += caption;
        int margin = (int)Math.Round(16 * scale);
        int x = Math.Max(work.Left, work.Right - width - margin);
        int y = Math.Max(work.Top, work.Bottom - height - margin);

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        UpdateThumbnail();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Keep it out of Alt-Tab; it is a companion of the source window, not an app of its own.
        nint exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle | (nint)WindowStyles.WS_EX_TOOLWINDOW);

        RegisterResult = NativeMethods.DwmRegisterThumbnail(_hwnd, Source, out _thumbnail);
        if (RegisterResult != 0)
        {
            Diagnostics.Log($"DwmRegisterThumbnail failed for 0x{Source:X}: HRESULT 0x{RegisterResult:X8}");
            _thumbnail = 0;
            return;
        }

        UpdateThumbnail();
    }

    private void UpdateThumbnail()
    {
        if (_thumbnail == 0 || _hwnd == 0)
        {
            return;
        }

        var client = Win32WindowOps.GetClientSize(_hwnd);
        double scale = Win32WindowOps.GetDpi(_hwnd) / 96.0;
        int caption = (int)Math.Round(CaptionDip * scale);
        int pad = (int)Math.Round(PaddingDip * scale);

        int areaWidth = client.Width - 2 * pad;
        int areaHeight = client.Height - caption - 2 * pad;
        if (areaWidth <= 0 || areaHeight <= 0)
        {
            return;
        }

        int sourceWidth = 0;
        int sourceHeight = 0;
        if (NativeMethods.DwmQueryThumbnailSourceSize(_thumbnail, out var sourceSize) == 0)
        {
            sourceWidth = sourceSize.Width;
            sourceHeight = sourceSize.Height;
        }

        var fit = ThumbnailLayout.Fit(sourceWidth, sourceHeight, areaWidth, areaHeight);
        ThumbnailRect = new PixelRect(pad + fit.Left, caption + pad + fit.Top, pad + fit.Right, caption + pad + fit.Bottom);

        var properties = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            Flags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
            Destination = new NativeMethods.RECT
            {
                Left = ThumbnailRect.Left,
                Top = ThumbnailRect.Top,
                Right = ThumbnailRect.Right,
                Bottom = ThumbnailRect.Bottom,
            },
            Opacity = byte.MaxValue,
            Visible = true,
            SourceClientAreaOnly = true,
        };

        int hr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        if (hr != 0)
        {
            Diagnostics.Log($"DwmUpdateThumbnailProperties failed: HRESULT 0x{hr:X8}");
        }
    }

    private void OnCaptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was released before the drag could start; nothing to do.
            }
        }
    }

    private void OnThumbnailMouseDown(object sender, MouseButtonEventArgs e) => OpenRequested?.Invoke(this);

    private void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_thumbnail != 0)
        {
            int hr = NativeMethods.DwmUnregisterThumbnail(_thumbnail);
            if (hr != 0)
            {
                Diagnostics.Log($"DwmUnregisterThumbnail failed: HRESULT 0x{hr:X8}");
            }

            _thumbnail = 0;
        }
    }
}
