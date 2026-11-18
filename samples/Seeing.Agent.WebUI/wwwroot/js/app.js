/**
 * Seeing.Agent WebUI JavaScript Utilities
 */

/*
 * AntDesign Modal 打开时会调 disableBodyScroll，给 body 写
 *   position:relative; width:calc(100% - scrollbar); overflow:hidden
 * 本应用壳层已是固定视口（overflow:hidden），误判滚动条会把整页挤窄一截。
 * ant-design-blazor.js 先于本文件加载，直接把锁滚动改成空操作。
 */
(function neutralizeAntModalBodyScroll() {
    const helper = window.AntDesign?.interop?.domManipulationHelper;
    if (!helper || typeof helper.disableBodyScroll !== 'function')
        return;
    helper.disableBodyScroll = function () { };
})();

// 自动滚动到指定元素
function scrollIntoView(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'end' });
    }
}

// 聚焦指定元素
function focusElement(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.focus();
    }
}

// 复制文本到剪贴板
async function copyToClipboard(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch (err) {
        console.error('Failed to copy: ', err);
        return false;
    }
}

// 获取元素尺寸
function getElementSize(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        return {
            width: element.offsetWidth,
            height: element.offsetHeight
        };
    }
    return null;
}

// 检测暗色模式
function isDarkMode() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

// 切换主题
function toggleTheme() {
    const currentTheme = document.documentElement.getAttribute('data-theme');
    const newTheme = currentTheme === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', newTheme);
    return newTheme;
}

// 保存主题偏好
function saveThemePreference(theme) {
    localStorage.setItem('seeing-agent-theme', theme);
}

// 加载主题偏好
function loadThemePreference() {
    return localStorage.getItem('seeing-agent-theme');
}

// 初始化主题
function initializeTheme() {
    const savedTheme = loadThemePreference();
    if (savedTheme) {
        document.documentElement.setAttribute('data-theme', savedTheme);
    } else if (isDarkMode()) {
        document.documentElement.setAttribute('data-theme', 'dark');
    }
}

// 页面加载时初始化
document.addEventListener('DOMContentLoaded', function() {
    initializeTheme();
});

// ========== 文件附件处理 ==========

// 触发文件选择
function triggerFileInput(inputId) {
    const input = document.getElementById(inputId);
    if (input) {
        input.click();
    }
}

// 触发 InputFile 组件内部的文件输入（Blazor Server）
function triggerFileInputByRef(dotnetRef) {
    // 查找隐藏的 input[type="file"] 元素（通常在 InputFile 组件渲染的容器内）
    const fileInputs = document.querySelectorAll('input[type="file"][style*="display: none"]');
    if (fileInputs.length > 0) {
        // 触发最后一个（最新的）隐藏的文件输入
        fileInputs[fileInputs.length - 1].click();
    }
}

// 从剪贴板事件收集文件（含截图）
function collectClipboardFiles(data) {
    if (!data) return [];
    if (data.files && data.files.length > 0)
        return Array.from(data.files);

    const files = [];
    for (const item of data.items || []) {
        if (item.kind === 'file') {
            const file = item.getAsFile();
            if (file) files.push(file);
        }
    }
    return files;
}

