// 工作台 dock 前端交互：分隔线拖拽（百分比宽度，实时写 CSS 变量，松手回传 .NET）。
(function () {
    'use strict';

    window.seeingWorkbench = window.seeingWorkbench || {};

    // 最近一次拖拽的最终宽度（按 dock 元素缓存，避免多实例互相覆盖）
    window.seeingWorkbench.initResize = function (el, dotnetRef, value, min, max, invert) {
        if (!el || el.__seeingWorkbenchResizeBound) return;
        el.__seeingWorkbenchResizeBound = true;

        var dock = el.closest ? el.closest('.workbench-dock') : null;

        // 百分比模式的基准：会话内容区宽度（与旧文件面板一致）
        function containerWidth() {
            var wrapper = el.closest ? el.closest('.session-content-wrapper') : null;
            var w = wrapper ? wrapper.clientWidth : 0;
            if (w > 0) return w;
            var parent = el.parentElement;
            w = parent ? parent.clientWidth : 0;
            return w > 0 ? w : document.documentElement.clientWidth;
        }

        function clamp(v) {
            return Math.max(min, Math.min(max, v));
        }

        function apply(v) {
            if (dock) dock.style.setProperty('--workbench-width', v + '%');
        }

        function current() {
            if (!dock) return value;
            var raw = getComputedStyle(dock).getPropertyValue('--workbench-width');
            var v = parseFloat(raw);
            return isNaN(v) ? value : v;
        }

        apply(clamp(value));

        el.addEventListener('pointerdown', function (e) {
            e.preventDefault();
            var startX = e.clientX;
            var startVal = current();

            try { el.setPointerCapture(e.pointerId); } catch (_) { /* 合成事件/不支持 */ }
            document.body.classList.add('workbench-resizing');

            function onMove(ev) {
                var deltaPx = ev.clientX - startX;
                var delta = (deltaPx / containerWidth()) * 100;
                apply(clamp(startVal + (invert ? -delta : delta)));
            }

            function onUp(ev) {
                window.removeEventListener('pointermove', onMove);
                window.removeEventListener('pointerup', onUp);
                try { el.releasePointerCapture(ev.pointerId); } catch (_) { /* noop */ }
                document.body.classList.remove('workbench-resizing');
                if (dotnetRef) {
                    dotnetRef.invokeMethodAsync('OnResizeEndAsync', current());
                }
            }

            window.addEventListener('pointermove', onMove);
            window.addEventListener('pointerup', onUp);
        });
    };
})();
