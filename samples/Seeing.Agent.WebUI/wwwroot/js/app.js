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
        this.scrollTimeout = null;
        this.lastScrollTop = 0;
        this.container = null;
        this.anchorElement = null;
    }
    
    /**
     * 初始化滚动管理器
     * @param {string} containerId - 滚动容器 ID
     * @param {string} anchorId - 滚动锚点 ID
     * @param {number} threshold - 判断用户滚动的阈值（像素）
     */
    init(containerId, anchorId, threshold = 100) {
        this.container = document.getElementById(containerId);
        this.anchorElement = document.getElementById(anchorId);
        this.threshold = threshold;
        
        if (this.container) {
            // 监听用户滚动
            this.container.addEventListener('scroll', () => this.handleScroll());
            
            // 监听鼠标滚轮
            this.container.addEventListener('wheel', () => {
                this.userScrolled = true;
                this.resetScrollTimeout();
            });
            
            // 监听触摸滑动（移动端）
            this.container.addEventListener('touchmove', () => {
                this.userScrolled = true;
                this.resetScrollTimeout();
            });
        }
    }
    
    /**
     * 处理滚动事件
     */
    handleScroll() {
        if (!this.container) return;
        
        const currentScrollTop = this.container.scrollTop;
        const scrollHeight = this.container.scrollHeight;
        const clientHeight = this.container.clientHeight;
        
        // 检测是否滚动到底部
        const isAtBottom = scrollHeight - currentScrollTop - clientHeight < this.threshold;
        
        if (isAtBottom) {
            // 滚动到底部，重置用户滚动标记
            this.userScrolled = false;
        } else if (currentScrollTop < this.lastScrollTop) {
            // 向上滚动，标记为用户滚动
            this.userScrolled = true;
            this.resetScrollTimeout();
        }
        
        this.lastScrollTop = currentScrollTop;
    }
    
    /**
     * 重置滚动超时（用户停止滚动一段时间后恢复自动滚动）
     */
    resetScrollTimeout() {
        if (this.scrollTimeout) {
            clearTimeout(this.scrollTimeout);
        }
        
        // 3 秒后恢复自动滚动
        this.scrollTimeout = setTimeout(() => {
            this.userScrolled = false;
        }, 3000);
    }
    
    /**
     * 滚动到底部（如果用户没有手动滚动）
     * @param {boolean} force - 强制滚动，忽略用户滚动标记
     */
    scrollToBottom(force = false) {
        if (this.userScrolled && !force) return;
        
        if (this.anchorElement) {
            this.anchorElement.scrollIntoView({ behavior: 'smooth', block: 'end' });
        } else if (this.container) {
            this.container.scrollTo({
                top: this.container.scrollHeight,
                behavior: 'smooth'
            });
        }
    }
    
    /**
     * 强制滚动到底部并重置用户滚动标记
     */
    forceScrollToBottom() {
        this.userScrolled = false;
        this.scrollToBottom(true);
    }
    
    /**
     * 检查是否应该自动滚动
     */
    shouldAutoScroll() {
        return !this.userScrolled;
    }
    
    /**
     * 销毁滚动管理器
     */
    destroy() {
        if (this.scrollTimeout) {
            clearTimeout(this.scrollTimeout);
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
 */
function initMessageListScroll(containerId, anchorId) {
    if (messageListScrollManager) {
        messageListScrollManager.destroy();
    }
    messageListScrollManager = new ScrollManager();
    messageListScrollManager.init(containerId, anchorId);
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
    } else {
        // 回退到简单滚动
        scrollIntoView('message-list-scroll-anchor');
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
 * 销毁消息列表滚动管理器
 */
function destroyMessageListScroll() {
    if (messageListScrollManager) {
        messageListScrollManager.destroy();
        messageListScrollManager = null;
    }
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
        // 折叠
        section.classList.remove('expanded');
        section.classList.add('collapsed');
        if (icon) icon.style.transform = 'rotate(0deg)';
        if (content) content.style.display = 'none';
    } else {
        // 展开
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