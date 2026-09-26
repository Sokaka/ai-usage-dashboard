using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class ThemeAppIconTests
{
	private static readonly int[] ExpectedFrameSizes = [16, 20, 24, 32, 48, 64];
	private static readonly byte[] PngSignature =
		[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	[Fact]
	public void BundledWindowIconFallback_Decodes()
	{
		RunOnStaThread(() =>
		{
			_ = LoadLogoResources("Palette.xaml");
			Uri resourceUri = new(
				"pack://application:,,,/AiUsageDashboard.App;component/Assets/AppIcon.png",
				UriKind.Absolute);
			BitmapImage image = new(resourceUri);
			Assert.True(image.PixelWidth > 0);
			Assert.True(image.PixelHeight > 0);
		});
	}

	[Theory]
	[InlineData("Palette.xaml")]
	[InlineData("MidnightPalette.xaml")]
	[InlineData("LightPalette.xaml")]
	[InlineData("SakuraPalette.xaml")]
	[InlineData("HighContrastPalette.xaml")]
	public void Create_RendersThemeLogoIntoValidMultiSizeIco(
		string paletteFileName)
	{
		RunOnStaThread(() =>
		{
			(Style style, ResourceDictionary palette) =
				LoadLogoResources(paletteFileName);
			using ThemeAppIcon icon = ThemeAppIcon.Create(
				style, palette);

			BitmapSource windowIcon = Assert.IsAssignableFrom<BitmapSource>(
				icon.WindowIcon);
			Assert.Equal(64, windowIcon.PixelWidth);
			Assert.Equal(64, windowIcon.PixelHeight);
			Assert.True(windowIcon.IsFrozen);
			byte[] windowPixels = GetPixels(windowIcon);
			Assert.Contains(
				windowPixels.Where((_, index) => index % 4 == 3),
				alpha => alpha > 0);

			using MemoryStream stream = new();
			icon.TrayIcon.Save(stream);
			byte[] ico = stream.ToArray();
			Assert.Equal((ushort)0, ReadUInt16(ico, 0));
			Assert.Equal((ushort)1, ReadUInt16(ico, 2));
			Assert.Equal((ushort)ExpectedFrameSizes.Length,
				ReadUInt16(ico, 4));

			int expectedOffset = 6 + (16 * ExpectedFrameSizes.Length);
			for (int index = 0; index < ExpectedFrameSizes.Length; index++)
			{
				int entryOffset = 6 + (16 * index);
				int size = ExpectedFrameSizes[index];
				Assert.Equal(size, ico[entryOffset]);
				Assert.Equal(size, ico[entryOffset + 1]);
				Assert.Equal((ushort)1, ReadUInt16(ico, entryOffset + 4));
				Assert.Equal((ushort)32, ReadUInt16(ico, entryOffset + 6));

				int length = checked((int)ReadUInt32(ico, entryOffset + 8));
				int offset = checked((int)ReadUInt32(ico, entryOffset + 12));
				Assert.Equal(expectedOffset, offset);
				Assert.True(length > 24);
				Assert.True(offset + length <= ico.Length);
				Assert.True(ico.AsSpan(offset, 8).SequenceEqual(PngSignature));
				Assert.Equal(size, (int)BinaryPrimitives.ReadUInt32BigEndian(
					ico.AsSpan(offset + 16, 4)));
				Assert.Equal(size, (int)BinaryPrimitives.ReadUInt32BigEndian(
					ico.AsSpan(offset + 20, 4)));
				expectedOffset += length;
			}

			Assert.Equal(ico.Length, expectedOffset);
		});
	}

	[Fact]
	public void Create_UsesThemeResourcesAndDisposeReleasesTrayIcon()
	{
		RunOnStaThread(() =>
		{
		(Style classicStyle, ResourceDictionary classicPalette) =
			LoadLogoResources("Palette.xaml");
			using ThemeAppIcon classic = ThemeAppIcon.Create(
				classicStyle, classicPalette);
			(Style sakuraStyle, ResourceDictionary sakuraPalette) =
			LoadLogoResources("SakuraPalette.xaml");
			ThemeAppIcon sakura = ThemeAppIcon.Create(
				sakuraStyle, sakuraPalette);
			try
			{
				Assert.False(GetPixels(classic.WindowIcon).AsSpan()
					.SequenceEqual(GetPixels(sakura.WindowIcon)));
				Assert.NotEqual(nint.Zero, sakura.TrayIcon.Handle);
			}
			finally
			{
				sakura.Dispose();
				sakura.Dispose();
			}

			Assert.Throws<ObjectDisposedException>(() => _ = sakura.TrayIcon);
		});
	}

	private static (Style Style, ResourceDictionary Palette)
		LoadLogoResources(string paletteFileName)
	{
		ResourceDictionary controls =
			(ResourceDictionary)Application.LoadComponent(new Uri(
				"/AiUsageDashboard.App;component/Themes/Controls.xaml",
				UriKind.Relative));
		ResourceDictionary palette =
			(ResourceDictionary)Application.LoadComponent(new Uri(
				$"/AiUsageDashboard.App;component/Themes/{paletteFileName}",
				UriKind.Relative));
		Style logoStyle = Assert.IsType<Style>(controls["ThemeLogoStyle"]);
		return (logoStyle, palette);
	}

	private static byte[] GetPixels(ImageSource source)
	{
		BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(source);
		byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
		bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
		return pixels;
	}

	private static ushort ReadUInt16(byte[] bytes, int offset) =>
		BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

	private static uint ReadUInt32(byte[] bytes, int offset) =>
		BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

	private static void RunOnStaThread(Action action)
	{
		Exception? failure = null;
		Thread thread = new(() =>
		{
			try
			{
				action();
			}
			catch (Exception exception)
			{
				failure = exception;
			}
		});
		thread.IsBackground = true;
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert.True(thread.Join(TimeSpan.FromSeconds(10)),
			"Theme icon STA action exceeded ten seconds.");
		if (failure is not null)
		{
			ExceptionDispatchInfo.Capture(failure).Throw();
		}
	}
}
