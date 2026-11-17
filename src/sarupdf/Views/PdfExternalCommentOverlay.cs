namespace SaruPdf.Views;

internal static class PdfExternalCommentOverlay
{
    internal static string Install(SaruPdf.Documents.PdfExternalCommentLabels.SvgLabel[] labels) => $$"""
        (() => {
            const v = window.viewer?.viewport_;
            if (!v?.documentDimensions_) return false;
            document.querySelector('[data-sarupdf-external-comments]')?.remove();
            const root = document.createElement('div'), pages = new Map();
            root.setAttribute('data-sarupdf-external-comments', '');
            root.setAttribute('aria-hidden', 'true');
            root.style.cssText = 'position:fixed;inset:0;pointer-events:none;overflow:hidden;z-index:2147483600;';
            const ns = 'http://www.w3.org/2000/svg';
            for (const s of {{System.Text.Json.JsonSerializer.Serialize(labels)}}) {
                let svg = pages.get(s.Page);
                if (!svg) {
                    svg = document.createElementNS(ns, 'svg');
                    svg.setAttribute('viewBox', `0 0 ${s.Width} ${s.Height}`);
                    svg.style.cssText = 'position:absolute;pointer-events:none;';
                    pages.set(s.Page, svg); root.appendChild(svg);
                }
                const path = document.createElementNS(ns, 'path');
                path.setAttribute('d', s.Path); path.setAttribute('fill', '#ff0000'); path.setAttribute('fill-rule', 'evenodd');
                path.setAttribute('transform', `translate(${s.X} ${s.Y})`); svg.appendChild(path);
            }
            document.documentElement.appendChild(root);
            const draw = () => {
                if (!root.isConnected) return;
                for (const [page, svg] of pages) {
                    const r = v.getPageScreenRect(page);
                    const layout = `${r.x},${r.y},${r.width},${r.height}`;
                    if (svg.dataset.layout === layout) continue;
                    svg.dataset.layout = layout;
                    Object.assign(svg.style, {left:`${r.x}px`, top:`${r.y}px`, width:`${r.width}px`, height:`${r.height}px`});
                }
                requestAnimationFrame(draw);
            };
            draw(); return true;
        })()
        """;
}