// 粘贴/拖拽共用：上传文件并只把元数据回传 C#
async function uploadFiles(files, sessionId, dotNetRef) {
    if (!files || files.length === 0) return;

    const fail = async (message) => {
        try {
            await dotNetRef.invokeMethodAsync('HandleUploadFailed', message);
        } catch (err) {
            console.error('HandleUploadFailed failed', err);
        }
    };

    if (!sessionId) {
        await fail('当前会话未就绪，无法上传附件');
        return;
    }
    if (files.length > 10) {
        await fail('单次最多上传 10 个文件');
        return;
    }

    const maxImage = 10 * 1024 * 1024;
    const maxFile = 50 * 1024 * 1024;
    for (const file of files) {
        const limit = file.type && file.type.startsWith('image/') ? maxImage : maxFile;
        if (file.size > limit) {
            await fail(`文件超过大小限制：${file.name}`);
            return;
        }
    }

    const form = new FormData();
    for (const file of files)
        form.append('files', file, file.name || 'pasted');

    try {
        const response = await fetch('/api/uploads?sessionId=' + encodeURIComponent(sessionId), {
            method: 'POST',
            headers: { 'X-Seeing-Upload': '1' },
            body: form
        });

        if (!response.ok) {
            let message = `上传失败（HTTP ${response.status}）`;
            try {
                const body = await response.json();
                if (body && body.error) message = body.error;
            } catch { /* 非 JSON 错误体 */ }
            await fail(message);
            return;
        }

        const result = await response.json();
        await dotNetRef.invokeMethodAsync('HandleUploadedFiles', result.items || []);
    } catch (err) {
        await fail(`上传失败：${err}`);
    }
}

// 处理粘贴文件/截图（绑定 prompt 容器，不绑 document）
function setupPasteHandler(containerId, dotNetRef) {
    const container = document.getElementById(containerId);
    if (!container || container.__seeingPasteHandler) return;

    const handler = async (e) => {
        const files = collectClipboardFiles(e.clipboardData);
        if (files.length === 0) return; // 保留普通文本粘贴

        // Word/Excel/网页的富文本常同时带 file（内嵌图片）；不要吞掉正文。
        if (e.clipboardData && e.clipboardData.getData('text/html')) return;

        e.preventDefault();
        await uploadFiles(files, container.dataset.sessionId, dotNetRef);
    };

    container.__seeingPasteHandler = handler;
    container.addEventListener('paste', handler);
}

function disposePasteHandler(containerId) {
    const container = document.getElementById(containerId);
    if (!container || !container.__seeingPasteHandler) return;
    container.removeEventListener('paste', container.__seeingPasteHandler);
    delete container.__seeingPasteHandler;
}

// 处理拖放文件（与粘贴共用上传）
function setupDropZone(containerId, dotNetRef) {
    const container = document.getElementById(containerId);
    if (!container || container.__seeingDropHandlers) return;

    const over = (e) => {
        e.preventDefault();
        container.classList.add('drag-over');
    };
    const leave = () => container.classList.remove('drag-over');
    const drop = async (e) => {
        e.preventDefault();
        container.classList.remove('drag-over');
        const files = e.dataTransfer && e.dataTransfer.files
            ? Array.from(e.dataTransfer.files)
            : [];
        await uploadFiles(files, container.dataset.sessionId, dotNetRef);
    };

    container.__seeingDropHandlers = { over, leave, drop };
    container.addEventListener('dragover', over);
    container.addEventListener('dragleave', leave);
    container.addEventListener('drop', drop);
}

function disposeDropZone(containerId) {
    const container = document.getElementById(containerId);
    if (!container || !container.__seeingDropHandlers) return;
    const handlers = container.__seeingDropHandlers;
    container.removeEventListener('dragover', handlers.over);
    container.removeEventListener('dragleave', handlers.leave);
    container.removeEventListener('drop', handlers.drop);
    delete container.__seeingDropHandlers;
}


// ========== Textarea 自动高度调整 ==========

/**
 * 按内容重算 textarea 高度。
 * 必须先压到 0 再读 scrollHeight：用 height:auto 时，在 flex 列布局里
 * 会短暂撑满剩余空间，把高度误写成接近 max（工作区切换 / 消息流布局抖动时常见）。
 */
function autoResizeTextarea(textarea) {
    if (!textarea || textarea._seeingAutoResizing) return;

    textarea._seeingAutoResizing = true;
    try {
        textarea.style.height = '0px';
        // 与 .prompt-textarea 的 min-height / max-height 对齐
        const newHeight = Math.min(Math.max(textarea.scrollHeight, 44), 200);
        textarea.style.height = newHeight + 'px';
    } finally {
        // 同帧内的 ResizeObserver 回调忽略；下一帧再允许重入
        requestAnimationFrame(() => { textarea._seeingAutoResizing = false; });
    }
}

