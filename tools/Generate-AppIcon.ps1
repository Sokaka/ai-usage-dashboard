Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $repositoryRoot "src\AiUsageDashboard.App\Assets"
$appIconSourcePath = Join-Path $assetsDirectory "AppIcon.source.png"
$appIconPath = Join-Path $assetsDirectory "AppIcon.png"
$glyphSourcePath = Join-Path $assetsDirectory "AppIconGlyph.source.png"
$glyphPath = Join-Path $assetsDirectory "AppIconGlyph.png"
$windowsIconPath = Join-Path $assetsDirectory "AiUsageDashboard.ico"

if (-not (Test-Path -LiteralPath $glyphSourcePath))
{
	throw "The icon glyph source was not found: $glyphSourcePath"
}

function New-ScaledBitmap
{
	param([System.Drawing.Image] $Source, [int] $Size)

	$bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
	$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

	try
	{
		$graphics.Clear([System.Drawing.Color]::Transparent)
		$graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
		$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
		$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
		$graphics.DrawImage($Source, 0, 0, $Size, $Size)
		return $bitmap
	}
	catch
	{
		$bitmap.Dispose()
		throw
	}
	finally
	{
		$graphics.Dispose()
	}
}

function New-AppIconBitmap
{
	param([System.Drawing.Image] $Glyph, [int] $Size)

	$supersample = if ($Size -le 256) { 4 } else { 2 }
	$renderSize = $Size * $supersample
	$renderBitmap = [System.Drawing.Bitmap]::new($renderSize, $renderSize)
	$graphics = [System.Drawing.Graphics]::FromImage($renderBitmap)
	$backgroundBrush = [System.Drawing.SolidBrush]::new(
		[System.Drawing.Color]::FromArgb(0xFF, 0x08, 0x0F, 0x28))
	$borderTargetWidth = [Math]::Max(1.0, $Size / 56.0)
	$borderPen = [System.Drawing.Pen]::new(
		[System.Drawing.Color]::FromArgb(0xB3, 0x38, 0xBD, 0xF8),
		[single] ($borderTargetWidth * $supersample))

	try
	{
		$graphics.Clear([System.Drawing.Color]::Transparent)
		$graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
		$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
		$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
		$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
		$borderPen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset

		$outerBounds = [System.Drawing.RectangleF]::new(0, 0, $renderSize - 1, $renderSize - 1)
		$graphics.FillEllipse($backgroundBrush, $outerBounds)
		$graphics.DrawEllipse($borderPen, $outerBounds)

		$glyphSize = $renderSize * (46.0 / 56.0)
		$glyphOffset = ($renderSize - $glyphSize) / 2.0
		$glyphBounds = [System.Drawing.RectangleF]::new(
			[float] $glyphOffset,
			[float] $glyphOffset,
			[float] $glyphSize,
			[float] $glyphSize)
		$graphics.DrawImage($Glyph, $glyphBounds)

		return New-ScaledBitmap -Source $renderBitmap -Size $Size
	}
	finally
	{
		$borderPen.Dispose()
		$backgroundBrush.Dispose()
		$graphics.Dispose()
		$renderBitmap.Dispose()
	}
}

function Convert-ToPngBytes
{
	param([System.Drawing.Bitmap] $Bitmap)

	$stream = [System.IO.MemoryStream]::new()

	try
	{
		$Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
		return ,$stream.ToArray()
	}
	finally
	{
		$stream.Dispose()
	}
}

function New-WindowsIconBytes
{
	param([System.Drawing.Image] $Glyph, [int[]] $Sizes)

	$frames = foreach ($size in $Sizes)
	{
		$bitmap = New-AppIconBitmap -Glyph $Glyph -Size $size

		try
		{
			[PSCustomObject]@{ Size = $size; Bytes = [byte[]](Convert-ToPngBytes $bitmap) }
		}
		finally
		{
			$bitmap.Dispose()
		}
	}

	$stream = [System.IO.MemoryStream]::new()
	$writer = [System.IO.BinaryWriter]::new($stream)

	try
	{
		$writer.Write([uint16] 0)
		$writer.Write([uint16] 1)
		$writer.Write([uint16] $frames.Count)
		$offset = 6 + (16 * $frames.Count)

		foreach ($frame in $frames)
		{
			$dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
			$writer.Write([byte] $dimension)
			$writer.Write([byte] $dimension)
			$writer.Write([byte] 0)
			$writer.Write([byte] 0)
			$writer.Write([uint16] 1)
			$writer.Write([uint16] 32)
			$writer.Write([uint32] $frame.Bytes.Length)
			$writer.Write([uint32] $offset)
			$offset += $frame.Bytes.Length
		}

		foreach ($frame in $frames)
		{
			$writer.Write([byte[]] $frame.Bytes)
		}

		$writer.Flush()
		return ,$stream.ToArray()
	}
	finally
	{
		$writer.Dispose()
		$stream.Dispose()
	}
}

$glyph = [System.Drawing.Image]::FromFile($glyphSourcePath)

try
{
	$glyphBitmap = New-ScaledBitmap -Source $glyph -Size 256
	$appIconSource = New-AppIconBitmap -Glyph $glyph -Size 1024
	$appIcon = New-AppIconBitmap -Glyph $glyph -Size 256

	try
	{
		$glyphBitmap.Save($glyphPath, [System.Drawing.Imaging.ImageFormat]::Png)
		$appIconSource.Save($appIconSourcePath, [System.Drawing.Imaging.ImageFormat]::Png)
		$appIcon.Save($appIconPath, [System.Drawing.Imaging.ImageFormat]::Png)
		[System.IO.File]::WriteAllBytes(
			$windowsIconPath,
			[byte[]](New-WindowsIconBytes $glyph @(16, 20, 24, 32, 40, 48, 64, 128, 256)))
	}
	finally
	{
		$appIcon.Dispose()
		$appIconSource.Dispose()
		$glyphBitmap.Dispose()
	}
}
finally
{
	$glyph.Dispose()
}

Write-Host "Generated app icon assets in $assetsDirectory"
