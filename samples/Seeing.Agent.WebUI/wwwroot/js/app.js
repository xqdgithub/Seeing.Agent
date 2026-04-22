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