/** 供 Blazor 在程序化改值后显式重算（@bind 清空不派发 input）。 */
function autoResizeTextareaById(textareaId) {
    autoResizeTextarea(document.getElementById(textareaId));
}

/**
 * 绑定自动高度：input / focus / 父级宽度变化 / 程序化改 value。
 * 幂等——重复调用只重算，不叠监听。
 */
function setupTextareaAutoResize(textareaId) {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;

    if (textarea.dataset.autoResizeBound === '1') {
        autoResizeTextarea(textarea);
        return;
    }
    textarea.dataset.autoResizeBound = '1';

    autoResizeTextarea(textarea);

    textarea.addEventListener('input', () => autoResizeTextarea(textarea));
    // 点击聚焦时重测：布局变化后「一点就恢复」的用户路径
    textarea.addEventListener('focus', () => autoResizeTextarea(textarea));

    // 只观察父级宽度（工作区/侧栏切换），避免观察自身 height 写入造成循环
    const widthHost = textarea.parentElement;
    if (widthHost && typeof ResizeObserver !== 'undefined') {
        let lastWidth = widthHost.clientWidth;
        const ro = new ResizeObserver(() => {
            const width = widthHost.clientWidth;
            if (width === lastWidth) return;
            lastWidth = width;
            autoResizeTextarea(textarea);
        });
        ro.observe(widthHost);
    }

    // Blazor 改 .value 不派发 input；拦截 setter，提交清空 / 会话切换后也能收回高度
    const descriptor = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value');
    if (descriptor && descriptor.get && descriptor.set) {
        Object.defineProperty(textarea, 'value', {
            configurable: true,
            enumerable: descriptor.enumerable,
            get() { return descriptor.get.call(this); },
            set(v) {
                descriptor.set.call(this, v);
                requestAnimationFrame(() => autoResizeTextarea(textarea));
            }
        });
    }
}

// ========== 智能滚动管理 ==========

/**
 * 滚动管理器 - 支持用户滚动时暂停自动滚动
 */
class ScrollManager {
    constructor() {
        this.userScrolled = false;
        this.lastScrollTop = 0;
        this.container = null;
        this.anchorElement = null;
        this.containerId = null;
        this.anchorId = null;
        this.threshold = 100;
        this._resizeObserver = null;
        this._pinRetryTimer = null;
        this._onScroll = () => this.handleScroll();
        this._onWheel = (e) => {
            if (!this.container) return;
            // Only unpin when the user scrolls away from the bottom.
            if (e.deltaY < 0 || !this.isAtBottom()) {
                this.userScrolled = true;
                this.setBackToBottomVisible(true);
            }
        };
        this._onTouchMove = () => {
            if (!this.container) return;
            if (!this.isAtBottom()) {
                this.userScrolled = true;
                this.setBackToBottomVisible(true);
            }
        };
    }
    
    /**
     * 初始化滚动管理器
     * @param {string} containerId - 滚动容器 ID
     * @param {string} anchorId - 滚动锚点 ID
     * @param {number} threshold - 判断用户滚动的阈值（像素）
     * @param {string} [contentId] - 内容区 ID（高度变化时保持贴底）
     */
    init(containerId, anchorId, threshold = 100, contentId = null) {
        this.containerId = containerId;
        this.anchorId = anchorId;
        this.contentId = contentId;
        this.threshold = threshold;
        this.bindContainer();
        this.observeResize();
    }

    bindContainer() {
        const next = document.getElementById(this.containerId);
        if (this.container === next && next)
            return !!this.container;

        if (this.container) {
            this.container.removeEventListener('scroll', this._onScroll);
            this.container.removeEventListener('wheel', this._onWheel);
            this.container.removeEventListener('touchmove', this._onTouchMove);
        }

        this.container = next;
        this.anchorElement = this.anchorId ? document.getElementById(this.anchorId) : null;
        if (this.container) {
            this.container.addEventListener('scroll', this._onScroll, { passive: true });
            this.container.addEventListener('wheel', this._onWheel, { passive: true });
            this.container.addEventListener('touchmove', this._onTouchMove, { passive: true });
        }
        return !!this.container;
    }

