namespace SaruPdf.Views;

// Internal PDF links move the renderer viewport without a browser navigation.
// Record only explicit page/destination jumps, never wheel/scrollbar movement.
internal static class PdfLocationHistory
{
    internal const string Install = """
        (() => {
            const v = window.viewer?.viewport_;
            if (!v?.documentDimensions_) return false;
            if (v.__saruLocations) return true;
            const read = () => {
                const Page = v.getMostVisiblePage(), r = v.getPageScreenRect(Page);
                return {Page, Width: r.width * devicePixelRatio,
                    CenterX: (v.size.width / 2 - r.x) / r.width,
                    CenterY: (v.size.height / 2 - r.y) / r.height};
            };
            const same = (a, b) => a.Page === b.Page && Math.abs(a.Width - b.Width) < 2 &&
                Math.abs(a.CenterX - b.CenterX) * a.Width < 2 && Math.abs(a.CenterY - b.CenterY) * a.Width < 2;
            const h = {stack: [], depth: 0, read, same, sequence: 0, timer: 0};
            const center = state => ({Page: state.Page, X: state.CenterX, Y: state.CenterY, Normalized: true});
            h.pulse = marker => {
                try {
                    h.pulseElement?.remove();
                    if (!marker) marker = center(read());
                    const r = v.getPageScreenRect(marker.Page);
                    const x = r.x + marker.X * (marker.Normalized ? r.width : v.getZoom());
                    const y = r.y + marker.Y * (marker.Normalized ? r.height : v.getZoom());
                    if (!Number.isFinite(x) || !Number.isFinite(y)) return;
                    const ring = document.createElement('div');
                    ring.setAttribute('data-sarupdf-location-pulse', String(marker.Page));
                    ring.setAttribute('aria-hidden', 'true');
                    ring.style.cssText = 'position:fixed;width:36px;height:36px;box-sizing:border-box;border:3px solid rgb(235,35,35);border-radius:50%;pointer-events:none;z-index:2147483647;background:transparent;';
                    // A destination is often at the very top of the viewport.
                    // Keep the complete ring visible without moving the PDF.
                    ring.style.left = `${Math.max(22, Math.min(innerWidth - 22, x)) - 18}px`;
                    ring.style.top = `${Math.max(22, Math.min(innerHeight - 22, y)) - 18}px`;
                    document.documentElement.appendChild(ring);
                    h.pulseElement = ring;
                    ring.animate([{opacity: 220/255}, {opacity: 0}], {duration: 900}).finished
                        .then(() => ring.remove(), () => ring.remove());
                } catch { /* A visual cue must never block PDF navigation. */ }
            };
            h.schedule = (marker, before) => {
                h.sequence++;
                clearTimeout(h.timer);
                h.timer = setTimeout(() => {
                    h.timer = 0;
                    try { if (before && !same(before, read())) h.pulse(marker); } catch { }
                }, 120);
            };
            const rememberPointer = event => {
                h.pointer = {x: event.clientX, y: event.clientY, time: performance.now()};
                h.pulseElement?.remove();
            };
            let installed = 0;
            for (const [owner, name] of [[window.viewer, 'handleNavigateToDestination_'],
                [v, 'handleNavigateToDestination'], [window.viewer, 'handleNavigate']]) {
                const original = owner?.[name];
                if (typeof original !== 'function') continue;
                owner[name] = function(...args) {
                    const outer = h.depth++ === 0;
                    let before;
                    const sequence = h.sequence;
                    try {
                        try {
                            before = read();
                            if (outer) {
                                const state = before, last = h.stack.at(-1), p = h.pointer;
                                state.Marker = center(state);
                                if (p && performance.now() - p.time < 1500) {
                                    const r = v.getPageScreenRect(state.Page);
                                    state.Marker = {Page: state.Page, X: (p.x-r.x)/r.width, Y: (p.y-r.y)/r.height, Normalized: true};
                                }
                                if (state.Width > 0 && (!last || !same(last, state))) {
                                    h.stack.push(state);
                                    if (h.stack.length > 100) h.stack.shift();
                                } else if (last) last.Marker = state.Marker;
                            }
                        } catch { /* Tracking must never block the PDF's own navigation. */ }
                        const result = original.apply(this, args);
                        if (name !== 'handleNavigate') {
                            h.schedule({Page: args[0], X: args[1] || 0, Y: args[2] || 0}, before);
                        } else if (h.sequence === sequence) h.schedule(null, before);
                        return result;
                    } finally { h.depth--; }
                };
                installed++;
            }
            if (!installed) return false;
            window.addEventListener('pointerdown', rememberPointer, true);
            window.addEventListener('mousedown', rememberPointer, true);
            // Edge's native PDF surface forwards pointer events through this
            // adapter instead of dispatching DOM pointerdown on the embed.
            try {
                const adapter = window.viewer.pdfAnnotationManagerWrapper_;
                const pointer = adapter?.handlePointerEventFromPlugin;
                if (typeof pointer === 'function' && typeof window.viewer.subscribePointerEventsUnseasoned_ === 'function') {
                    adapter.handlePointerEventFromPlugin = function(event) {
                        try { if (event.detail?.type === 'pointerdown') rememberPointer(event.detail); } catch { }
                        return pointer.apply(this, arguments);
                    };
                    window.viewer.subscribePointerEventsUnseasoned_();
                }
            } catch { /* DOM events or the viewport center remain available. */ }
            v.__saruLocations = h;
            return true;
        })()
        """;

    // Discard no-op destinations so a link to the current location is harmless.
    internal const string Peek = """
        (() => {
            const h = window.viewer?.viewport_?.__saruLocations;
            if (!h) return null;
            const current = h.read();
            while (h.stack.length && h.same(current, h.stack.at(-1))) h.stack.pop();
            return h.stack.at(-1) ?? null;
        })()
        """;
    internal const string Pop = """
        (() => {
            const h = window.viewer?.viewport_?.__saruLocations;
            if (!h) return null;
            const current = h.read();
            while (h.stack.length && h.same(current, h.stack.at(-1))) h.stack.pop();
            const state = h.stack.pop();
            h.returnMarker = state?.Marker;
            return state ?? null;
        })()
        """;
    internal const string PulseReturn = """
        (() => {
            const h = window.viewer?.viewport_?.__saruLocations;
            if (!h) return false;
            clearTimeout(h.timer);
            h.pulse(h.returnMarker);
            return true;
        })()
        """;
}
