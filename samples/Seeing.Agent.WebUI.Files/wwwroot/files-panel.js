// 文件面板前端交互：分隔线拖拽（CSS 变量 + localStorage）与 Ctrl+S 保存快捷键。
(function () {
    'use strict';

    window.seeingFilesPanel = window.seeingFilesPanel || {};

    // ===== 分隔线拖拽 =====
    // 读取 data-* 属性（变量名 / 方向 / min / max / 存储键），
    // pointerdown 起拖，pointermove 实时写 CSS 变量，pointerup 写 localStorage。
    window.seeingFilesPanel.initResize = function (el) {
        if (!el || el.__seeingResizeBound) return;
        el.__seeingResizeBound = true;

        var cssVar = el.getAttribute('data-var');
        var min = parseInt(el.getAttribute('data-min') || '120', 10);
        var max = parseInt(el.getAttribute('data-max') || '1200', 10);
        var key = el.getAttribute('data-key');
        var invert = el.getAttribute('data-invert') === 'true';
        var percent = el.getAttribute('data-percent') === 'true';
        var fallback = parseInt(el.getAttribute('data-default') || String(min), 10);

        function clamp(v) {
            return Math.max(min, Math.min(max, v));
        }

        // 百分比模式：以内容包装器宽度为基准（.session-content-wrapper）；否则为像素
        function containerWidth() {
            var wrapper = el.closest ? el.closest('.session-content-wrapper') : null;
            var w = wrapper ? wrapper.clientWidth : 0;
            if (w > 0) return w;
            var parent = el.parentElement;
            w = parent ? parent.clientWidth : 0;
            return w > 0 ? w : document.documentElement.clientWidth;
        }

        function apply(v) {
            document.documentElement.style.setProperty(cssVar, percent ? v + '%' : v + 'px');
        }

        function current() {
            var raw = getComputedStyle(document.documentElement).getPropertyValue(cssVar);
            var v = parseFloat(raw);
            if (isNaN(v)) return fallback;
            if (!percent) return v;
            // 百分比模式的初值可能来自 CSS 默认（如 50%），直接采用；否则由像素换算
            return raw.indexOf('%') >= 0 ? v : (v / containerWidth()) * 100;
        }

        // 初始化：从 localStorage 恢复；缺省写入 data-default 作为变量初值
        // 校验恢复值落在 [min,max]，否则视为脏数据（如旧版像素值）回退默认
        var restored = key ? parseFloat(localStorage.getItem(key) || '') : NaN;
        if (isNaN(restored) || restored < min || restored > max) restored = NaN;
        apply(clamp(isNaN(restored) ? fallback : restored));

        el.addEventListener('pointerdown', function (e) {
            e.preventDefault();
            var startX = e.clientX;
            var startVal = current();

            try { el.setPointerCapture(e.pointerId); } catch (_) { /* 合成事件/不支持 */ }
            document.body.classList.add('file-panel-resizing');

            function onMove(ev) {
                var deltaPx = ev.clientX - startX;
                var delta = percent ? (deltaPx / containerWidth()) * 100 : deltaPx;
                var next = clamp(startVal + (invert ? -delta : delta));
                apply(next);
            }

            function onUp(ev) {
                window.removeEventListener('pointermove', onMove);
                window.removeEventListener('pointerup', onUp);
                try { el.releasePointerCapture(ev.pointerId); } catch (_) { /* noop */ }
                document.body.classList.remove('file-panel-resizing');
                if (key) {
                    localStorage.setItem(key, String(current()));
                }
            }

            window.addEventListener('pointermove', onMove);
            window.addEventListener('pointerup', onUp);
        });
    };

    // ===== 文件面板左右位置持久化 =====
    var SIDE_KEY = 'seeing-files-panel-side';

    window.seeingFilesPanel.getPanelSide = function () {
        try { return localStorage.getItem(SIDE_KEY) || ''; } catch (_) { return ''; }
    };

    window.seeingFilesPanel.setPanelSide = function (side) {
        try { localStorage.setItem(SIDE_KEY, side === 'right' ? 'right' : 'left'); } catch (_) { /* noop */ }
    };

    // ===== Ctrl+S 保存快捷键 =====
    // 仅在焦点位于 Monaco 编辑器容器内时拦截，避免影响全局；返回 token 供卸载。
    var saveHandlers = {};
    var saveSeq = 0;

    window.seeingFilesPanel.attachSaveShortcut = function (dotnetRef) {
        var token = ++saveSeq;
        var handler = function (e) {
            if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 's') return;
            var active = document.activeElement;
            if (!active || !active.closest || !active.closest('.file-editor-monaco')) return;
            e.preventDefault();
            e.stopPropagation();
            dotnetRef.invokeMethodAsync('OnSaveShortcut');
        };
        document.addEventListener('keydown', handler, true);
        saveHandlers[token] = handler;
        return token;
    };

    window.seeingFilesPanel.detachSaveShortcut = function (token) {
        var handler = saveHandlers[token];
        if (handler) {
            document.removeEventListener('keydown', handler, true);
            delete saveHandlers[token];
        }
    };
})();