    ensureContainer() {
        return this.bindContainer();
    }

    observeResize() {
        if (typeof ResizeObserver === 'undefined') return;
        this._resizeObserver?.disconnect();
        this._resizeObserver = new ResizeObserver(() => {
            // Content (markdown/tools) often grows after first paint — stay pinned if user hasn't scrolled away.
            if (!this.userScrolled)
                this.scrollToBottom(true, 'auto');
        });
        const target = (this.contentId && document.getElementById(this.contentId))
            || this.container;
        if (target)
            this._resizeObserver.observe(target);
    }
    
    /**
     * 处理滚动事件
     */
    handleScroll() {
        if (!this.container) return;
        
        const currentScrollTop = this.container.scrollTop;
        const atBottom = this.isAtBottom();
        
        this.userScrolled = !atBottom;
        this.lastScrollTop = currentScrollTop;
        this.setBackToBottomVisible(this.userScrolled);
    }
    
    /**
     * 滚动到底部（如果用户没有手动滚动）
     * @param {boolean} force - 强制滚动，忽略用户滚动标记
     * @param {ScrollBehavior} behavior - 'auto' | 'smooth'
     */
    scrollToBottom(force = false, behavior = 'smooth') {
        if (this.userScrolled && !force) return;
        if (!this.ensureContainer()) return;

        // Direct scrollTop — reliable inside nested overflow containers.
        const top = this.container.scrollHeight;
        if (behavior === 'smooth') {
            this.container.scrollTo({ top, behavior: 'smooth' });
        } else {
            this.container.scrollTop = top;
        }
    }
    
    /**
     * 强制滚动到底部并重置用户滚动标记；短延迟再补滚以覆盖异步撑高。
     */
    forceScrollToBottom() {
        this.userScrolled = false;
        this.ensureContainer();
        this.scrollToBottom(true, 'auto');
        this.setBackToBottomVisible(false);
        this.schedulePinRetries();
    }

    schedulePinRetries() {
        if (this._pinRetryTimer)
            clearTimeout(this._pinRetryTimer);

        const delays = [50, 150, 350, 700];
        let i = 0;
        const tick = () => {
            if (this.userScrolled) return;
            this.scrollToBottom(true, 'auto');
            this.setBackToBottomVisible(false);
            i++;
            if (i < delays.length)
                this._pinRetryTimer = setTimeout(tick, delays[i] - (delays[i - 1] || 0));
        };
        this._pinRetryTimer = setTimeout(tick, delays[0]);
    }

    isAtBottom() {
        if (!this.ensureContainer()) return true;
        // Not laid out yet — treat as not at bottom so C# keeps retrying.
        if (this.container.clientHeight < 8)
            return false;
        const distance = this.container.scrollHeight
            - this.container.scrollTop
            - this.container.clientHeight;
        return distance < this.threshold;
    }
    
    /**
     * 检查是否应该自动滚动
     */
    shouldAutoScroll() {
        return !this.userScrolled;
    }

    notifyContentGrew() {
        this.scrollToBottom(false, 'auto');
    }

    preserveScrollOnPrepend(previousScrollHeight) {
        if (!this.ensureContainer()) return;
        const delta = this.container.scrollHeight - previousScrollHeight;
        this.container.scrollTop += delta;
    }

    setBackToBottomVisible(visible) {
        const btn = document.getElementById('message-list-scroll-to-bottom');
        if (!btn) return;
        if (visible) btn.removeAttribute('hidden');
        else btn.setAttribute('hidden', '');
    }

    isNearTop(threshold = 80) {
        if (!this.ensureContainer()) return false;
        return this.container.scrollTop < threshold;
    }

    getScrollHeight() {
        return this.ensureContainer() ? (this.container.scrollHeight ?? 0) : 0;
    }
    
