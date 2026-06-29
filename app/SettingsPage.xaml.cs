using Floaty.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Floaty;

/// <summary>
/// Native MAUI page that hosts the Blazor <c>Settings</c> component. Opened in its own window
/// from the overlay's ⚙ button; keeps normal window chrome (see WindowsOverlayWindowController).
/// </summary>
public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
		blazorWebView.WebResourceRequested += OnWebResourceRequested;
	}

	private void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e)
	{
		if (!e.Uri.Host.Equals("localfiles", StringComparison.OrdinalIgnoreCase))
			return;

		var segments = e.Uri.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (segments.Length != 2)
		{
			SetNotFound(e);
			return;
		}

		var scope = segments[0];
		var fileName = Path.GetFileName(Uri.UnescapeDataString(segments[1]));

		if (string.IsNullOrWhiteSpace(fileName))
		{
			SetNotFound(e);
			return;
		}

		try
		{
			if (scope.Equals("ring", StringComparison.OrdinalIgnoreCase))
			{
				var filePath = Path.Combine(FloatyPaths.RingImages, fileName);
				if (File.Exists(filePath))
				{
					var bytes = File.ReadAllBytes(filePath);
					SetOk(e, fileName, bytes);
					return;
				}
			}

			if (scope.Equals("builtin", StringComparison.OrdinalIgnoreCase))
			{
				var bytes = TryReadBuiltInImage(fileName);
				if (bytes is not null)
				{
					SetOk(e, fileName, bytes);
					return;
				}
			}
		}
		catch
		{
			// Keep the webview stable if preview loading fails.
		}

		SetNotFound(e);
	}

	private static byte[]? TryReadBuiltInImage(string fileName)
	{
		var candidates = new[]
		{
			fileName,
			Path.Combine("Resources", "Images", fileName).Replace('\\', '/'),
		};

		try
		{
			foreach (var candidate in candidates)
			{
				try
				{
					using var stream = FileSystem.OpenAppPackageFileAsync(candidate).GetAwaiter().GetResult();
					using var memory = new MemoryStream();
					stream.CopyTo(memory);
					return memory.ToArray();
				}
				catch
				{
					// Try the next candidate path.
				}
			}

			return null;
		}
		catch
		{
			return null;
		}
	}

	private static void SetOk(WebViewWebResourceRequestedEventArgs e, string fileName, byte[] bytes)
	{
		e.SetResponse(200, "OK", GetMimeType(fileName), new MemoryStream(bytes));
		e.Handled = true;
	}

	private static void SetNotFound(WebViewWebResourceRequestedEventArgs e)
	{
		e.SetResponse(404, "Not Found", "text/plain", new MemoryStream());
		e.Handled = true;
	}

	private static string GetMimeType(string fileName)
	{
		var extension = Path.GetExtension(fileName).ToLowerInvariant();
		return extension switch
		{
			".jpg" or ".jpeg" => "image/jpeg",
			".png" => "image/png",
			".gif" => "image/gif",
			".webp" => "image/webp",
			".svg" => "image/svg+xml",
			".bmp" => "image/bmp",
			_ => "application/octet-stream",
		};
	}
}
