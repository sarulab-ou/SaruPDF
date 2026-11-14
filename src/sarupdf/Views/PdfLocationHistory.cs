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
            const h = {stack: [], depth: 0, read, same};
            let installed = 0;
            for (const [owner, name] of [[window.viewer, 'handleNavigateToDestination_'],
                [v, 'handleNavigateToDestination'], [window.viewer, 'handleNavigate']]) {
                const original = owner?.[name];
                if (typeof original !== 'function') continue;
                owner[name] = function(...args) {
                    const outer = h.depth++ === 0;
                    try {
                        if (outer) {
                            try {
                                const state = read(), last = h.stack.at(-1);
                                if (state.Width > 0 && (!last || !same(last, state))) {
                                    h.stack.push(state);
                                    if (h.stack.length > 100) h.stack.shift();
                                }
                            } catch { /* Tracking must never block the PDF's own navigation. */ }
                        }
                        return original.apply(this, args);
                    } finally { h.depth--; }
                };
                installed++;
            }
            if (!installed) return false;
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
            return h.stack.pop() ?? null;
        })()
        """;
}