    /**
     * 销毁滚动管理器
     */
    destroy() {
        if (this._pinRetryTimer) {
            clearTimeout(this._pinRetryTimer);
            this._pinRetryTimer = null;
        }
        this._resizeObserver?.disconnect();
        this._resizeObserver = null;
        if (this.container) {
            this.container.removeEventListener('scroll', this._onScroll);
            this.container.removeEventListener('wheel', this._onWheel);
            this.container.removeEventListener('touchmove', this._onTouchMove);
        }
        this.container = null;
        this.anchorElement = null;
    }
}

// 全局滚动管理器实例
let messageListScrollManager = null;

/**
 * 初始化消息列表滚动管理器
 * @param {string} containerId - 滚动容器 ID
 * @param {string} anchorId - 滚动锚点 ID
 * @param {string} [contentId] - 内容区 ID（用于 ResizeObserver）
 * @returns {boolean} 是否找到滚动容器并完成绑定
 */
function initMessageListScroll(containerId, anchorId, contentId) {
    if (messageListScrollManager) {
        messageListScrollManager.destroy();
        messageListScrollManager = null;
    }

    const container = document.getElementById(containerId);
    if (!container) {
        return false;
    }

    messageListScrollManager = new ScrollManager();
    messageListScrollManager.init(containerId, anchorId, 100, contentId);
    return true;
}

/**
 * 智能滚动到底部（尊重用户滚动行为）
 */
function smartScrollToBottom() {
    if (messageListScrollManager) {
        messageListScrollManager.scrollToBottom();
    }
}

/**
 * 强制滚动到底部（忽略用户滚动）
 */
function forceScrollToBottom() {
    if (messageListScrollManager) {
        messageListScrollManager.forceScrollToBottom();
        return;
    }

    const container = document.getElementById('message-list-container');
    if (container) {
        container.scrollTop = container.scrollHeight;
    }
}

/**
 * bash 终端输出区：是否接近底部（用于贴底自动滚动）
 * @param {HTMLElement} el
 */
function bashTerminalIsNearBottom(el) {
    if (!el) return true;
    return el.scrollHeight - el.scrollTop - el.clientHeight < 40;
}

/**
 * bash 终端输出区贴底
 * @param {HTMLElement} el
 */
function bashTerminalStickBottom(el) {
    if (!el) return;
    el.scrollTop = el.scrollHeight;
}

// 显式挂到 window，避免缓存/作用域导致 Blazor 找不到
window.bashTerminalIsNearBottom = bashTerminalIsNearBottom;
window.bashTerminalStickBottom = bashTerminalStickBottom;

/**
 * 主动让当前焦点元素失焦。
 * 用于「hover 才显现的操作按钮」场景：点过按钮后焦点会留在按钮上，
 * 导致按钮组持续显示（粘住）。Razor 里没有直接失焦的手段，需要走这里。
 */
function blurActiveElement() {
    const active = document.activeElement;
    if (active && typeof active.blur === 'function') {
        active.blur();
    }
}

window.blurActiveElement = blurActiveElement;

/**
 * 检查是否应该自动滚动
 */
function shouldAutoScroll() {
    if (messageListScrollManager) {
        return messageListScrollManager.shouldAutoScroll();
    }
    return true;
}

/**
 * 是否已贴近底部（用于 C# 校验 force pin 是否生效）
 */
function isAtBottom() {
    if (messageListScrollManager) {
        return messageListScrollManager.isAtBottom();
    }
    const container = document.getElementById('message-list-container');
    if (!container) return true;
    return container.scrollHeight - container.scrollTop - container.clientHeight < 100;
}

/**
 * 销毁消息列表滚动管理器
 */
function destroyMessageListScroll() {
    if (messageListScrollManager) {
        messageListScrollManager.destroy();
        messageListScrollManager = null;
    }
}

function notifyContentGrew() {
    messageListScrollManager?.notifyContentGrew();
}

function preserveScrollOnPrepend(previousScrollHeight) {
    messageListScrollManager?.preserveScrollOnPrepend(previousScrollHeight);
}

