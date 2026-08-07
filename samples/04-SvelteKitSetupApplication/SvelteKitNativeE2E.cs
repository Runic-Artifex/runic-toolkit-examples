using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Examples.Setup.Contract;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

namespace RunicToolkit.Examples.Setup;

internal static class SvelteKitNativeE2E
{
    internal static async Task<int> RunAsync()
    {
        string? browserPath = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(browserPath) || !File.Exists(browserPath))
        {
            Console.Error.WriteLine("WEBUI_BROWSER_PATH must identify Chrome, Edge, or Chromium.");
            return 1;
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        string profile = Path.Combine(
            Path.GetTempPath(),
            "runic-sveltekit-e2e-" + Guid.NewGuid().ToString("N"));
        await using var session = new ApplicationBridgeSession(
            new SetupBridgeDispatcher(new SetupBridgeHandler()));
        using var window = new WebUiWindow();
        window.SetPublic(false);
        window.SetRootFolder(webRoot);
        await using CsWebUiApplicationBridge bridge = CsWebUiApplicationBridge.Attach(window, session);
        string url = window.StartServer("index.html");
        Directory.CreateDirectory(profile);
        using var browser = CreateBrowser(browserPath, profile, url);

        try
        {
            if (!browser.Start()) throw new InvalidOperationException("The browser did not start.");
            Task<string> diagnostics = browser.StandardError.ReadToEndAsync();
            string result = string.Empty;
            // ExecuteJavaScript and binding responses share CsWebUi's native response path.
            // Let the larger SvelteKit bundle finish its initial bridge handshake before polling.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        result = window.ExecuteJavaScript(
                            InteractionScript,
                            TimeSpan.FromSeconds(1),
                            512);
                        if (result.StartsWith("Complete|5", StringComparison.Ordinal) ||
                            result.Contains("|error ·", StringComparison.Ordinal)) break;
                    }
                    catch (WebUiException) { }
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }

            bool passed = result.StartsWith("Complete|5|", StringComparison.Ordinal);
            Console.WriteLine(passed
                ? "PASS: native browser completed the package-only SvelteKit vertical."
                : $"FAIL: native SvelteKit result was '{result}' (host identity: {bridge.ConnectionIdentity?.ToString() ?? "none"}).");
            if (!browser.HasExited) browser.Kill(true);
            await browser.WaitForExitAsync().ConfigureAwait(false);
            _ = await diagnostics.ConfigureAwait(false);
            return passed ? 0 : 1;
        }
        finally
        {
            WebUiApplication.Exit();
            WebUiApplication.Clean();
            if (!browser.HasExited)
            {
                browser.Kill(true);
                await browser.WaitForExitAsync().ConfigureAwait(false);
            }
            await TryDeleteProfileAsync(profile).ConfigureAwait(false);
        }
    }

    private static Process CreateBrowser(string browserPath, string profile, string url)
    {
        var browser = new Process
        {
            StartInfo =
            {
                FileName = browserPath,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (string argument in new[]
        {
            "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
            "--disable-background-networking", "--disable-component-update", "--disable-crash-reporter",
            "--no-first-run", "--remote-debugging-port=0", "--user-data-dir=" + profile, url,
        }) browser.StartInfo.ArgumentList.Add(argument);
        return browser;
    }

    private static async Task TryDeleteProfileAsync(string profile)
    {
        for (int attempt = 0; Directory.Exists(profile) && attempt < 10; attempt++)
        {
            try { Directory.Delete(profile, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private const string InteractionScript = """
        const root = document.querySelector('[data-e2e-view]');
        if (!root) return `loading|${document.readyState}|${globalThis.__runicBootError || ''}|${document.body.innerText.slice(0, 160)}`;
        const view = root.getAttribute('data-e2e-view') || 'loading';
        const progress = root.getAttribute('data-e2e-progress') || '0';
        const status = document.querySelector('.status')?.textContent?.trim() || '';
        const error = document.querySelector('.error')?.textContent?.trim() || '';
        const binding = typeof globalThis.__runicToolkit_applicationBridge_send;
        const webui = typeof globalThis.webui;
        const click = (name) => {
          const button = document.querySelector(`[data-e2e="${name}"]`);
          if (button && !button.disabled) button.click();
        };
        if (view === 'Welcome') click('next');
        else if (view === 'Destination') {
          const text = document.querySelector('section p')?.textContent || '';
          click(text.includes('No destination') ? 'select' : 'next');
        } else if (view === 'Features') click('install');
        return `${view}|${progress}|${status}|${error}|binding:${binding}|webui:${webui}`;
        """;
}
