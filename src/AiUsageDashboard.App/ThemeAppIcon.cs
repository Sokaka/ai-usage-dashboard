using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Icon = System.Drawing.Icon;

namespace AiUsageDashboard.App;

internal sealed class ThemeAppIcon : IDisposable
{
	private const int WindowIconSizePixels = 64;
	private static readonly int[] TrayFrameSizesPixels = [16, 20, 24, 32, 48, 64];
	private readonly MemoryStream _trayIconStream;
	private readonly Icon _trayIcon;
	private bool _isDisposed;

	internal ImageSource WindowIcon { get; }

	internal Icon TrayIcon
	{
		get
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			return _trayIcon;
		}
	}

	private ThemeAppIcon(
		ImageSource windowIcon,
		Icon trayIcon,
		MemoryStream trayIconStream)
	{
		WindowIcon = windowIcon;
		_trayIcon = trayIcon;
		_trayIconStream = trayIconStream;
	}

	internal static ThemeAppIcon Create(
		Style logoStyle,
		ResourceDictionary palette)
	{
		ArgumentNullException.ThrowIfNull(logoStyle);
		ArgumentNullException.ThrowIfNull(palette);

		BitmapSource windowIcon = RenderLogo(
			logoStyle,
			palette,
			WindowIconSizePixels);
		List<byte[]> pngFrames = new(TrayFrameSizesPixels.Length);
		foreach (int sizePixels in TrayFrameSizesPixels)
		{
		BitmapSource rendered = sizePixels == WindowIconSizePixels
				? windowIcon
				: RenderLogo(logoStyle, palette, sizePixels);
			pngFrames.Add(EncodePng(rendered));
		}

		MemoryStream? iconStream = null;
		Icon? trayIcon = null;
		try
		{
			iconStream = BuildIco(pngFrames);
			trayIcon = new Icon(iconStream);
			return new ThemeAppIcon(windowIcon, trayIcon, iconStream);
		}
		catch
		{
			trayIcon?.Dispose();
			iconStream?.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		try
		{
			_trayIcon.Dispose();
		}
		finally
		{
			_trayIconStream.Dispose();
		}
	}

	private static BitmapSource RenderLogo(
		Style logoStyle,
		ResourceDictionary palette,
		int sizePixels)
	{
		ContentControl logo = new()
		{
			Style = logoStyle,
			Width = sizePixels,
			Height = sizePixels
		};
		logo.Resources.MergedDictionaries.Add(palette);
		System.Windows.Size size = new(sizePixels, sizePixels);
		logo.Measure(size);
		logo.Arrange(new Rect(size));
		logo.UpdateLayout();

		RenderTargetBitmap bitmap = new(sizePixels, sizePixels, 96, 96,
			PixelFormats.Pbgra32);
		bitmap.Render(logo);
		bitmap.Freeze();
		return bitmap;
	}

	private static byte[] EncodePng(BitmapSource bitmap)
	{
		PngBitmapEncoder encoder = new();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using MemoryStream stream = new();
		encoder.Save(stream);
		return stream.ToArray();
	}

	private static MemoryStream BuildIco(IReadOnlyList<byte[]> pngFrames)
	{
		MemoryStream stream = new();
		try
		{
			using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
			writer.Write((ushort)0);
			writer.Write((ushort)1);
			writer.Write((ushort)pngFrames.Count);

			int frameOffset = checked(6 + (16 * pngFrames.Count));
			for (int index = 0; index < pngFrames.Count; index++)
			{
				int sizePixels = TrayFrameSizesPixels[index];
				byte[] pngFrame = pngFrames[index];
				writer.Write((byte)sizePixels);
				writer.Write((byte)sizePixels);
				writer.Write((byte)0);
				writer.Write((byte)0);
				writer.Write((ushort)1);
				writer.Write((ushort)32);
				writer.Write((uint)pngFrame.Length);
				writer.Write((uint)frameOffset);
				frameOffset = checked(frameOffset + pngFrame.Length);
			}

			foreach (byte[] pngFrame in pngFrames)
			{
				writer.Write(pngFrame);
			}

			writer.Flush();
			stream.Position = 0;
			return stream;
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}
}