function isNearTop(threshold) {
    return messageListScrollManager?.isNearTop(threshold) ?? false;
}

function getScrollHeight() {
    return messageListScrollManager?.getScrollHeight() ?? 0;
}

// ========== 思考过程折叠/展开 ==========

/**
 * 切换思考过程的展开/折叠状态
 * @param {string} reasoningId - 思考过程块的 ID
 */
function toggleReasoning(reasoningId) {
    const section = document.querySelector(`[data-reasoning-id="${reasoningId}"]`);
    if (!section) return;
    
    const isExpanded = section.classList.contains('expanded');
    const icon = section.querySelector('.reasoning-toggle-icon');
    const content = section.querySelector('.reasoning-content');
    
    if (isExpanded) {
        section.classList.remove('expanded');
        section.classList.add('collapsed');
        if (icon) icon.style.transform = 'rotate(0deg)';
        if (content) content.style.display = 'none';
    } else {
        section.classList.remove('collapsed');
        section.classList.add('expanded');
        if (icon) icon.style.transform = 'rotate(90deg)';
        if (content) content.style.display = 'block';
    }
}

/**
 * 展开思考过程
 * @param {string} reasoningId - 思考过程块的 ID
 */
function expandReasoning(reasoningId) {
    const section = document.querySelector(`[data-reasoning-id="${reasoningId}"]`);
    if (!section) return;
    
    const icon = section.querySelector('.reasoning-toggle-icon');
    const content = section.querySelector('.reasoning-content');
    
    section.classList.remove('collapsed');
    section.classList.add('expanded');
    if (icon) icon.style.transform = 'rotate(90deg)';
    if (content) content.style.display = 'block';
}

/**
 * 折叠思考过程
 * @param {string} reasoningId - 思考过程块的 ID
 */
function collapseReasoning(reasoningId) {
    const section = document.querySelector(`[data-reasoning-id="${reasoningId}"]`);
    if (!section) return;
    
    const icon = section.querySelector('.reasoning-toggle-icon');
    const content = section.querySelector('.reasoning-content');
    
    section.classList.remove('expanded');
    section.classList.add('collapsed');
    if (icon) icon.style.transform = 'rotate(0deg)';
    if (content) content.style.display = 'none';
}

// ========== 工具调用展开/收起 ==========

/**
 * 切换工具调用的展开/收起状态（向后兼容，供 HTML onclick 调用）
 * @param {string} toolCallId - 工具调用块的 ID
 */
function toggleToolCall(toolCallId) {
    const compact = document.querySelector(`[data-tool-call-id="${toolCallId}"]`);
    if (!compact) return;

    const detail = compact.nextElementSibling;
    if (!detail) return;

    const isExpanded = detail.style.display !== 'none';
    if (isExpanded) {
        detail.style.display = 'none';
    } else {
        detail.style.display = 'block';
    }
}

// ========== 命令自动完成辅助函数 ==========

/**
 * 获取光标前的文本
 * @param {string} textareaId - Textarea 元素 ID
 * @returns {string} 光标前的文本
 */
function getTextBeforeCursor(textareaId) {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return '';
    const cursorPos = textarea.selectionStart;
    return textarea.value.substring(0, cursorPos);
}

/**
 * 验证 `/` 触发是否有效（仅在行首或空白后触发）
 * @param {string} textareaId - Textarea 元素 ID
 * @returns {boolean} 是否有效触发
 */
function isSlashTriggerValid(textareaId) {
    const textBeforeCursor = getTextBeforeCursor(textareaId);
    if (textBeforeCursor.length === 0) return true; // 在开头
    const lastChar = textBeforeCursor[textBeforeCursor.length - 1];
    return lastChar === ' ' || lastChar === '\n'; // 在空格或换行后
}

/**
 * 设置命令自动完成的键盘事件处理
 * @param {string} textareaId - Textarea 元素 ID
 * @param {any} dotNetRef - .NET 引用
 */
