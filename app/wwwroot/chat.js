// Measurement and scrolling for the chat message list.
//
// A BlazorWebView does not size itself to its content, so the panel cannot hug the message list the
// way the old CollectionView did. This module measures the rendered content and reports it back; C#
// turns that into a HeightRequest, which flows into the existing panel-resize pipeline.
//
// Auto-scroll lives here too rather than in C#: the ResizeObserver already fires on every reflow, so
// re-sticking to the bottom in that same callback is both race-free and free.

const PIN_SLACK_PX = 24;   // treat "within two lines of the bottom" as still pinned
const REPORT_EPSILON = 0.5;

let state = null;

export function init(dotNetRef, scroller, content) {
    dispose();

    state = {
        dotNetRef,
        scroller,
        content,
        pinned: true,
        lastReported: -1,
        frame: 0,
    };

    state.onScroll = () => {
        const distance = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
        state.pinned = distance < PIN_SLACK_PX;
    };
    scroller.addEventListener('scroll', state.onScroll, { passive: true });

    // Capture phase: markdown links must never navigate the webview, which would replace the whole
    // message list with the target page. C# re-validates the scheme and hands it to the OS launcher.
    state.onClick = (e) => {
        const anchor = e.target.closest && e.target.closest('a[href]');
        if (!anchor) return;
        e.preventDefault();
        e.stopPropagation();
        const href = anchor.getAttribute('href');
        if (href) state.dotNetRef.invokeMethodAsync('OnLinkClicked', href);
    };
    document.addEventListener('click', state.onClick, true);

    state.observer = new ResizeObserver(() => schedule());
    state.observer.observe(content);

    measure();
}

// Coalesce a burst of mutations (a streaming repaint can touch several nodes) into one frame.
function schedule() {
    if (!state || state.frame) return;
    state.frame = requestAnimationFrame(() => {
        state.frame = 0;
        measure();
    });
}

function measure() {
    if (!state) return;

    if (state.pinned) stick();

    const height = state.content.getBoundingClientRect().height;
    if (Math.abs(height - state.lastReported) < REPORT_EPSILON) return;

    state.lastReported = height;
    state.dotNetRef.invokeMethodAsync('OnContentHeight', height, window.devicePixelRatio);
}

function stick() {
    state.scroller.scrollTop = state.scroller.scrollHeight;
}

// Explicit jump (a message was sent, a conversation was opened). Also re-arms auto-stick, so sending
// after having scrolled up brings the user back to the live end of the conversation.
export function scrollToBottom(smooth) {
    if (!state) return;
    state.pinned = true;
    state.scroller.scrollTo({
        top: state.scroller.scrollHeight,
        behavior: smooth ? 'smooth' : 'auto',
    });
}

export function dispose() {
    if (!state) return;
    if (state.frame) cancelAnimationFrame(state.frame);
    state.observer?.disconnect();
    state.scroller.removeEventListener('scroll', state.onScroll);
    document.removeEventListener('click', state.onClick, true);
    state = null;
}
