using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TabScatter;

public partial class MainWindow : Window
{
    private readonly WebView2[] _browsers;
    private readonly Button[] _openButtons;
    private readonly Button[] _backButtons;
    private readonly Button[] _forwardButtons;
    private readonly List<(WebView2 Browser, Button Button)> _bookmarkControls = [];
    private readonly List<WebView2> _elementBlockBrowsers = [];
    private readonly SemaphoreSlim _bookmarkFileLock = new(1, 1);
    private readonly SemaphoreSlim _elementBlockFileLock = new(1, 1);
    private readonly Dictionary<string, List<string>> _blockedElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WebView2, (string Token, string Host)> _elementPickerSessions = [];
    private readonly string?[] _currentLinks = new string?[4];
    private CoreWebView2Environment? _environment;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        _browsers = [Browser1, Browser2, Browser3, Browser4];
        _openButtons = [OpenButton1, OpenButton2, OpenButton3, OpenButton4];
        _backButtons = [BackButton1, BackButton2, BackButton3, BackButton4];
        _forwardButtons = [ForwardButton1, ForwardButton2, ForwardButton3, ForwardButton4];
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(AppContext.BaseDirectory, "WebView2UserData");
            _environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);

            try
            {
                await LoadBlockedElementsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not load blocked-element rules from BlockedElements.json.\n\n{ex.Message}",
                    "Element Blocker Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            for (var i = 0; i < _browsers.Length; i++)
            {
                var browser = _browsers[i];
                await browser.EnsureCoreWebView2Async(_environment);
                ConfigureNewWindowHandling(browser);
                ConfigureNavigationButtons(browser, _backButtons[i], _forwardButtons[i]);
                ConfigureElementBlocking(browser);
            }

            _ready = true;
            await LoadRandomLinksAsync();
            await UpdateBookmarkButtonsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"The embedded browser could not be initialized. Make sure the Microsoft Edge WebView2 Runtime is installed.\n\n{ex.Message}",
                "TabScatter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            await LoadRandomLinksAsync();
        }
    }

    private async Task LoadRandomLinksAsync()
    {
        try
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "Links.txt");
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Links.txt was not found beside the application.", filePath);
            }

            var links = (await File.ReadAllLinesAsync(filePath))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToArray();

            var invalidLinks = links.Where(link =>
                !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                .ToArray();

            if (invalidLinks.Length > 0)
            {
                throw new InvalidDataException(
                    $"Links.txt contains invalid or unsupported links:\n{string.Join("\n", invalidLinks)}");
            }

            if (links.Length == 0)
            {
                throw new InvalidDataException("Links.txt does not contain any HTTP or HTTPS links.");
            }

            var shuffled = links.ToArray();
            for (var i = shuffled.Length - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            for (var i = 0; i < _browsers.Length; i++)
            {
                _currentLinks[i] = i < shuffled.Length ? shuffled[i] : null;
                _openButtons[i].IsEnabled = _currentLinks[i] is not null;
                _browsers[i].Source = _currentLinks[i] is { } link
                    ? new Uri(link)
                    : new Uri("about:blank");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not load links from Links.txt.\n\n{ex.Message}",
                "TabScatter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), out var index) ||
            index < 0 ||
            index >= _currentLinks.Length ||
            _currentLinks[index] is not { } link)
        {
            return;
        }

        await OpenInAppTabAsync(link);
    }

    private async void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.Tag is not WebView2 browser ||
            browser.CoreWebView2 is null ||
            !Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out var currentUri) ||
            currentUri is null ||
            !IsHttpUrl(currentUri))
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _bookmarkFileLock.WaitAsync();
            try
            {
                var filePath = Path.Combine(AppContext.BaseDirectory, "Links.txt");
                var savedLinks = await File.ReadAllLinesAsync(filePath);
                if (!savedLinks.Any(savedLink => IsSameHttpUrl(savedLink, currentUri)))
                {
                    var existingText = await File.ReadAllTextAsync(filePath);
                    var separator = existingText.Length > 0 &&
                                    !existingText.EndsWith('\n') &&
                                    !existingText.EndsWith('\r')
                        ? Environment.NewLine
                        : string.Empty;
                    await File.AppendAllTextAsync(
                        filePath,
                        $"{separator}{currentUri.AbsoluteUri}{Environment.NewLine}");
                }
            }
            finally
            {
                _bookmarkFileLock.Release();
            }

            await UpdateBookmarkButtonsAsync();
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            MessageBox.Show(
                this,
                $"Could not save the URL to Links.txt.\n\n{ex.Message}",
                "Bookmark Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void NavigateBack_Click(object sender, RoutedEventArgs e)
    {
        if (GetPanelBrowser(sender) is { CoreWebView2.CanGoBack: true } browser)
        {
            browser.CoreWebView2.GoBack();
        }
    }

    private void NavigateForward_Click(object sender, RoutedEventArgs e)
    {
        if (GetPanelBrowser(sender) is { CoreWebView2.CanGoForward: true } browser)
        {
            browser.CoreWebView2.GoForward();
        }
    }

    private WebView2? GetPanelBrowser(object sender)
    {
        return sender is Button button &&
               int.TryParse(button.Tag?.ToString(), out var index) &&
               index >= 0 &&
               index < _browsers.Length
            ? _browsers[index]
            : null;
    }

    private void ConfigureBookmarkButton(WebView2 browser, Button button)
    {
        button.Tag = browser;
        _bookmarkControls.Add((browser, button));
        browser.CoreWebView2.NavigationStarting += (_, _) => button.IsEnabled = false;
        browser.CoreWebView2.NavigationCompleted += async (_, _) => await UpdateBookmarkButtonsAsync();
    }

    private async Task UpdateBookmarkButtonsAsync()
    {
        try
        {
            var savedLinks = await File.ReadAllLinesAsync(
                Path.Combine(AppContext.BaseDirectory, "Links.txt"));
            foreach (var (browser, button) in _bookmarkControls)
            {
                if (!Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var currentUri) ||
                    currentUri is null ||
                    !IsHttpUrl(currentUri))
                {
                    button.IsEnabled = false;
                    continue;
                }

                button.IsEnabled = !savedLinks.Any(savedLink => IsSameHttpUrl(savedLink, currentUri));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not check URLs in Links.txt.\n\n{ex.Message}",
                "Bookmark Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool IsSameHttpUrl(string savedLink, Uri currentUri)
    {
        return Uri.TryCreate(savedLink.Trim(), UriKind.Absolute, out var savedUri) &&
               savedUri is not null &&
               IsHttpUrl(savedUri) &&
               savedUri.Equals(currentUri);
    }

    private static bool IsHttpUrl(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private string BlockedElementsPath => Path.Combine(AppContext.BaseDirectory, "BlockedElements.json");

    private async Task LoadBlockedElementsAsync()
    {
        if (!File.Exists(BlockedElementsPath))
        {
            return;
        }

        var savedRules = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            await File.ReadAllTextAsync(BlockedElementsPath));
        if (savedRules is null)
        {
            throw new InvalidDataException("BlockedElements.json does not contain a valid rule list.");
        }

        foreach (var (host, selectors) in savedRules)
        {
            _blockedElements[host.ToLowerInvariant()] = selectors
                .Where(selector => !string.IsNullOrWhiteSpace(selector))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    private void ConfigureElementBlocking(WebView2 browser)
    {
        var environment = _environment;
        var coreWebView2 = browser.CoreWebView2;
        if (environment is null || coreWebView2 is null)
        {
            return;
        }

        _elementBlockBrowsers.Add(browser);
        coreWebView2.ContextMenuRequested += (_, args) =>
        {
            if (!Uri.TryCreate(coreWebView2.Source, UriKind.Absolute, out var pageUri) ||
                pageUri is null ||
                !IsHttpUrl(pageUri))
            {
                return;
            }

            var blockItem = environment.CreateContextMenuItem(
                "Block element on this site",
                null,
                CoreWebView2ContextMenuItemKind.Command);
            blockItem.CustomItemSelected += async (_, _) =>
            {
                try
                {
                    await StartElementPickerAsync(browser);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        $"Could not start the element picker.\n\n{ex.Message}",
                        "Element Blocker Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
            args.MenuItems.Insert(0, blockItem);

            if (_blockedElements.TryGetValue(GetHostKey(pageUri), out var selectors) &&
                selectors.Count > 0)
            {
                var clearItem = environment.CreateContextMenuItem(
                    "Clear blocked elements on this site",
                    null,
                    CoreWebView2ContextMenuItemKind.Command);
                clearItem.CustomItemSelected += async (_, _) =>
                {
                    try
                    {
                        await ClearBlockedElementsAsync(browser);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            this,
                            $"Could not clear blocked-element rules.\n\n{ex.Message}",
                            "Element Blocker Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                };
                args.MenuItems.Insert(1, clearItem);
            }
        };

        coreWebView2.WebMessageReceived += async (_, args) =>
        {
            try
            {
                await HandleElementPickerMessageAsync(browser, args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not save the blocked-element rule.\n\n{ex.Message}",
                    "Element Blocker Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
        coreWebView2.NavigationStarting += (_, _) => _elementPickerSessions.Remove(browser);
        coreWebView2.NavigationCompleted += async (_, _) =>
        {
            try
            {
                await ApplyBlockedElementsAsync(browser);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not apply blocked-element rules.\n\n{ex.Message}",
                    "Element Blocker Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
    }

    private async Task StartElementPickerAsync(WebView2 browser)
    {
        if (!Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var pageUri) ||
            pageUri is null ||
            !IsHttpUrl(pageUri))
        {
            return;
        }

        var token = Guid.NewGuid().ToString("N");
        _elementPickerSessions[browser] = (token, GetHostKey(pageUri));
        var script = $"({ElementPickerScript})({JsonSerializer.Serialize(token)})";
        await browser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task HandleElementPickerMessageAsync(
        WebView2 browser,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!_elementPickerSessions.TryGetValue(browser, out var session) ||
            !Uri.TryCreate(args.Source, UriKind.Absolute, out var sourceUri) ||
            sourceUri is null ||
            !string.Equals(GetHostKey(sourceUri), session.Host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        JsonDocument message;
        try
        {
            message = JsonDocument.Parse(args.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (message)
        {
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "blockElement" ||
                !root.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String ||
                token.GetString() != session.Token ||
                !root.TryGetProperty("selector", out var selectorElement) ||
                selectorElement.ValueKind != JsonValueKind.String ||
                selectorElement.GetString() is not { Length: > 0 and <= 2048 } selector ||
                selector.Contains('{') ||
                selector.Contains('}'))
            {
                return;
            }

            _elementPickerSessions.Remove(browser);
            await _elementBlockFileLock.WaitAsync();
            try
            {
                if (!_blockedElements.TryGetValue(session.Host, out var selectors))
                {
                    selectors = [];
                    _blockedElements[session.Host] = selectors;
                }

                if (!selectors.Contains(selector, StringComparer.Ordinal))
                {
                    selectors.Add(selector);
                    try
                    {
                        await SaveBlockedElementsAsync();
                    }
                    catch
                    {
                        selectors.Remove(selector);
                        if (selectors.Count == 0)
                        {
                            _blockedElements.Remove(session.Host);
                        }

                        throw;
                    }
                }
            }
            finally
            {
                _elementBlockFileLock.Release();
            }

            await ApplyBlockedElementsToAllBrowsersAsync();
        }
    }

    private async Task ClearBlockedElementsAsync(WebView2 browser)
    {
        if (!Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var pageUri) ||
            pageUri is null)
        {
            return;
        }

        await _elementBlockFileLock.WaitAsync();
        List<string>? removedSelectors = null;
        try
        {
            var host = GetHostKey(pageUri);
            if (!_blockedElements.Remove(host, out removedSelectors))
            {
                return;
            }

            try
            {
                await SaveBlockedElementsAsync();
            }
            catch
            {
                _blockedElements[host] = removedSelectors;
                throw;
            }
        }
        finally
        {
            _elementBlockFileLock.Release();
        }

        if (removedSelectors is not null)
        {
            await ApplyBlockedElementsToAllBrowsersAsync();
        }
    }

    private async Task SaveBlockedElementsAsync()
    {
        var json = JsonSerializer.Serialize(
            _blockedElements,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(BlockedElementsPath, json);
    }

    private async Task ApplyBlockedElementsToAllBrowsersAsync()
    {
        foreach (var browser in _elementBlockBrowsers.ToArray())
        {
            await ApplyBlockedElementsAsync(browser);
        }
    }

    private async Task ApplyBlockedElementsAsync(WebView2 browser)
    {
        if (!Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var pageUri) ||
            pageUri is null)
        {
            return;
        }

        _blockedElements.TryGetValue(GetHostKey(pageUri), out var selectors);
        var css = string.Join(
            Environment.NewLine,
            (selectors ?? []).Select(selector => $"{selector} {{ display: none !important; }}"));
        var script = $$"""
            (() => {
                const id = "r34-viewer-blocked-elements";
                let style = document.getElementById(id);
                const css = {{JsonSerializer.Serialize(css)}};
                if (css === "") {
                    style?.remove();
                    return;
                }
                if (!style) {
                    style = document.createElement("style");
                    style.id = id;
                    (document.head || document.documentElement).appendChild(style);
                }
                style.textContent = css;
            })();
            """;
        await browser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static string GetHostKey(Uri uri)
    {
        return uri.DnsSafeHost.ToLowerInvariant();
    }

    private const string ElementPickerScript = """
        (token => {
            document.getElementById("r34-element-picker-overlay")?.remove();
            const overlay = document.createElement("div");
            overlay.id = "r34-element-picker-overlay";
            overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;pointer-events:none";
            const highlight = document.createElement("div");
            highlight.style.cssText = "position:fixed;border:2px solid #22c55e;background:#22c55e22;pointer-events:none";
            const label = document.createElement("div");
            label.textContent = "Click an element to block it · Esc to cancel";
            label.style.cssText = "position:fixed;top:12px;left:50%;transform:translateX(-50%);padding:8px 12px;background:#111827;color:white;font:14px sans-serif;border:1px solid #22c55e;border-radius:4px";
            overlay.append(highlight, label);
            document.documentElement.appendChild(overlay);

            const cleanup = () => {
                document.removeEventListener("mousemove", onMove, true);
                document.removeEventListener("click", onClick, true);
                document.removeEventListener("keydown", onKey, true);
                overlay.remove();
            };
            const onMove = event => {
                const element = document.elementFromPoint(event.clientX, event.clientY);
                if (!element || overlay.contains(element) || element === document.body || element === document.documentElement) return;
                const rect = element.getBoundingClientRect();
                highlight.style.left = `${rect.left}px`;
                highlight.style.top = `${rect.top}px`;
                highlight.style.width = `${rect.width}px`;
                highlight.style.height = `${rect.height}px`;
            };
            const selectorFor = element => {
                const parts = [];
                let current = element;
                while (current && current.nodeType === Node.ELEMENT_NODE && current !== document.documentElement) {
                    let part = current.localName;
                    if (current.id) {
                        part += `#${CSS.escape(current.id)}`;
                    } else {
                        const classes = Array.from(current.classList).slice(0, 3);
                        if (classes.length) part += classes.map(name => `.${CSS.escape(name)}`).join("");
                        const siblings = Array.from(current.parentElement?.children || []).filter(node => node.localName === current.localName);
                        if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
                    }
                    parts.unshift(part);
                    const candidate = parts.join(" > ");
                    try {
                        if (document.querySelectorAll(candidate).length === 1) return candidate;
                    } catch {}
                    current = current.parentElement;
                }
                return parts.join(" > ");
            };
            const onClick = event => {
                const element = document.elementFromPoint(event.clientX, event.clientY);
                if (!element || overlay.contains(element) || element === document.body || element === document.documentElement) return;
                event.preventDefault();
                event.stopImmediatePropagation();
                const selector = selectorFor(element);
                cleanup();
                if (selector) window.chrome?.webview?.postMessage({type: "blockElement", token, selector});
            };
            const onKey = event => {
                if (event.key !== "Escape") return;
                event.preventDefault();
                event.stopImmediatePropagation();
                cleanup();
            };
            document.addEventListener("mousemove", onMove, true);
            document.addEventListener("click", onClick, true);
            document.addEventListener("keydown", onKey, true);
        })
        """;

    private void ConfigureNewWindowHandling(WebView2 browser, TextBlock? title = null, TabItem? tab = null)
    {
        browser.CoreWebView2.NewWindowRequested += async (_, args) =>
        {
            args.Handled = true;
            await OpenInAppTabAsync(args.Uri);
        };

        if (tab is not null)
        {
            browser.CoreWebView2.NavigationStarting += (_, args) => tab.ToolTip = args.Uri;
        }

        if (title is not null)
        {
            browser.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                title.Text = string.IsNullOrWhiteSpace(browser.CoreWebView2.DocumentTitle)
                    ? browser.Source?.Host ?? "New Tab"
                    : browser.CoreWebView2.DocumentTitle;
            };
        }
    }

    private static void ConfigureNavigationButtons(WebView2 browser, Button backButton, Button forwardButton)
    {
        void UpdateButtons()
        {
            backButton.IsEnabled = browser.CoreWebView2?.CanGoBack == true;
            forwardButton.IsEnabled = browser.CoreWebView2?.CanGoForward == true;
        }

        backButton.Click += (_, _) =>
        {
            if (browser.CoreWebView2?.CanGoBack == true)
            {
                browser.CoreWebView2.GoBack();
            }
        };
        forwardButton.Click += (_, _) =>
        {
            if (browser.CoreWebView2?.CanGoForward == true)
            {
                browser.CoreWebView2.GoForward();
            }
        };
        browser.CoreWebView2.HistoryChanged += (_, _) => UpdateButtons();
        browser.CoreWebView2.NavigationCompleted += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    private async Task OpenInAppTabAsync(string link)
    {
        if (_environment is null ||
            !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var browser = new WebView2();
        var title = new TextBlock
        {
            Text = uri.Host,
            Foreground = System.Windows.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var closeButton = new Button
        {
            Content = "x",
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var bookmarkButton = new Button
        {
            Content = "🔖",
            ToolTip = "Save URL to Links.txt",
            IsEnabled = false,
            Margin = new Thickness(2)
        };
        var backButton = new Button
        {
            Content = "←",
            ToolTip = "Back",
            IsEnabled = false,
            Margin = new Thickness(2),
            Width = 34,
            Height = 30,
            Padding = new Thickness(0)
        };
        var forwardButton = new Button
        {
            Content = "→",
            ToolTip = "Forward",
            IsEnabled = false,
            Margin = new Thickness(2),
            Width = 34,
            Height = 30,
            Padding = new Thickness(0)
        };
        var navigationToolbar = new Grid
        {
            Margin = new Thickness(4)
        };
        navigationToolbar.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navigationToolbar.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        var navigationButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        navigationButtons.Children.Add(backButton);
        navigationButtons.Children.Add(forwardButton);
        navigationToolbar.Children.Add(navigationButtons);
        bookmarkButton.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(bookmarkButton, 1);
        navigationToolbar.Children.Add(bookmarkButton);
        var tabContent = new DockPanel();
        var toolbarBorder = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(17, 24, 39)),
            Child = navigationToolbar
        };
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        tabContent.Children.Add(toolbarBorder);
        tabContent.Children.Add(browser);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(closeButton);

        var tab = new TabItem { Header = header, Content = tabContent, ToolTip = link };
        closeButton.Click += (_, _) =>
        {
            AppTabs.Items.Remove(tab);
            _bookmarkControls.RemoveAll(control => ReferenceEquals(control.Browser, browser));
            _elementBlockBrowsers.Remove(browser);
            _elementPickerSessions.Remove(browser);
        };
        bookmarkButton.Click += Bookmark_Click;
        AppTabs.Items.Add(tab);
        AppTabs.SelectedItem = tab;

        try
        {
            await browser.EnsureCoreWebView2Async(_environment);
            ConfigureNewWindowHandling(browser, title, tab);
            ConfigureNavigationButtons(browser, backButton, forwardButton);
            ConfigureBookmarkButton(browser, bookmarkButton);
            ConfigureElementBlocking(browser);
            browser.Source = uri;
        }
        catch (Exception ex)
        {
            AppTabs.Items.Remove(tab);
            MessageBox.Show(
                this,
                $"Could not open the page in a new app tab.\n\n{ex.Message}",
                "TabScatter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

}