function setupCommandAutocomplete(textareaId, dotNetRef) {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;

    // 存储引用以便后续清理
    textarea._commandAutocompleteRef = dotNetRef;

    textarea.addEventListener('keydown', function(e) {
        // 检查是否显示下拉框（通过 data 属性）
        const isOpen = textarea.dataset.commandDropdownOpen === 'true';
        if (!isOpen) return;

        // 只阻止导航键的默认行为
        switch (e.key) {
            case 'ArrowDown':
            case 'ArrowUp':
            case 'Enter':
                e.preventDefault();
                break;
            case 'Escape':
                // Escape 不阻止默认，让输入框保持焦点
                break;
        }
    });
}

/**
 * 更新命令下拉框状态
 * @param {string} textareaId - Textarea 元素 ID
 * @param {boolean} isOpen - 是否打开
 */
function setCommandDropdownState(textareaId, isOpen) {
    const textarea = document.getElementById(textareaId);
    if (textarea) {
        textarea.dataset.commandDropdownOpen = isOpen ? 'true' : 'false';
    }
}

/**
 * 滚动命令面板，使选中项可见。
 * 面板是 max-height + overflow-y 的滚动区，键盘上下移动时选中项会跑出视野。
 * block: 'nearest' —— 已可见时不动，减少闪烁。
 * @param {string} optionId - 命令项元素 ID
 */
function scrollCommandOptionIntoView(optionId) {
    const item = document.getElementById(optionId);
    if (item) {
        item.scrollIntoView({ block: 'nearest' });
    }
}

/**
 * 是否应使用移动端布局（汉堡侧栏、内容区无左边距）。
 * 含三类：移动 UA、触控小屏、以及窄视口（≤768）。
 * 窄视口必须算进去：DevTools Responsive / 桌面缩窗若不切汉堡，
 * 会留下左侧灰侧栏（含审批中心）并占走内容区 margin。
 */
function isMobileBrowser() {
    var hasTouch = navigator.maxTouchPoints > 0;
    var ua = navigator.userAgent || '';
    var isMobileUA = /Mobi|Android|iPhone|iPad|iPod/i.test(ua);
    var smallScreen = window.screen.width <= 1024 && window.screen.height <= 1024;
    var narrowViewport = window.matchMedia
        && window.matchMedia('(max-width: 768px)').matches;
    return !!(isMobileUA || (hasTouch && smallScreen) || narrowViewport);
}

/** @type {{ mql: MediaQueryList, handler: EventListener } | null} */
var _mobileLayoutSubscription = null;

/**
 * 订阅窄视口变化，回调 .NET OnMobileLayoutChanged(bool)。
 * 幂等：重复订阅会先卸旧监听。
 */
function subscribeMobileLayoutChanged(dotNetRef) {
    unsubscribeMobileLayoutChanged();
    if (!window.matchMedia || !dotNetRef) return;

    var mql = window.matchMedia('(max-width: 768px)');
    var handler = function () {
        try {
            dotNetRef.invokeMethodAsync('OnMobileLayoutChanged', isMobileBrowser());
        } catch (e) {
            // circuit 已释放
        }
    };

    if (mql.addEventListener) mql.addEventListener('change', handler);
    else mql.addListener(handler);

    _mobileLayoutSubscription = { mql: mql, handler: handler };
}

function unsubscribeMobileLayoutChanged() {
    var sub = _mobileLayoutSubscription;
    _mobileLayoutSubscription = null;
    if (!sub) return;

    if (sub.mql.removeEventListener) sub.mql.removeEventListener('change', sub.handler);
    else if (sub.mql.removeListener) sub.mql.removeListener(sub.handler);
}

/**
 * 请求当前 WebUI Host 正常退出。
 * 通过同源 POST 调用，实际关闭权限由服务端限制为本机连接。
 */
async function shutdownWebUi() {
    const response = await fetch('/api/webui/shutdown', {
        method: 'POST',
        headers: { 'Accept': 'application/json' }
    });

    if (!response.ok) {
        throw new Error(`关闭请求失败（HTTP ${response.status}）`);
    }
}