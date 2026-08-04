/**
 * Seeing.Agent WebUI JavaScript Utilities
 */

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

// 获取文件信息
function getFileInfo(file) {
    return {
        name: file.name,
        size: file.size,
        type: file.type,
        lastModified: new Date(file.lastModified).toISOString()
    };
}

// 将文件转换为 Base64
async function fileToBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = (error) => reject(error);
        reader.readAsDataURL(file);
    });
}

// 处理拖放文件
function setupDropZone(dropZoneId, onDropCallback) {
    const dropZone = document.getElementById(dropZoneId);
    if (!dropZone) return;
    
    dropZone.addEventListener('dragover', (e) => {
        e.preventDefault();
        dropZone.classList.add('drag-over');
    });
    
    dropZone.addEventListener('dragleave', () => {
        dropZone.classList.remove('drag-over');
    });
    
    dropZone.addEventListener('drop', async (e) => {
        e.preventDefault();
        dropZone.classList.remove('drag-over');
        
        const files = e.dataTransfer.files;
        if (files.length > 0 && onDropCallback) {
            const fileInfos = [];
            for (const file of files) {
                const base64 = await fileToBase64(file);
                fileInfos.push({
                    info: getFileInfo(file),
                    base64: base64
                });
            }
            onDropCallback.invokeMethodAsync('HandleFilesDrop', fileInfos);
        }
    });
}

// 处理粘贴图片
function setupPasteHandler(targetId, onPasteCallback) {
    const target = document.getElementById(targetId);
    if (!target) return;
    
    document.addEventListener('paste', async (e) => {
        const items = e.clipboardData.items;
        for (const item of items) {
            if (item.type.startsWith('image/')) {
                const file = item.getAsFile();
                if (file && onPasteCallback) {
                    const base64 = await fileToBase64(file);
                    const fileInfo = {
                        info: getFileInfo(file),
                        base64: base64
                    };
                    onPasteCallback.invokeMethodAsync('HandlePasteImage', fileInfo);
                }
            }
        }
    });
}

// ========== Textarea 自动高度调整 ==========

// 自动调整 textarea 高度
function autoResizeTextarea(textarea) {
    if (!textarea) return;
    
    // 重置高度以获取正确的 scrollHeight
    textarea.style.height = 'auto';
    
    // 设置新高度（最小 40px，最大 200px）
    const newHeight = Math.min(Math.max(textarea.scrollHeight, 40), 200);
    textarea.style.height = newHeight + 'px';
}

// 设置 textarea 自动高度调整
function setupTextareaAutoResize(textareaId) {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return;
    
    // 初始化高度
    autoResizeTextarea(textarea);
    
    // 监听输入事件
    textarea.addEventListener('input', () => {
        autoResizeTextarea(textarea);
    });
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
 * 判断是否为移动设备浏览器（非桌面端缩窄窗口）
 * 使用 navigator 属性区分，不依赖窗口宽度
 * @returns {boolean}
 */
function isMobileBrowser() {
    var hasTouch = navigator.maxTouchPoints > 0;
    var ua = navigator.userAgent;
    var isMobileUA = /Mobi|Android|iPhone|iPad|iPod/i.test(ua);
    var smallScreen = window.screen.width <= 1024 && window.screen.height <= 1024;
    return isMobileUA || (hasTouch && smallScreen);
}