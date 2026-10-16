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

    // ===== 文件树选中节点滚动定位 =====
    // 树节点联动右侧激活文件后，将选中节点滚入可视区（nearest：就近最小滚动）。
    window.seeingFilesPanel.scrollTreeNodeIntoView = function (el) {
        if (el && el.scrollIntoView) {
            try { el.scrollIntoView({ block: 'nearest', inline: 'nearest' }); } catch (_) { /* noop */ }
        }
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

    function isFileEditorTarget(node) {
        if (!node || node.nodeType !== 1 || !node.closest) return false;
        return !!(node.closest('.file-editor-monaco') || node.closest('.monaco-editor') || node.closest('.native-edit-context'));
    }

    // Monaco 0.55 的 EditContext 焦点节点有时不在 document.activeElement 的祖先链上，
    // 需同时看事件目标，以及「DOM 落在文件编辑器里且 hasTextFocus」的实例。
    function fileEditorHasFocus(e) {
        if (isFileEditorTarget(e.target) || isFileEditorTarget(document.activeElement)) return true;
        var target = e.target;
        if (target && target.closest && target.closest('.file-panel-host') && document.querySelector('.file-editor-monaco'))
            return true;
        var list = (window.blazorMonaco && window.blazorMonaco.editors) || [];
        for (var i = 0; i < list.length; i++) {
            var ed = list[i].editor;
            if (!ed || !ed.hasTextFocus || !ed.hasTextFocus() || !ed.getDomNode) continue;
            var dom = ed.getDomNode();
            if (dom && dom.closest && dom.closest('.file-editor-monaco')) return true;
        }
        return false;
    }

    window.seeingFilesPanel.attachSaveShortcut = function (dotnetRef) {
        var token = ++saveSeq;
        var handler = function (e) {
            var key = (e.key || '').toLowerCase();
            if (!(e.ctrlKey || e.metaKey) || (key !== 's' && e.code !== 'KeyS')) return;
            if (!fileEditorHasFocus(e)) return;
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

    // ===== 编辑器命令枚举（BlazorMonaco 未暴露 getSupportedActions 的 C# API）=====
    window.seeingEditor = window.seeingEditor || {};
    // 按 id 读当前缓冲；id 对不上时退回文件面板里真正获得焦点的那一个编辑器。
    window.seeingEditor.readValue = function (editorId) {
        var list = (window.blazorMonaco && window.blazorMonaco.editors) || [];
        function valueOf(ed) {
            return ed && typeof ed.getValue === 'function' ? ed.getValue() : null;
        }
        function inFileEditor(ed) {
            if (!ed || !ed.getDomNode) return false;
            var dom = ed.getDomNode();
            return !!(dom && dom.closest && dom.closest('.file-editor-monaco'));
        }
        for (var i = 0; i < list.length; i++) {
            var focused = list[i].editor;
            if (focused && focused.hasTextFocus && focused.hasTextFocus() && inFileEditor(focused))
                return valueOf(focused);
        }
        for (var j = 0; j < list.length; j++) {
            if (list[j].id === editorId && inFileEditor(list[j].editor)) {
                var direct = valueOf(list[j].editor);
                if (direct !== null) return direct;
            }
        }
        for (var k = 0; k < list.length; k++) {
            if (inFileEditor(list[k].editor)) return valueOf(list[k].editor);
        }
        return null;
    };

    // Monaco 0.55 的内容变更事件含不可 JSON.stringify 的字段，BlazorMonaco 的回调会静默失败。
    // 这里只把 getValue() 的纯文本送回 .NET，用来点亮「未保存」。
    window.seeingEditor._watchPending = window.seeingEditor._watchPending || {};
    // 编辑器是在子组件 OnAfterRender 里才 create 的，父组件这次调用时往往还没有实例。
    // 先挂上就返回；没有就短轮询，直到实例出现再挂内容监听。
    window.seeingEditor.watchDirty = function (editorId, dotnetRef) {
        function attach() {
            var list = (window.blazorMonaco && window.blazorMonaco.editors) || [];
            for (var i = 0; i < list.length; i++) {
                var holder = list[i];
                if (holder.id !== editorId || !holder.editor) continue;
                if (holder._seeingDirty) return true;
                var ed = holder.editor;
                holder._seeingDirty = ed.onDidChangeModelContent(function () {
                    var text;
                    try { text = ed.getValue(); } catch (_) { return; }
                    dotnetRef.invokeMethodAsync('OnBufferChanged', text);
                });
                return true;
            }
            return false;
        }
        if (attach()) return true;
        if (window.seeingEditor._watchPending[editorId]) return false;
        window.seeingEditor._watchPending[editorId] = true;
        var tries = 0;
        var timer = setInterval(function () {
            tries++;
            if (attach() || tries >= 30) {
                clearInterval(timer);
                delete window.seeingEditor._watchPending[editorId];
            }
        }, 100);
        return false;
    };

    window.seeingEditor.getActions = function (editorId) {
        var holder = (window.blazorMonaco && window.blazorMonaco.editors || [])
            .find(function (h) { return h.id === editorId; });
        if (!holder || !holder.editor || typeof holder.editor.getSupportedActions !== 'function') return '[]';
        return JSON.stringify(holder.editor.getSupportedActions().map(function (a) {
            return { id: a.id, label: a.label };
        }));
    };
})();
