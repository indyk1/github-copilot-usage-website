// Small, dependency-free helpers for the dashboard. Loaded from this site only (no CDN).
(function () {
    'use strict';

    var reduceMotion = function () {
        return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    };

    // ---- Count-up -----------------------------------------------------------------------------
    // Elements look like <span data-countup data-to="26135" data-decimals="0" data-prefix="$" data-suffix="%">26,135</span>.
    // The server renders the final text, so without script (or with reduced motion) the final figure is already there.
    // We only touch the existing text node's value so Blazor's own updates to it keep working.
    var formatters = {};
    function format(value, decimals) {
        var key = decimals;
        if (!formatters[key]) {
            formatters[key] = new Intl.NumberFormat('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
        }
        return formatters[key].format(value);
    }

    function textNode(el) {
        for (var n = el.firstChild; n; n = n.nextSibling) if (n.nodeType === 3) return n;
        return null;
    }

    function countUp(el) {
        var to = parseFloat(el.getAttribute('data-to'));
        if (!isFinite(to)) return;
        var prev = el.getAttribute('data-counted');
        el.setAttribute('data-counted', String(to));
        if (prev === String(to)) return;               // already shown this value
        var from = prev === null ? 0 : parseFloat(prev);
        if (reduceMotion() || from === to) return;
        var node = textNode(el);
        if (!node) return;
        var decimals = parseInt(el.getAttribute('data-decimals') || '0', 10);
        var prefix = el.getAttribute('data-prefix') || '';
        var suffix = el.getAttribute('data-suffix') || '';
        var finalText = node.nodeValue;
        var start = null, duration = 1100;
        function step(ts) {
            if (start === null) start = ts;
            var t = Math.min(1, (ts - start) / duration);
            var eased = 1 - Math.pow(1 - t, 3);
            node.nodeValue = t >= 1 ? finalText : prefix + format(from + (to - from) * eased, decimals) + suffix;
            if (t < 1 && node.isConnected) requestAnimationFrame(step);
            else node.nodeValue = finalText;
        }
        requestAnimationFrame(step);
    }

    function scan(root) {
        if (!root || root.nodeType !== 1) return;
        if (root.hasAttribute && root.hasAttribute('data-countup')) countUp(root);
        var list = root.querySelectorAll ? root.querySelectorAll('[data-countup]') : [];
        for (var i = 0; i < list.length; i++) countUp(list[i]);
    }

    function start() {
        scan(document.body);
        new MutationObserver(function (records) {
            for (var i = 0; i < records.length; i++) {
                var r = records[i];
                if (r.type === 'attributes') scan(r.target);
                else for (var j = 0; j < r.addedNodes.length; j++) scan(r.addedNodes[j]);
            }
        }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-to'] });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();

    // ---- Element width for the charts ----------------------------------------------------------
    // The chart draws in real pixels so its text stays 12px on a phone instead of scaling down with the SVG.
    var observers = new WeakMap();
    window.glass = {
        observeWidth: function (el, dotnet) {
            if (!el || typeof ResizeObserver === 'undefined') return;
            var last = -1;
            var ro = new ResizeObserver(function (entries) {
                var w = Math.round(entries[0].contentRect.width);
                if (w > 0 && Math.abs(w - last) >= 1) {
                    last = w;
                    dotnet.invokeMethodAsync('OnWidth', w).catch(function () { });
                }
            });
            ro.observe(el);
            observers.set(el, ro);
        },
        unobserveWidth: function (el) {
            var ro = el && observers.get(el);
            if (ro) { ro.disconnect(); observers.delete(el); }
        }
    };
})();
