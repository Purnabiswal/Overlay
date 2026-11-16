using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyOverlay;

public class ChatInjectionResult
{
    public bool Success { get; set; }
    public string? Site { get; set; }
    public string? Reason { get; set; }
    public string? Url { get; set; }
    public int AppendedLength { get; set; }
}

/// <summary>
/// Handles intelligent, non-destructive text insertion into ChatGPT, Gemini, and other AI chat inputs in WebView2.
/// Preserves existing user-typed text, dispatches framework input events (ProseMirror, Quill, React),
/// and strictly avoids automatically clicking Send.
/// </summary>
public static class ChatInputInjector
{
    public static async Task<ChatInjectionResult> InjectTextAsync(BrowserPanel browserPanel, string text)
    {
        if (browserPanel == null)
        {
            return new ChatInjectionResult
            {
                Success = false,
                Reason = "BrowserPanel is null."
            };
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChatInjectionResult
            {
                Success = true,
                Reason = "Empty text; nothing to insert."
            };
        }

        // 1. Prioritize currently active tab
        BrowserTab? targetTab = browserPanel.ActiveTab;

        // 2. If active tab is not an AI chat site, see if another open tab is ChatGPT or Gemini
        if (targetTab == null || !IsAiSiteUrl(targetTab.CurrentUrl))
        {
            var openAiTab = browserPanel.Tabs.FirstOrDefault(t => IsAiSiteUrl(t.CurrentUrl));
            if (openAiTab != null)
            {
                targetTab = openAiTab;
            }
        }

        if (targetTab == null)
        {
            return new ChatInjectionResult
            {
                Success = false,
                Reason = "No browser tabs are currently open."
            };
        }

        if (targetTab.WebView.CoreWebView2 == null)
        {
            return new ChatInjectionResult
            {
                Success = false,
                Reason = "Active browser tab WebView2 is not yet initialized.",
                Url = targetTab.CurrentUrl
            };
        }

        string jsonEncodedText = JsonSerializer.Serialize(text);
        string script = GetInjectionScript(jsonEncodedText);

        try
        {
            string rawJsonResult = await targetTab.WebView.CoreWebView2.ExecuteScriptAsync(script);

            if (string.IsNullOrWhiteSpace(rawJsonResult) || rawJsonResult.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatInjectionResult
                {
                    Success = false,
                    Reason = "Script execution returned null.",
                    Url = targetTab.CurrentUrl
                };
            }

            // WebView2 ExecuteScriptAsync returns JSON string (often double-encoded JSON string)
            string unescaped = rawJsonResult;
            if (rawJsonResult.StartsWith("\"") && rawJsonResult.EndsWith("\""))
            {
                try
                {
                    unescaped = JsonSerializer.Deserialize<string>(rawJsonResult) ?? rawJsonResult;
                }
                catch { }
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ChatInjectionResult>(unescaped, options);

            return result ?? new ChatInjectionResult
            {
                Success = false,
                Reason = "Failed to deserialize injection response.",
                Url = targetTab.CurrentUrl
            };
        }
        catch (Exception ex)
        {
            return new ChatInjectionResult
            {
                Success = false,
                Reason = $"WebView2 script execution error: {ex.Message}",
                Url = targetTab.CurrentUrl
            };
        }
    }

    public static bool IsAiSiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("chat.openai.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("gemini.google.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("claude.ai", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("copilot.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInjectionScript(string jsonEncodedText)
    {
        return @"
(function() {
    const textToInsert = " + jsonEncodedText + @";

    function isVisible(el) {
        if (!el) return false;
        try {
            const rect = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
        } catch(e) {
            return false;
        }
    }

    function findChatInput() {
        // 1. ChatGPT primary targets (ChatGPT ProseMirror div or textarea)
        const chatGpt1 = document.querySelector('#prompt-textarea');
        if (chatGpt1 && isVisible(chatGpt1)) return { el: chatGpt1, site: 'ChatGPT' };

        const chatGpt2 = document.querySelector('div[contenteditable=""true""].ProseMirror');
        if (chatGpt2 && isVisible(chatGpt2)) return { el: chatGpt2, site: 'ChatGPT' };

        // 2. Gemini primary targets (Gemini Quill editor inside rich-textarea)
        const gemini1 = document.querySelector('rich-textarea div.ql-editor[contenteditable=""true""]');
        if (gemini1 && isVisible(gemini1)) return { el: gemini1, site: 'Gemini' };

        const gemini2 = document.querySelector('div.ql-editor[contenteditable=""true""]');
        if (gemini2 && isVisible(gemini2)) return { el: gemini2, site: 'Gemini' };

        const gemini3 = document.querySelector('rich-textarea [contenteditable=""true""]');
        if (gemini3 && isVisible(gemini3)) return { el: gemini3, site: 'Gemini' };

        // 3. Common AI chat input patterns (Claude, Copilot, Mistral, generic)
        const common1 = document.querySelector('div[contenteditable=""true""][role=""textbox""]');
        if (common1 && isVisible(common1)) return { el: common1, site: 'AI Chat' };

        const common2 = document.querySelector('textarea[placeholder*=""Ask"" i], textarea[placeholder*=""Message"" i], textarea[placeholder*=""Type"" i]');
        if (common2 && isVisible(common2)) return { el: common2, site: 'AI Chat' };

        // 4. Currently focused element
        if (document.activeElement && (
            document.activeElement.tagName === 'TEXTAREA' ||
            document.activeElement.getAttribute('contenteditable') === 'true' ||
            (document.activeElement.tagName === 'INPUT' && document.activeElement.type === 'text')
        )) {
            return { el: document.activeElement, site: 'ActiveElement' };
        }

        // 5. Any visible textarea or contenteditable on page
        const anyTextarea = Array.from(document.querySelectorAll('textarea')).find(isVisible);
        if (anyTextarea) return { el: anyTextarea, site: 'Textarea' };

        const anyContentEditable = Array.from(document.querySelectorAll('[contenteditable=""true""]')).find(isVisible);
        if (anyContentEditable) return { el: anyContentEditable, site: 'ContentEditable' };

        return null;
    }

    const target = findChatInput();
    if (!target || !target.el) {
        return JSON.stringify({
            success: false,
            reason: 'Could not find message input element on current tab. Supported: ChatGPT, Gemini, or standard text inputs.',
            url: window.location.href,
            site: 'Unknown'
        });
    }

    const el = target.el;
    try {
        el.focus();
    } catch(e) {}

    // Determine leading whitespace separator if existing text is present
    const existing = (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT')
        ? (el.value || '')
        : (el.innerText || el.textContent || '');

    let textToAppend = textToInsert;
    if (existing && existing.trim().length > 0) {
        const lastChar = existing.slice(-1);
        if (lastChar !== ' ' && lastChar !== '\n' && lastChar !== '\r') {
            textToAppend = ' ' + textToAppend;
        }
    }

    let inserted = false;

    // Case A: Standard TEXTAREA or INPUT
    if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
        const start = el.selectionStart;
        const end = el.selectionEnd;
        if (typeof start === 'number' && typeof end === 'number' && start === end && start === el.value.length) {
            el.value += textToAppend;
        } else if (typeof el.setRangeText === 'function') {
            el.setRangeText(textToAppend, el.selectionEnd, el.selectionEnd, 'end');
        } else {
            el.value += textToAppend;
        }
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        inserted = true;
    }
    // Case B: ContentEditable element (ChatGPT ProseMirror, Gemini Quill, etc.)
    else if (el.isContentEditable || el.getAttribute('contenteditable') === 'true') {
        try {
            // Move cursor/caret to the end of content
            const sel = window.getSelection();
            if (sel) {
                const range = document.createRange();
                range.selectNodeContents(el);
                range.collapse(false);
                sel.removeAllRanges();
                sel.addRange(range);
            }

            // Chromium execCommand triggers proper input transactions in Quill and ProseMirror
            inserted = document.execCommand('insertText', false, textToAppend);
        } catch(e) {
            inserted = false;
        }

        // Fallback DOM append if execCommand did not handle it
        if (!inserted) {
            const lastP = el.querySelector('p:last-of-type') || el;
            lastP.appendChild(document.createTextNode(textToAppend));
            try {
                el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: textToAppend }));
            } catch(e) {}
            inserted = true;
        }

        // Fire standard DOM input and change events to notify framework state
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
    }

    return JSON.stringify({
        success: inserted,
        site: target.site,
        appendedLength: textToAppend.length,
        url: window.location.href
    });
})();
";
    }
}
